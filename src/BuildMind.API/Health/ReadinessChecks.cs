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
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
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
            await scope.DisposeAsync().ConfigureAwait(false);
            gate.Semaphore.Release();
            return Readiness.Result(HealthStatus.Unhealthy, Readiness.Reasons.Unreachable);
        }

        // Dispose the scope and release the gate only when the probe has really finished (even if abandoned).
        _ = probing.ContinueWith(
            async finished =>
            {
                _ = finished.Exception; // observe; the message is never logged or returned
                await scope.DisposeAsync().ConfigureAwait(false);
                gate.Semaphore.Release();
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

    private static Task<bool> CanConnectAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<BuildMindDbContext>().Database.CanConnectAsync(cancellationToken);
}

/// <summary>
/// Python / RAG readiness. No internal service client exists yet (BE-M4-D05, G11), so a configured service is
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
