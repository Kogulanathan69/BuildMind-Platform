using System.Net;
using System.Text;
using BuildMind.Infrastructure.Integration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BuildMind.Infrastructure.Tests.Support;

/// <summary>Controlled HTTP transport for internal-service tests (no network, no real Python service).</summary>
public sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int calls;

    public int Calls => Volatile.Read(ref calls);

    public List<HttpRequestMessage> Requests { get; } = [];

    public static StubHttpHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) }));

    public static StubHttpHandler Throws(Exception exception) => new((_, _) => Task.FromException<HttpResponseMessage>(exception));

    public static StubHttpHandler Status(HttpStatusCode status) => new((_, _) => Task.FromResult(new HttpResponseMessage(status)));

    /// <summary>Never answers until cancelled (honours cancellation).</summary>
    public static StubHttpHandler Hangs() => new(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new HttpResponseMessage(HttpStatusCode.OK);
    });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        lock (Requests)
        {
            Requests.Add(request);
        }

        return respond(request, cancellationToken);
    }
}

public static class StubHttpHandlerHostExtensions
{
    /// <summary>
    /// Replaces the Python client's primary transport in the API test host. The derived factory reuses the parent's
    /// configuration and log provider, so <c>factory.Logs</c> still captures everything.
    /// </summary>
    public static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithPythonTransport(
        this BuildMindApiFactory factory, StubHttpHandler handler) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<PythonServiceClient>().ConfigurePrimaryHttpMessageHandler(() => handler)));
}
