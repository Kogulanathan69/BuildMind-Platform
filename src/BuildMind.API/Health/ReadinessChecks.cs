using System.Text.Json;
using BuildMind.Infrastructure.Configuration;
using BuildMind.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BuildMind.API.Health;

/// <summary>
/// Readiness (BE-M4-D04, design C11b approved 2026-11-18). Liveness (<c>/health</c>) stays dependency-free.
/// MySQL is mandatory: not configured, unreachable or too slow → Unhealthy (HTTP 503). Python and RAG are
/// instance-optional: never reported Healthy until a real client and check exist (BE-M4-D05), only Degraded
/// (HTTP 200). Results carry names, statuses and fixed reason codes only, never exception text, hosts or
/// connection details. Readiness never writes to or migrates the database.
/// </summary>
public static class Readiness
{
    public const string Path = "/health/ready";
    public const string Tag = "ready";

    /// <summary>Framework-level timeout per check; the MySQL check enforces its own shorter deadline.</summary>
    public static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(5);

    public static class Reasons
    {
        public const string Reachable = "reachable";
        public const string NotConfigured = "not_configured";
        public const string Unreachable = "unreachable";
        public const string Timeout = "timeout";
        public const string Busy = "check_in_progress";
        public const string Unverified = "unverified";
        public const string Unavailable = "unavailable";
    }

    /// <summary>Explicit mapping (C11b): Healthy/Degraded → 200, Unhealthy → 503.</summary>
    public static readonly IReadOnlyDictionary<HealthStatus, int> StatusCodes = new Dictionary<HealthStatus, int>
    {
        [HealthStatus.Healthy] = Microsoft.AspNetCore.Http.StatusCodes.Status200OK,
        [HealthStatus.Degraded] = Microsoft.AspNetCore.Http.StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = Microsoft.AspNetCore.Http.StatusCodes.Status503ServiceUnavailable,
    };

    internal static HealthCheckResult Result(HealthStatus status, string reason) =>
        new(status, reason, exception: null, data: new Dictionary<string, object> { ["reason"] = reason });

    /// <summary>Writes <c>{"status":"…","checks":[{"name":"…","status":"…","reason":"…"}]}</c>.</summary>
    public static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    // A check that timed out at framework level or threw carries no reason of ours; its exception is never surfaced.
                    reason = entry.Value.Data.TryGetValue("reason", out var reason) && reason is string text
                        ? text
                        : Reasons.Unavailable,
                }),
        };

        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            context.RequestAborted);
    }
}

