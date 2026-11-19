using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BuildMind.Application.Abstractions.Errors;
using BuildMind.Application.Abstractions.Regulations;
using BuildMind.Infrastructure.Configuration;
using BuildMind.Infrastructure.Integration;
using BuildMind.Infrastructure.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BuildMind.Infrastructure.Tests.Integration;

/// <summary>
/// M6-review corrections (M4 + M7): strict raw-JSON validation before deserialization, duplicate-property rejection
/// on API input, token transport enforcement, and the human-reviewed constraint client (constraints contract v0)
/// validated against the SAME example the Python provider generates (rag-service/contract/constraints_response.v0.example.json).
/// </summary>
public sealed class StrictContractTests
{
    private const string Base = "http://rag.internal:8100/";
    private const string Health = "{\"service\":\"buildmind-rag\",\"status\":\"ok\",\"contract_version\":\"1\",\"corpus_loaded\":true}";
    private static readonly string Example = File.ReadAllText(
        Path.Combine(RepositoryPaths.Root, "rag-service", "contract", "constraints_response.v0.example.json"));

    private static readonly ConstraintBinding Binding = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"), 3,
        Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Guid.Parse("33333333-3333-4333-8333-333333333333"));

    private static readonly RegulatoryConstraintQuery ExampleQuery = new(
        "Synthetic North Council planning area",
        [RegulatoryConstraintKind.RearSpaceMin, RegulatoryConstraintKind.SideSpaceMin, RegulatoryConstraintKind.FloorAreaRatioMax],
        Binding);

    private static RagServiceClient Client(StubHttpHandler handler, int timeoutSeconds = 5) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new ServicesOptions { Rag = new InternalServiceOptions { Enabled = true, BaseUrl = Base, TimeoutSeconds = timeoutSeconds } }),
            new InternalServiceBulkhead());

    private static string Tamper(Action<JsonObject> change)
    {
        var node = JsonNode.Parse(Example)!.AsObject();
        change(node);
        return node.ToJsonString();
    }

    private static JsonObject Kind(JsonObject pack, int index) => pack["kinds"]![index]!.AsObject();

    // ---- strict raw JSON (all internal responses) ------------------------------------------------------------------

    [Theory]
    [InlineData("{\"service\":\"buildmind-rag\",\"service\":\"x\",\"status\":\"ok\",\"contract_version\":\"1\",\"corpus_loaded\":true}")] // duplicate
    [InlineData("{\"service\":\"buildmind-rag\",\"status\":\"ok\",\"contract_version\":\"1\",\"corpus_loaded\":true,}")]                // trailing comma
    [InlineData("{/*c*/\"service\":\"buildmind-rag\",\"status\":\"ok\",\"contract_version\":\"1\",\"corpus_loaded\":true}")]            // comment
    [InlineData("{\"service\":\"buildmind-rag\",\"status\":\"ok\",\"contract_version\":\"1\",\"corpus_loaded\":true,\"admin\":true}")]  // unknown member
    [InlineData("[{\"service\":\"buildmind-rag\"}]")]                                                                                      // non-object root
    public async Task Internal_responses_must_be_strict_json(string body)
    {
        var failure = await Assert.ThrowsAsync<DependencyFailureException>(() =>
            Client(StubHttpHandler.Json(body)).CheckHealthAsync(CancellationToken.None));

        Assert.Equal(DependencyFailureKind.MalformedResponse, failure.Kind);
    }

    [Fact]
    public void Raw_validation_detects_nested_duplicates()
    {
        Assert.True(InternalServiceClient.IsStrictJsonObject("{\"a\":{\"b\":1,\"c\":2}}"u8));
        Assert.False(InternalServiceClient.IsStrictJsonObject("{\"a\":{\"b\":1,\"b\":2}}"u8));
        Assert.False(InternalServiceClient.IsStrictJsonObject("{\"a\":1"u8));
    }

    [Fact]
    public async Task Api_rejects_duplicate_json_properties_as_malformed()
    {
        await using var factory = new BuildMindApiFactory();
        using var client = factory.CreateClient();
        using var content = new StringContent("{\"name\":\"a\",\"name\":\"b\",\"count\":1}", Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.PostAsync(SyntheticTestEndpoints.Prefix + "/json", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- token transport enforcement -------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://rag.internal:8100", false, true)]   // plaintext to a network host with a token: refused
    [InlineData("https://rag.internal:8100", false, false)] // TLS: allowed
    [InlineData("http://127.0.0.1:8100", false, false)]     // loopback: allowed
    [InlineData("http://rag.internal:8100", true, false)]   // explicit, reviewable private-network exception
    public void Tokens_never_travel_in_cleartext_over_a_network_outside_development(string url, bool allowPlaintext, bool fails)
    {
        var options = new ServicesOptions
        {
            Rag = new InternalServiceOptions { Enabled = true, BaseUrl = url, AuthToken = "synthetic-internal-token-0123456789abcdef", AllowPlaintextPrivateNetwork = allowPlaintext },
        };

        var failures = options.Validate(requireRagAuthToken: true, enforceSecureTransport: true);

        Assert.Equal(fails, failures.Any(f => f.Contains("must use https", StringComparison.Ordinal)));
        Assert.Empty(options.Validate(requireRagAuthToken: false, enforceSecureTransport: false)); // Development
    }

    // ---- constraint client (constraints contract v0) -------------------------------------------------------------

    [Fact]
    public async Task Shared_example_maps_to_a_bound_pack_with_explicit_states()
    {
        var pack = await Client(StubHttpHandler.Json(Example)).GetConstraintsAsync(ExampleQuery, CancellationToken.None);

        Assert.Equal(Binding, pack.Binding);
        Assert.False(pack.Complete);
        Assert.True(pack.Synthetic);
        Assert.Equal(
            [ConstraintKindStatus.Verified, ConstraintKindStatus.Unverified, ConstraintKindStatus.Missing],
            pack.Kinds.Select(k => k.Status));
        var rear = Assert.Single(pack.Kinds[0].Constraints);
        Assert.Equal((">=", 3.0, "m", "HUMAN_REVIEWED"), (rear.Comparator, rear.Value, rear.Unit, rear.ReviewStatus));
        Assert.Equal(64, pack.EvidencePackId.Length);
    }

    [Fact]
    public async Task Request_carries_the_exact_binding_in_snake_case()
    {
        var handler = StubHttpHandler.Json(Example);

        await Client(handler).GetConstraintsAsync(ExampleQuery, CancellationToken.None);

        var body = JsonNode.Parse(Assert.Single(handler.RequestBodies)!)!;
        Assert.Equal("33333333-3333-4333-8333-333333333333", (string?)body["binding"]!["requirement_revision_id"]);
        Assert.Equal(3, (int)body["binding"]!["project_revision"]!);
        Assert.Equal("REAR_SPACE_MIN", (string?)body["kinds"]![0]);
        Assert.Null(body["building_use"]); // omitted, not null
    }

    public static TheoryData<string> TamperedPacks => new()
    {
        Tamper(p => p["binding"]!["project_revision"] = 4),                                         // bound to another revision
        Tamper(p => p["binding"]!["land_revision_id"] = "44444444-4444-4444-8444-444444444444"),
        Tamper(p => p["jurisdiction"] = "Synthetic South Council planning area"),
        Tamper(p => p["kinds"]!.AsArray().RemoveAt(2)),                                              // kinds differ from the request
        Tamper(p => p["legal_status_verified"] = true),
        Tamper(p => p["contract_version"] = "constraints-1"),
        Tamper(p => p["overall_status"] = "COMPLETE"),                                                // inconsistent with kinds
        Tamper(p => Kind(p, 1)["status"] = "VERIFIED"),                                               // VERIFIED without a reviewed value
        Tamper(p => Kind(p, 2)["status"] = "VERIFIED"),                                               // VERIFIED with no value at all
        Tamper(p => Kind(p, 0)["constraints"]![0]!["review_status"] = "REJECTED"),                    // REJECTED must never appear
        Tamper(p => Kind(p, 0)["constraints"]![0]!["unit"] = "percent"),                              // unit/kind mismatch
        Tamper(p => Kind(p, 0)["constraints"]![0]!["comparator"] = "<="),                             // comparator/kind mismatch
        Tamper(p => Kind(p, 0)["constraints"]![0]!["source"]!["page"] = 0),
        Tamper(p => Kind(p, 0)["constraints"]![0]!["reviewer_role"] = null),                          // reviewed without reviewer
        Tamper(p => p["evidence_pack_id"] = "not-a-hash"),
        Tamper(p => p["unexpected"] = "field"),                                                       // unknown member
    };

    [Theory]
    [MemberData(nameof(TamperedPacks))]
    public async Task Packs_violating_the_contract_are_rejected(string json)
    {
        var failure = await Assert.ThrowsAsync<DependencyFailureException>(() =>
            Client(StubHttpHandler.Json(json)).GetConstraintsAsync(ExampleQuery, CancellationToken.None));

        Assert.Equal(DependencyFailureKind.MalformedResponse, failure.Kind);
    }

    public static TheoryData<RegulatoryConstraintQuery> InvalidQueries => new()
    {
        ExampleQuery with { Jurisdiction = " " },
        ExampleQuery with { Kinds = [] },
        ExampleQuery with { Kinds = [RegulatoryConstraintKind.RearSpaceMin, RegulatoryConstraintKind.RearSpaceMin] },
        ExampleQuery with { Binding = Binding with { ProjectRevision = 0 } },
        ExampleQuery with { Binding = Binding with { LandRevisionId = Guid.Empty } },
    };

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task Invalid_constraint_queries_are_rejected_before_any_request(RegulatoryConstraintQuery query)
    {
        var handler = StubHttpHandler.Json(Example);

        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).GetConstraintsAsync(query, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Unavailable_table_is_a_truthful_dependency_failure()
    {
        var failure = await Assert.ThrowsAsync<DependencyFailureException>(() =>
            Client(StubHttpHandler.Json("{\"code\":\"CONSTRAINTS_UNAVAILABLE\"}", HttpStatusCode.ServiceUnavailable))
                .GetConstraintsAsync(ExampleQuery, CancellationToken.None));

        Assert.Equal(DependencyFailureKind.Unavailable, failure.Kind);
    }

    [Fact]
    public async Task Hanging_service_times_out_within_the_configured_bound()
    {
        var watch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<DependencyFailureException>(() =>
            Client(StubHttpHandler.Hangs(), timeoutSeconds: 1).GetConstraintsAsync(ExampleQuery, CancellationToken.None));

        Assert.Equal(DependencyFailureKind.Timeout, failure.Kind);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_dependency_failure()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(StubHttpHandler.Hangs(), timeoutSeconds: 30).GetConstraintsAsync(ExampleQuery, cancel.Token));
    }

    [Fact]
    public void Constraint_port_resolves_to_the_hardened_rag_client()
    {
        using var factory = new BuildMindApiFactory("Production");
        using var scope = factory.Services.CreateScope();

        Assert.IsType<RagServiceClient>(scope.ServiceProvider.GetRequiredService<IRegulatoryConstraintSource>());
    }
}
