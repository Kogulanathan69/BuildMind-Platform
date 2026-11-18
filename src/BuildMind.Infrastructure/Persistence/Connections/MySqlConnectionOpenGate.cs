using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BuildMind.Infrastructure.Persistence.Connections;

/// <summary>
/// STEP 10-E: process-wide serialization of MySQL connection OPENING. This works around a thread-safety defect in
/// the MySql.Data provider (verified in the shipped MySql.Data 26.10.0 IL, net10.0 build SHA-256
/// <c>adda88e99fcef0b5…</c>, <c>&lt;StartSSLAsync&gt;d__11.MoveNext</c>). The provider's static
/// <c>Dictionary</c> fields <c>tlsConnectionRef</c> and <c>tlsRetry</c> are mutated after the TLS handshake
/// (<c>set_Item</c>, <c>Remove</c> at IL 891–1055) <b>outside</b> its own <c>SemaphoreSlim</c> (released at IL 613).
/// Two TLS handshakes completing concurrently anywhere in the process can corrupt them. That is the CI run #55
/// failure ("Operations that change non-concurrent collections must have exclusive access" in
/// <c>Dictionary.Remove</c>). Upstream: MySQL Bug #111759 and #110717. No fixed provider version exists
/// (26.10.0 is the latest).
/// </summary>
/// <remarks>
/// <para>TLS is NOT weakened: only the opening step is serialized, so every physical connection still negotiates
/// TLS. Commands, transactions and already-open pooled connections run fully concurrently. No retry is added.</para>
/// <para>Trade-off: a burst of new physical connections opens one after another (each waiter honours its own
/// cancellation token). While MySQL is unreachable, waiters queue behind the connect timeout of the open in
/// progress. Readiness keeps its own deadline and reports <c>timeout</c> truthfully.</para>
/// <para>Remove this gate only after a provider release whose IL no longer mutates these fields outside a lock,
/// verified the same way.</para>
/// </remarks>
public static class MySqlConnectionOpenGate
{
    /// <summary>The single process-wide gate (the provider state it protects is process-wide).</summary>
    internal static readonly OpenGate Default = new();

    /// <summary>EF Core interceptor applied by <see cref="MySqlContextOptions"/> to every BuildMind context.</summary>
    public static IInterceptor Interceptor { get; } = new SerializedOpenInterceptor(Default);

    /// <summary>For code (tests, tools) that opens a <see cref="DbConnection"/> directly, without EF Core.</summary>
    public static Task OpenAsync(DbConnection connection, CancellationToken cancellationToken = default) =>
        Default.OpenAsync(connection, cancellationToken);
}

/// <summary>
/// The gate could not be acquired within the connection's own connect timeout (STEP 10-F, bounded wait). Classified
/// as a database connection failure (503 <c>DEPENDENCY_UNAVAILABLE</c>), never retried.
/// </summary>
public sealed class MySqlConnectionGateTimeoutException(TimeSpan waited)
    : TimeoutException("A database connection could not be started in time.")
{
    public TimeSpan Waited { get; } = waited;
}

/// <summary>One-at-a-time opening. Instances exist so tests can verify semantics in isolation.</summary>
internal sealed class OpenGate
{
    /// <summary>Used when a connection reports no connect timeout (0 means "infinite" in ADO.NET).</summary>
    internal static readonly TimeSpan FallbackWait = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim semaphore = new(1, 1);

    internal int AvailableSlots => semaphore.CurrentCount;

    /// <summary>
    /// STEP 10-F: waiting for the gate is bounded by the connection's own connect timeout, so the total time is at
    /// most about twice that timeout (gate wait plus the open itself), never unbounded. The caller's cancellation
    /// still applies.
    /// </summary>
    internal static TimeSpan WaitLimit(DbConnection connection) =>
        connection.ConnectionTimeout > 0 ? TimeSpan.FromSeconds(connection.ConnectionTimeout) : FallbackWait;