/// <summary>
/// Allows at most one MySQL readiness probe at a time per host, so slow or hanging connection attempts can never
/// pile up when readiness is polled faster than the database answers. Singleton.
/// </summary>
public sealed class ReadinessProbeGate
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

    /// <summary>The MySQL probe's gate.</summary>
    internal SemaphoreSlim Semaphore => For("mysql");

    /// <summary>One single-flight gate per check name (STEP 08-B: also used by the Python check).</summary>
    internal SemaphoreSlim For(string check) => gates.GetOrAdd(check, _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Mandatory MySQL readiness: opens and closes one connection through the configured DbContext (read-only; no
/// queries against feature tables, no writes, no migrations).
/// </summary>
/// <remarks>
/// The check enforces its own deadline with <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/>, so it
/// returns on time even if the driver ignores cancellation. An abandoned probe keeps the single-flight gate and its
/// DI scope until it really finishes; only then is the scope (DbContext, connection) disposed and the gate released.
/// </remarks>
public sealed class MySqlReadinessCheck : IHealthCheck
{
    internal static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IOptions<DatabaseOptions> options;
    private readonly IServiceScopeFactory scopes;
    private readonly ReadinessProbeGate gate;
    private readonly Func<IServiceProvider, CancellationToken, Task<bool>> probe;
    private readonly TimeSpan probeTimeout;

    public MySqlReadinessCheck(IOptions<DatabaseOptions> options, IServiceScopeFactory scopes, ReadinessProbeGate gate)
        : this(options, scopes, gate, CanConnectAsync, DefaultProbeTimeout)
    {
    }

    internal MySqlReadinessCheck(
        IOptions<DatabaseOptions> options,
        IServiceScopeFactory scopes,
        ReadinessProbeGate gate,
        Func<IServiceProvider, CancellationToken, Task<bool>> probe,
        TimeSpan probeTimeout)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        this.probeTimeout = probeTimeout;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return Readiness.Result(HealthStatus.Unhealthy, Readiness.Reasons.NotConfigured);
        }

        if (!await gate.Semaphore.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return Readiness.Result(HealthStatus.Unhealthy, Readiness.Reasons.Busy);
        }

        var scope = scopes.CreateAsyncScope();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(probeTimeout);

        Task<bool> probing;
        try
        {
            probing = probe(scope.ServiceProvider, deadline.Token);
        }
        catch (Exception)
        {
            await ReleaseAsync(scope, gate.Semaphore).ConfigureAwait(false);
            return Readiness.Result(HealthStatus.Unhealthy, Readiness.Reasons.Unreachable);
        }

        // Dispose the scope and release the gate only when the probe has really finished (even if abandoned).
        _ = probing.ContinueWith(
            async finished =>
            {
                _ = finished.Exception; // observe; the message is never logged or returned
                await ReleaseAsync(scope, gate.Semaphore).ConfigureAwait(false);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();

        try
        {
            var reachable = await probing.WaitAsync(probeTimeout, cancellationToken).ConfigureAwait(false);
            return Readiness.Result(
                reachable ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                reachable ? Readiness.Reasons.Reachable : Readiness.Reasons.Unreachable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller (request abort or framework timeout) cancelled the whole readiness run
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            return Readiness.Result(HealthStatus.Unhealthy, Readiness.Reasons.Timeout);
        }
        catch (Exception)
        {
            // The exception (host, user, provider text) is deliberately dropped.
            return Readiness.Result(HealthStatus.Unhealthy, Readiness.Reasons.Unreachable);
        }
    }

    /// <summary>
    /// STEP 08-C: the gate is ALWAYS released, even if disposing the probe scope throws (a failing Dispose of any
    /// scoped service previously left the single-flight gate held forever, so every later probe reported
    /// "check_in_progress"). A disposal failure is swallowed: readiness has nothing safe to report about it.
    /// </summary>
    private static async Task ReleaseAsync(AsyncServiceScope scope, SemaphoreSlim semaphore)
    {
        try
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Intentionally ignored; the gate release below must still happen.
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static Task<bool> CanConnectAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<BuildMindDbContext>().Database.CanConnectAsync(cancellationToken);
}

/// <summary>
/// Python readiness (BE-M4-D05) against the CONFIRMED contract <c>GET /health</c> →
/// <c>{"service":"buildmind-python","status":"ok"}</c>. Python is instance-optional (C11b): success → Healthy
/// (<c>reachable</c>); any failure → Degraded (<c>not_configured</c>, <c>timeout</c> or <c>unreachable</c>), never 503.
/// Bounded by its own deadline. Feature calls that need Python still fail with their own 503/504 Problem Details.
/// </summary>
public sealed class PythonReadinessCheck : IHealthCheck
{
    internal static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(3);

    private readonly Func<CancellationToken, Task> probe;
    private readonly bool enabled;
    private readonly ReadinessProbeGate gate;
    private readonly TimeSpan deadline;

    public PythonReadinessCheck(BuildMind.Infrastructure.Integration.PythonServiceClient client, ReadinessProbeGate gate)
        : this((client ?? throw new ArgumentNullException(nameof(client))).CheckHealthAsync, client.IsEnabled, gate, DefaultDeadline)
    {
    }

    internal PythonReadinessCheck(Func<CancellationToken, Task> probe, bool enabled, ReadinessProbeGate gate, TimeSpan deadline)
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        this.enabled = enabled;
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.deadline = deadline;
    }

    /// <remarks>
    /// Same protections as the MySQL check (STEP 08-B): a hard deadline via <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/>
    /// that holds even if a transport ignores cancellation, and a single-flight gate so readiness polling can never
    /// accumulate outstanding Python probes. An abandoned probe releases the gate only when it really finishes.
    /// </remarks>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            return Readiness.Result(HealthStatus.Degraded, Readiness.Reasons.NotConfigured);
        }

        var semaphore = gate.For("python");
        if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return Readiness.Result(HealthStatus.Degraded, Readiness.Reasons.Busy);
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(deadline);

        Task probing;
        try
        {
            probing = probe(bounded.Token);
        }
        catch (Exception)
        {
            semaphore.Release();
            return Readiness.Result(HealthStatus.Degraded, Readiness.Reasons.Unreachable);
        }

        _ = probing.ContinueWith(
            finished =>
            {
                _ = finished.Exception; // observe; never logged or returned
                semaphore.Release();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await probing.WaitAsync(deadline, cancellationToken).ConfigureAwait(false);
            return Readiness.Result(HealthStatus.Healthy, Readiness.Reasons.Reachable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            return Readiness.Result(HealthStatus.Degraded, Readiness.Reasons.Timeout);
        }
        catch (BuildMind.Application.Abstractions.Errors.DependencyFailureException failure)
        {
            return Readiness.Result(HealthStatus.Degraded, failure.Kind switch
            {
                BuildMind.Application.Abstractions.Errors.DependencyFailureKind.Disabled => Readiness.Reasons.NotConfigured,
                BuildMind.Application.Abstractions.Errors.DependencyFailureKind.Timeout => Readiness.Reasons.Timeout,
                _ => Readiness.Reasons.Unreachable,
            });
        }
        catch (Exception)
        {
            return Readiness.Result(HealthStatus.Degraded, Readiness.Reasons.Unreachable);
        }
    }
}

/// <summary>
/// RAG readiness. No RAG HTTP service or contract exists in the repository, so a configured RAG is
/// "unverified" and an unconfigured one "not_configured": both Degraded, never Healthy.
/// </summary>
public sealed class InternalServiceReadinessCheck(string service, IOptions<ServicesOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var configured = service switch
        {
            "python" => options.Value.Python,
            "rag" => options.Value.Rag,
            _ => throw new InvalidOperationException("Unknown internal service readiness check."),
        };

        var reason = configured.Enabled ? Readiness.Reasons.Unverified : Readiness.Reasons.NotConfigured;
        return Task.FromResult(Readiness.Result(HealthStatus.Degraded, reason));
    }
}
