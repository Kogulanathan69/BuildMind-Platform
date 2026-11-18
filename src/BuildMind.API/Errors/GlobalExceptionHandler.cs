using BuildMind.Application.Abstractions.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BuildMind.API.Errors;

/// <summary>
/// Translates unhandled exceptions into safe RFC 9457 responses (decision C3).
/// </summary>
/// <remarks>
/// Only real exception types are mapped: framework <see cref="BadHttpRequestException"/> (keeps its 4xx
/// status), <see cref="UnauthenticatedAccessException"/> (401) and client-aborted requests (no body).
/// Everything else — including <see cref="ArgumentException"/> and server-side
/// <see cref="OperationCanceledException"/> — is an unexpected 500. BE-M4-D05 adds
/// <see cref="DependencyFailureException"/>: timeout → 504 <c>DEPENDENCY_TIMEOUT</c>, any other kind → 503
/// <c>DEPENDENCY_UNAVAILABLE</c>, logged as a safe structured warning (dependency name, kind, status only).
/// Public responses never contain exception messages, stack traces or configuration values in any
/// environment. Outside Development the operational log records the exception type only, because
/// exception messages may carry secrets.
/// </remarks>
public sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = TraceIdentifiers.Get(httpContext);
        var method = httpContext.Request.Method;
        // Escaped form: decoded control characters (e.g. %0A) can never reach log output.
        var path = httpContext.Request.Path.ToUriComponent();
        var exceptionType = exception.GetType().FullName;

        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogClientAborted(logger, method, path, traceId);
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }

            return true;
        }

        if (httpContext.Response.HasStarted)
        {
            LogResponseStarted(logger, exceptionType, method, path, traceId);
            return false;
        }

        var (status, detail) = Classify(exception);

        if (exception is DependencyFailureException dependencyFailure)
        {
            // Expected operational failure of a private dependency: safe structured log, no exception text or URL.
            LogDependencyFailure(logger, dependencyFailure.Dependency, dependencyFailure.Kind, dependencyFailure.StatusCode, method, path, traceId);
        }
        else if (status >= StatusCodes.Status500InternalServerError)
        {
            if (environment.IsDevelopment())
            {
                LogUnhandledWithDetails(logger, exception, exceptionType, method, path, traceId);
            }
            else
            {
                LogUnhandled(logger, exceptionType, method, path, traceId);
            }
        }
        else
        {
            LogClientError(logger, exceptionType, status, method, path, traceId);
        }

        httpContext.Response.StatusCode = status;

        var written = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails { Status = status, Detail = detail },
            Exception = null,
        });

        // When content negotiation rejects a problem body, keep the safe status with an empty body.
        _ = written;
        return true;
    }

    private static (int Status, string Detail) Classify(Exception exception) => exception switch
    {
        UnauthenticatedAccessException =>
            (StatusCodes.Status401Unauthorized, "Authentication is required."),
        // BE-M4-D05: codes come from the status table (503 → DEPENDENCY_UNAVAILABLE, 504 → DEPENDENCY_TIMEOUT).
        DependencyFailureException { Kind: DependencyFailureKind.Timeout } =>
            (StatusCodes.Status504GatewayTimeout, "A required internal service did not respond in time."),
        DependencyFailureException =>
            (StatusCodes.Status503ServiceUnavailable, "A required internal service is unavailable."),
        BadHttpRequestException { StatusCode: >= 400 and < 500 } badRequest =>
            (badRequest.StatusCode, SafeClientDetail(badRequest.StatusCode)),
        _ =>
            (StatusCodes.Status500InternalServerError, ProblemDetailsDefaults.InternalErrorDetail),
    };

    private static string SafeClientDetail(int status) => status switch
    {
        StatusCodes.Status413PayloadTooLarge => "The request payload is too large.",
        StatusCodes.Status415UnsupportedMediaType => "The request content type is not supported.",
        _ => "The request is malformed or structurally invalid.",
    };

    [LoggerMessage(EventId = 1000, Level = LogLevel.Error,
        Message = "Unhandled exception {ExceptionType} while processing {Method} {Path}. TraceId {TraceId}")]
    private static partial void LogUnhandled(ILogger logger, string? exceptionType, string method, string path, string traceId);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error,
        Message = "Unhandled exception {ExceptionType} while processing {Method} {Path}. TraceId {TraceId}")]
    private static partial void LogUnhandledWithDetails(ILogger logger, Exception exception, string? exceptionType, string method, string path, string traceId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "Request {Method} {Path} rejected with status {Status} ({ExceptionType}). TraceId {TraceId}")]
    private static partial void LogClientError(ILogger logger, string? exceptionType, int status, string method, string path, string traceId);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
        Message = "Request {Method} {Path} was aborted by the client. TraceId {TraceId}")]
    private static partial void LogClientAborted(ILogger logger, string method, string path, string traceId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "Dependency {Dependency} failed ({FailureKind}, status {DependencyStatus}) while processing {Method} {Path}. TraceId {TraceId}")]
    private static partial void LogDependencyFailure(ILogger logger, string dependency, DependencyFailureKind failureKind, int? dependencyStatus, string method, string path, string traceId);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
        Message = "Exception {ExceptionType} after the response started for {Method} {Path}; no error body written. TraceId {TraceId}")]
    private static partial void LogResponseStarted(ILogger logger, string? exceptionType, string method, string path, string traceId);
}
