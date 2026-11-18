using System.Data;
using System.Data.Common;
using BuildMind.API.Errors;
using BuildMind.Application.Abstractions.Errors;
using BuildMind.Infrastructure.Persistence.Connections;
using BuildMind.Infrastructure.Persistence.Errors;

namespace BuildMind.Infrastructure.Tests.Persistence;

/// <summary>
/// STEP 10-E: deterministic semantics of the open gate (no database). Every test uses its OWN gate instance, so
/// parallel tests that open connections through the process-wide default gate cannot interfere.
/// </summary>
public sealed class MySqlConnectionOpenGateTests
{
    private readonly OpenGate gate = new();
    private readonly SerializedOpenInterceptor interceptor;

    public MySqlConnectionOpenGateTests() => interceptor = new SerializedOpenInterceptor(gate);

    private SerializedOpenInterceptor Interceptor => interceptor;

    [Fact]
    public void Production_contexts_use_the_process_wide_interceptor()
    {
        Assert.IsType<SerializedOpenInterceptor>(MySqlConnectionOpenGate.Interceptor);
        Assert.Same(MySqlConnectionOpenGate.Interceptor, MySqlConnectionOpenGate.Interceptor);
    }

    [Fact]
    public async Task Parallel_opens_never_overlap()
    {
        var tracker = new ConcurrencyTracker();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            gate.OpenAsync(new FakeConnection(tracker, TimeSpan.FromMilliseconds(15)), CancellationToken.None)));

        Assert.Equal(1, tracker.Max);
        Assert.Equal(16, tracker.Completed);
        Assert.Equal(1, gate.AvailableSlots);
    }

    [Fact]
    public async Task A_failed_open_releases_the_gate_and_keeps_the_original_exception()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.OpenAsync(new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero, fail: true), CancellationToken.None));

        Assert.Equal("synthetic open failure", failure.Message);
        Assert.Equal(1, gate.AvailableSlots);
    }

    [Fact]
    public async Task Cancellation_while_waiting_does_not_consume_or_leak_a_slot()
    {
        var blocker = new FakeConnection(new ConcurrencyTracker(), TimeSpan.FromMilliseconds(300));
        var holding = gate.OpenAsync(blocker, CancellationToken.None);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.OpenAsync(new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero), cancel.Token));
        await holding;

        Assert.Equal(1, gate.AvailableSlots);
    }

    [Fact]
    public async Task Interceptor_holds_from_opening_until_opened_or_failed_exactly_once()
    {
        var opened = new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero);
        var failed = new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero);

        await Interceptor.ConnectionOpeningAsync(opened, null!, default);
        Assert.Equal(0, gate.AvailableSlots);
        await Interceptor.ConnectionOpenedAsync(opened, null!);
        await Interceptor.ConnectionOpenedAsync(opened, null!); // a second notification must not over-release
        Assert.Equal(1, gate.AvailableSlots);

        Interceptor.ConnectionOpening(failed, null!, default);
        Assert.Equal(0, gate.AvailableSlots);
        await Interceptor.ConnectionFailedAsync(failed, null!);
        Assert.Equal(1, gate.AvailableSlots);

        // A failure notification for a connection that never acquired (e.g. cancelled while waiting) is a no-op.
        Interceptor.ConnectionFailed(new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero), null!);
        Assert.Equal(1, gate.AvailableSlots);
    }

    [Fact]
    public async Task Interceptor_waiter_cancelled_before_acquiring_holds_nothing()
    {
        var holder = new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero);
        await Interceptor.ConnectionOpeningAsync(holder, null!, default);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Interceptor.ConnectionOpeningAsync(new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero), null!, default, cancel.Token));
        await Interceptor.ConnectionOpenedAsync(holder, null!);

        Assert.Equal(1, gate.AvailableSlots);
    }

    [Fact]
    public async Task Interceptor_releases_on_cancelled_open_sync_and_async()
    {
        // STEP 10-F (G1): EF Core 10.0.12 reports a cancelled open via ConnectionCanceled, never ConnectionFailed.
        var asyncOpen = new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero);
        await Interceptor.ConnectionOpeningAsync(asyncOpen, null!, default);
        Assert.Equal(0, gate.AvailableSlots);
        await Interceptor.ConnectionCanceledAsync(asyncOpen, null!);
        Assert.Equal(1, gate.AvailableSlots);

        var syncOpen = new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero);
        Interceptor.ConnectionOpening(syncOpen, null!, default);
        Interceptor.ConnectionCanceled(syncOpen, null!);
        Interceptor.ConnectionCanceled(syncOpen, null!); // repeated notification: still exactly one release
        Assert.Equal(1, gate.AvailableSlots);
        Assert.Equal(2, Interceptor.Acquisitions);
    }

    [Fact]
    public async Task Waiting_for_the_gate_is_bounded_by_the_connection_timeout_async_and_sync()
    {
        // STEP 10-F (G2): without a bound, a sync open (no token) could wait forever behind a stuck open.
        var holder = new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero);
        await Interceptor.ConnectionOpeningAsync(holder, null!, default);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var asyncWait = await Assert.ThrowsAsync<MySqlConnectionGateTimeoutException>(async () =>
            await Interceptor.ConnectionOpeningAsync(new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero, timeoutSeconds: 1), null!, default));
        var syncWait = Assert.Throws<MySqlConnectionGateTimeoutException>(() =>
            Interceptor.ConnectionOpening(new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero, timeoutSeconds: 1), null!, default));
        var directWait = await Assert.ThrowsAsync<MySqlConnectionGateTimeoutException>(() =>
            gate.OpenAsync(new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero, timeoutSeconds: 1), CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"waited {watch.ElapsedMilliseconds} ms");
        Assert.All([asyncWait, syncWait, directWait], e => Assert.Equal(TimeSpan.FromSeconds(1), e.Waited));
        Assert.Equal(0, gate.AvailableSlots); // still held by the real holder; timed-out waiters took nothing
        await Interceptor.ConnectionOpenedAsync(holder, null!);
        Assert.Equal(1, gate.AvailableSlots);
    }

    [Fact]
    public void A_connection_without_a_timeout_still_gets_a_bounded_wait()
    {
        Assert.Equal(OpenGate.FallbackWait, OpenGate.WaitLimit(new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero, timeoutSeconds: 0)));
        Assert.Equal(TimeSpan.FromSeconds(7), OpenGate.WaitLimit(new FakeConnection(new ConcurrencyTracker(), TimeSpan.Zero, timeoutSeconds: 7)));
    }

    [Fact]
    public void A_gate_timeout_is_a_classified_503_and_never_an_unexpected_500()
    {
        Assert.True(PersistenceErrorClassifier.TryClassifyDatabaseFailure(
            new MySqlConnectionGateTimeoutException(TimeSpan.FromSeconds(15)), out var failure));

        Assert.Equal(PersistenceFailureKind.ConnectionFailure, failure.Kind);
        Assert.False(failure.IsTransient); // no retry semantics are implied
        var mapping = PersistenceProblemMapping.Map(failure);
        Assert.Equal((503, SharedErrorCodes.DependencyUnavailable), (mapping.Status, mapping.Code));
    }

    private sealed class ConcurrencyTracker
    {
        private int current;
        private int max;
        private int completed;

        public int Max => Volatile.Read(ref max);

        public int Completed => Volatile.Read(ref completed);

        public void Enter()
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            while (now > (seen = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, now, seen) != seen)
            {
            }
        }

        public void Exit()
        {
            Interlocked.Decrement(ref current);
            Interlocked.Increment(ref completed);
        }
    }

    private sealed class FakeConnection(ConcurrencyTracker tracker, TimeSpan openTime, bool fail = false, int timeoutSeconds = 15) : DbConnection
    {
        public override int ConnectionTimeout => timeoutSeconds;

        private ConnectionState state = ConnectionState.Closed;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "synthetic";

        public override string DataSource => "synthetic";

        public override string ServerVersion => "0";

        public override ConnectionState State => state;

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            tracker.Enter();
            try
            {
                await Task.Delay(openTime, cancellationToken);
                if (fail)
                {
                    throw new InvalidOperationException("synthetic open failure");
                }

                state = ConnectionState.Open;
            }
            finally
            {
                tracker.Exit();
            }
        }

        public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override void Close() => state = ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