    internal async Task OpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await EnterAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    internal void Enter(DbConnection connection)
    {
        var limit = WaitLimit(connection);
        if (!semaphore.Wait(limit))
        {
            throw new MySqlConnectionGateTimeoutException(limit);
        }
    }

    internal async Task EnterAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var limit = WaitLimit(connection);
        if (!await semaphore.WaitAsync(limit, cancellationToken).ConfigureAwait(false))
        {
            throw new MySqlConnectionGateTimeoutException(limit);
        }
    }

    internal void Exit() => semaphore.Release();
}

/// <summary>
/// Holds the gate from <c>ConnectionOpening</c> until <c>ConnectionOpened</c>, <c>ConnectionFailed</c> or
/// <c>ConnectionCanceled</c>. The holder is tracked per connection, so a release happens exactly once and only for
/// an opener that acquired.
/// </summary>
/// <remarks>
/// Verified against the shipped EF Core Relational 10.0.12 IL (<c>RelationalConnection.OpenInternal</c> and
/// <c>&lt;OpenInternalAsync&gt;d__72</c>):
/// <list type="bullet">
/// <item>Opening goes through <c>ConnectionOpening[Async]</c>, then <c>OpenDbConnection[Async]</c> (skipped when
/// suppressed), then <c>ConnectionOpened[Async]</c>.</item>
/// <item>On an exception, the catch calls <c>IExceptionDetector.IsCancellation</c>: a cancellation goes to
/// <c>ConnectionCanceled[Async]</c>, which calls the interceptor's <c>ConnectionCanceled</c>, NOT
/// <c>ConnectionFailed</c>; any other failure goes to <c>ConnectionFailed[Async]</c>. All three exits must
/// release. Before STEP 10-F a cancelled open leaked the gate and deadlocked all later opens.</item>
/// <item>Interceptors run only when <c>ShouldLogConnectionOpen</c> is true. The per-context logger resets its
/// suppression whenever an interceptor needs the event (<c>NeedsEventData</c>), so every open is intercepted. This
/// is verified live by <c>SSL02</c>.</item>
/// </list>
/// Not covered (provider-internal, residual risk R-1): MySql.Data's <c>MySqlConnection.CancelQuery</c> opens its own
/// unpooled connection for <c>KILL QUERY</c> directly, bypassing EF Core.
/// </remarks>
internal sealed class SerializedOpenInterceptor(OpenGate gate) : DbConnectionInterceptor
{
    private static readonly object Held = new();
    private readonly ConditionalWeakTable<DbConnection, object> holders = new();

    private long acquisitions;

    /// <summary>Number of times this interceptor acquired the gate (diagnostics and tests).</summary>
    internal long Acquisitions => Interlocked.Read(ref acquisitions);

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        gate.Enter(connection);
        Mark(connection);
        return result;
    }

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        await gate.EnterAsync(connection, cancellationToken).ConfigureAwait(false);
        Mark(connection);
        return result;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Release(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(connection);
        return Task.CompletedTask;
    }

    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) => Release(connection);

    // STEP 10-F (G1): EF Core reports a cancelled open here, never through ConnectionFailed.
    public override void ConnectionCanceled(DbConnection connection, ConnectionEndEventData eventData) => Release(connection);

    public override Task ConnectionCanceledAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(connection);
        return Task.CompletedTask;
    }

    public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(connection);
        return Task.CompletedTask;
    }

    private void Mark(DbConnection connection)
    {
        Interlocked.Increment(ref acquisitions);
        if (!holders.TryAdd(connection, Held))
        {
            // Unexpected concurrent double open of one connection: never keep the gate for it.
            gate.Exit();
            throw new InvalidOperationException("The connection is already being opened.");
        }
    }

    private void Release(DbConnection connection)
    {
        if (holders.Remove(connection))
        {
            gate.Exit();
        }
    }
}
