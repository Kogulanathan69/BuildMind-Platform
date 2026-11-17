using System.Text.Json;
using System.Text.Json.Nodes;
using BuildMind.Application.ContractTests.Integration;
using BuildMind.Application.Features.PlanEditor.Contracts;
using BuildMind.Application.Features.PlanEditor.Confirmation;
using BuildMind.Application.Features.PlanEditor.Integration;
using BuildMind.Application.Features.PlanEditor.Versioning;

var failures = new List<string>();
var passed = 0;
string geometryFixturePath;
try
{
    geometryFixturePath = FixtureLocator.ResolveGeometryFixture(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Fixture discovery failed: {exception.Message}");
    return 2;
}

Run("geometry fixture round trip", () =>
{
    var json = File.ReadAllText(geometryFixturePath);
    var geometry = JsonSerializer.Deserialize<PlanGeometryDto>(json, PlanEditorJson.StrictOptions) ?? throw new Exception("Deserialization returned null.");
    PlanEditorContractValidator.Validate(geometry);
    var roundTrip = JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions);
    Assert(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(roundTrip)), "Round trip lost geometry data.");
});

Run("missing required member", () =>
{
    const string invalid = "{\"schemaVersion\":\"0.1-draft\"}";
    AssertThrows<JsonException>(() => JsonSerializer.Deserialize<PlanGeometryDto>(invalid));
});

Run("unknown JSON member", () =>
{
    const string invalid = "{\"schemaVersion\":\"0.1-draft\",\"unexpected\":true}";
    AssertThrows<JsonException>(() => JsonSerializer.Deserialize<PlanGeometryDto>(invalid, PlanEditorJson.StrictOptions));
});

Run("unsupported command", () =>
{
    using var payload = JsonDocument.Parse("{}");
    var request = new PlanEditRequestDto
    {
        SchemaVersion = GeometrySchemaInfo.DraftVersion,
        ProjectId = Guid.NewGuid(),
        PlanId = Guid.NewGuid(),
        BaseRevisionId = Guid.NewGuid(),
        Command = new PlanEditCommandDto
        {
            CommandId = Guid.NewGuid(),
            CommandType = "teleportRoom",
            FloorId = Guid.NewGuid(),
            TargetId = Guid.NewGuid(),
            Payload = payload.RootElement.Clone()
        }
    };
    AssertThrows<ArgumentException>(() => PlanEditorContractValidator.Validate(request));
});

Run("all eight command payloads accept Python-compatible shapes", () =>
{
    foreach (var (commandType, payloadJson) in ValidCommandPayloads())
        PlanEditorContractValidator.Validate(EditRequest(commandType, payloadJson));
});

Run("all eight command payloads reject unexpected properties", () =>
{
    foreach (var (commandType, payloadJson) in ValidCommandPayloads())
    {
        var node = JsonNode.Parse(payloadJson)!.AsObject();
        node["unexpected"] = true;
        AssertThrows<ArgumentException>(() =>
            PlanEditorContractValidator.Validate(EditRequest(commandType, node.ToJsonString())));
    }
});

Run("command-specific malformed payloads are rejected", () =>
{
    var invalid = new Dictionary<string, string>
    {
        [PlanEditCommandTypes.AddRoom] = "{}",
        [PlanEditCommandTypes.RemoveRoom] = "{\"removeOrphanOpenings\":null}",
        [PlanEditCommandTypes.MoveRoom] = "{\"deltaX\":1}",
        [PlanEditCommandTypes.ResizeRoom] = "{\"scaleX\":0,\"scaleY\":1}",
        [PlanEditCommandTypes.UpdateWall] = "{}",
        [PlanEditCommandTypes.AddOpening] = "{}",
        [PlanEditCommandTypes.MoveOpening] = "{\"offset\":-1}",
        [PlanEditCommandTypes.RemoveOpening] = "{\"value\":1}"
    };
    foreach (var item in invalid)
        AssertThrows<ArgumentException>(() =>
            PlanEditorContractValidator.Validate(EditRequest(item.Key, item.Value)));
});

Run("shared command fixture matches C# acceptance", () =>
{
    var samplePath = FixtureLocator.ResolveCommandFixture(geometryFixturePath);
    using var samples = JsonDocument.Parse(File.ReadAllText(samplePath));
    foreach (var sample in samples.RootElement.EnumerateArray())
    {
        var commandType = sample.GetProperty("commandType").GetString()!;
        PlanEditorContractValidator.Validate(EditRequest(commandType, sample.GetProperty("validPayload").GetRawText()));
        AssertThrows<ArgumentException>(() => PlanEditorContractValidator.Validate(
            EditRequest(commandType, sample.GetProperty("invalidPayload").GetRawText())));
    }
});

Run("command rejects non-finite numeric payload and missing target", () =>
{
    AssertThrows<ArgumentException>(() => PlanEditorContractValidator.Validate(
        EditRequest(PlanEditCommandTypes.MoveRoom, "{\"deltaX\":1e400,\"deltaY\":0}")));
    using var payload = JsonDocument.Parse("{\"deltaX\":1,\"deltaY\":0}");
    var request = EditRequest(PlanEditCommandTypes.MoveRoom, payload.RootElement.GetRawText());
    request = new PlanEditRequestDto
    {
        SchemaVersion = request.SchemaVersion,
        ProjectId = request.ProjectId,
        PlanId = request.PlanId,
        BaseRevisionId = request.BaseRevisionId,
        Command = new PlanEditCommandDto
        {
            CommandId = request.Command.CommandId,
            CommandType = request.Command.CommandType,
            FloorId = request.Command.FloorId,
            TargetId = null,
            Payload = request.Command.Payload
        }
    };
    AssertThrows<ArgumentException>(() => PlanEditorContractValidator.Validate(request));
});

Run("non-finite coordinate", () =>
{
    var geometry = MinimalGeometry(double.NaN);
    AssertThrows<ArgumentException>(() => PlanEditorContractValidator.Validate(geometry));
});

Run("all result statuses", () =>
{
    var statuses = new[]
    {
        PlanEditStatus.Accepted,
        PlanEditStatus.AcceptedWithWarnings,
        PlanEditStatus.Rejected,
        PlanEditStatus.RevisionConflict,
        PlanEditStatus.ValidationUnavailable
    };
    foreach (var status in statuses)
    {
        var result = new PlanEditResultDto
        {
            SchemaVersion = GeometrySchemaInfo.DraftVersion,
            CommandId = Guid.NewGuid(),
            BaseRevisionId = Guid.NewGuid(),
            Status = status,
            CandidateGeometry = status.StartsWith("accepted", StringComparison.Ordinal) ? MinimalGeometry(0) : null,
            Diagnostics = [],
            ChangedEntityIds = []
        };
        PlanEditorContractValidator.Validate(result);
    }
});

Run("immutable revision and stale-write protection", () =>
{
    var geometry = MinimalGeometry(0);
    var json = JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions);
    var hash = PlanRevisionService.ComputeGeometryHash(json);
    var validation = Validation(hash);
    var baseRevision = Revision(geometry, "sha256:base", 1);
    var store = new MemoryRevisionStore(baseRevision);
    var service = new PlanRevisionService(store, new ValidationVerifier(ValidationEvidenceStatus.Verified), TimeProvider.System);
    var command = new CreatePlanRevisionCommand(
        geometry.ProjectId, geometry.PlanId, baseRevision.RevisionId, geometry,
        validation, Guid.NewGuid(), "contract test");
    var actor = Guid.NewGuid();
    var created = service.CreateAsync(command, actor).GetAwaiter().GetResult();
    Assert(created.Status == AppendRevisionStatus.Created, "Expected a created revision.");
    Assert(created.Revision?.ParentRevisionId == baseRevision.RevisionId, "Parent revision was not preserved.");
    Assert(created.Revision?.CreatedBy == actor, "Authenticated actor was not recorded.");
    var stale = service.CreateAsync(command with { EditCommandId = Guid.NewGuid() }, actor).GetAwaiter().GetResult();
    Assert(stale.Status == AppendRevisionStatus.RevisionConflict, "Stale edit did not conflict.");
});

Run("revision fails closed when authoritative validation is unavailable", () =>
{
    var geometry = MinimalGeometry(0);
    var json = JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions);
    var hash = PlanRevisionService.ComputeGeometryHash(json);
    var baseRevision = Revision(geometry, "sha256:base", 1);
    var store = new MemoryRevisionStore(baseRevision);
    var service = new PlanRevisionService(
        store, new ValidationVerifier(ValidationEvidenceStatus.Unavailable), TimeProvider.System);
    var result = service.CreateAsync(
        new(geometry.ProjectId, geometry.PlanId, baseRevision.RevisionId, geometry,
            Validation(hash), Guid.NewGuid(), "must not persist"),
        Guid.NewGuid()).GetAwaiter().GetResult();
    Assert(result.Status == AppendRevisionStatus.ValidationUnavailable,
        "Unavailable authoritative validation did not fail closed.");
    Assert(store.GetHistoryAsync(geometry.ProjectId, geometry.PlanId, default).Result.Count == 1,
        "A revision was persisted despite unavailable validation.");
});

Run("duplicate edit command replay is rejected atomically", () =>
{
    var geometry = MinimalGeometry(0);
    var hash = PlanRevisionService.ComputeGeometryHash(JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions));
    var baseRevision = Revision(geometry, "sha256:base", 1);
    var store = new MemoryRevisionStore(baseRevision);
    var service = new PlanRevisionService(store, new ValidationVerifier(ValidationEvidenceStatus.Verified), TimeProvider.System);
    var commandId = Guid.NewGuid();
    var first = service.CreateAsync(
        new(geometry.ProjectId, geometry.PlanId, baseRevision.RevisionId, geometry, Validation(hash), commandId, "first"),
        Guid.NewGuid()).Result;
    var replay = service.CreateAsync(
        new(geometry.ProjectId, geometry.PlanId, first.Revision!.RevisionId, geometry, Validation(hash), commandId, "replay"),
        Guid.NewGuid()).Result;
    Assert(replay.Status == AppendRevisionStatus.DuplicateCommand, "Duplicate command replay was accepted.");
    Assert(store.GetHistoryAsync(geometry.ProjectId, geometry.PlanId, default).Result.Count == 2,
        "Replay changed revision history.");
});

Run("concurrent edits allow one winner", () =>
{
    var geometry = MinimalGeometry(0);
    var hash = PlanRevisionService.ComputeGeometryHash(JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions));
    var baseRevision = Revision(geometry, "sha256:base", 1);
    var store = new MemoryRevisionStore(baseRevision);
    var service = new PlanRevisionService(store, new ValidationVerifier(ValidationEvidenceStatus.Verified), TimeProvider.System);
    Task<AppendRevisionResult> Create(string summary) => service.CreateAsync(
        new(geometry.ProjectId, geometry.PlanId, baseRevision.RevisionId, geometry,
            Validation(hash), Guid.NewGuid(), summary), Guid.NewGuid());
    var results = Task.WhenAll(Create("A"), Create("B")).Result;
    Assert(results.Count(item => item.Status == AppendRevisionStatus.Created) == 1, "Expected one concurrent winner.");
    Assert(results.Count(item => item.Status == AppendRevisionStatus.RevisionConflict) == 1, "Expected one stale conflict.");
});

Run("exact revision confirmation and acknowledgement", () =>
{
    var geometry = MinimalGeometry(0);
    var json = JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions);
    var hash = PlanRevisionService.ComputeGeometryHash(json);
    var revision = Revision(geometry, hash, 2, Validation(hash));
    var store = new MemoryRevisionStore(revision);
    var transaction = new ConfirmationTransaction();
    var sink = new ConfirmationSink();
    var service = new FinalPlanConfirmationService(
        new OwnershipGate(true), store,
        new InputSource(geometry.LandRevisionId, geometry.RequirementsRevisionId),
        new FreshnessPolicy(true), transaction, sink, TimeProvider.System);
    var withoutAcknowledgement = service.ConfirmAsync(
        new(geometry.ProjectId, geometry.PlanId, revision.RevisionId, hash, false), Guid.NewGuid())
        .GetAwaiter().GetResult();
    Assert(withoutAcknowledgement.Status == ConfirmationStatus.WarningAcknowledgementRequired,
        "Unverified evidence must require acknowledgement.");
    var confirmed = service.ConfirmAsync(
        new(geometry.ProjectId, geometry.PlanId, revision.RevisionId, hash, true), Guid.NewGuid())
        .GetAwaiter().GetResult();
    Assert(confirmed.Status == ConfirmationStatus.Confirmed, "Expected exact revision confirmation.");
    Assert(sink.PublishedRevisionId == revision.RevisionId, "Report sink did not receive the exact snapshot.");
});

Run("confirmation rejects unauthenticated actor", () =>
{
    var geometry = MinimalGeometry(0);
    var json = JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions);
    var hash = PlanRevisionService.ComputeGeometryHash(json);
    var revision = Revision(geometry, hash, 1, Validation(hash));
    var service = new FinalPlanConfirmationService(
        new OwnershipGate(true), new MemoryRevisionStore(revision),
        new InputSource(geometry.LandRevisionId, geometry.RequirementsRevisionId),
        new FreshnessPolicy(true), new ConfirmationTransaction(), new ConfirmationSink(), TimeProvider.System);
    var result = service.ConfirmAsync(
        new(geometry.ProjectId, geometry.PlanId, revision.RevisionId, hash, true), Guid.Empty)
        .GetAwaiter().GetResult();
    Assert(result.Status == ConfirmationStatus.Unauthorized, "Empty authenticated identity was accepted.");
});

Run("confirmation denies cross-project ownership", () =>
{
    var geometry = MinimalGeometry(0);
    var hash = PlanRevisionService.ComputeGeometryHash(JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions));
    var revision = Revision(geometry, hash, 1, Validation(hash));
    var service = ConfirmationService(geometry, revision, new OwnershipGate(false));
    var result = service.ConfirmAsync(
        new(geometry.ProjectId, geometry.PlanId, revision.RevisionId, hash, true), Guid.NewGuid()).Result;
    Assert(result.Status == ConfirmationStatus.Unauthorized, "Cross-project access was accepted.");
});

Run("confirmation rejects expired validation", () =>
{
    var geometry = MinimalGeometry(0);
    var hash = PlanRevisionService.ComputeGeometryHash(JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions));
    var revision = Revision(geometry, hash, 1, Validation(hash));
    var service = new FinalPlanConfirmationService(
        new OwnershipGate(true), new MemoryRevisionStore(revision),
        new InputSource(geometry.LandRevisionId, geometry.RequirementsRevisionId),
        new FreshnessPolicy(false), new ConfirmationTransaction(), new ConfirmationSink(), TimeProvider.System);
    var result = service.ConfirmAsync(
        new(geometry.ProjectId, geometry.PlanId, revision.RevisionId, hash, true), Guid.NewGuid()).Result;
    Assert(result.Status == ConfirmationStatus.ValidationStale, "Expired validation was accepted.");
});

Run("duplicate confirmation result is preserved", () =>
{
    var geometry = MinimalGeometry(0);
    var hash = PlanRevisionService.ComputeGeometryHash(JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions));
    var revision = Revision(geometry, hash, 1, Validation(hash));
    var sink = new ConfirmationSink();
    var service = new FinalPlanConfirmationService(
        new OwnershipGate(true), new MemoryRevisionStore(revision),
        new InputSource(geometry.LandRevisionId, geometry.RequirementsRevisionId),
        new FreshnessPolicy(true), new ConfirmationTransaction(ConfirmationStatus.AlreadyConfirmed), sink, TimeProvider.System);
    var result = service.ConfirmAsync(
        new(geometry.ProjectId, geometry.PlanId, revision.RevisionId, hash, true), Guid.NewGuid()).Result;
    Assert(result.Status == ConfirmationStatus.AlreadyConfirmed, "Duplicate confirmation status was lost.");
    Assert(sink.PublishedRevisionId is null, "Duplicate confirmation was republished.");
});

Run("confirmation transaction failure does not publish report", () =>
{
    var geometry = MinimalGeometry(0);
    var hash = PlanRevisionService.ComputeGeometryHash(JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions));
    var revision = Revision(geometry, hash, 1, Validation(hash));
    var sink = new ConfirmationSink();
    var service = new FinalPlanConfirmationService(
        new OwnershipGate(true), new MemoryRevisionStore(revision),
        new InputSource(geometry.LandRevisionId, geometry.RequirementsRevisionId),
        new FreshnessPolicy(true), new ConfirmationTransaction(throwFailure: true), sink, TimeProvider.System);
    AssertThrows<InvalidOperationException>(() => service.ConfirmAsync(
        new(geometry.ProjectId, geometry.PlanId, revision.RevisionId, hash, true), Guid.NewGuid()).GetAwaiter().GetResult());
    Assert(sink.PublishedRevisionId is null, "Report was published after transaction failure.");
});

Run("confirmation rejects geometry hash mismatch", () =>
{
    var geometry = MinimalGeometry(0);
    var json = JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions);
    var hash = PlanRevisionService.ComputeGeometryHash(json);
    var revision = Revision(geometry, hash, 1, Validation(hash));
    var service = new FinalPlanConfirmationService(
        new OwnershipGate(true), new MemoryRevisionStore(revision),
        new InputSource(geometry.LandRevisionId, geometry.RequirementsRevisionId),
        new FreshnessPolicy(true), new ConfirmationTransaction(), new ConfirmationSink(), TimeProvider.System);
    var result = service.ConfirmAsync(
        new(geometry.ProjectId, geometry.PlanId, revision.RevisionId, "sha256:wrong", true), Guid.NewGuid())
        .GetAwaiter().GetResult();
    Assert(result.Status == ConfirmationStatus.GeometryHashMismatch, "Mismatched hash was accepted.");
});

Run("M1 ownership test double preserves owner and cross-project decisions", () =>
{
    var ownedProject = Guid.NewGuid();
    var owner = Guid.NewGuid();
    IProjectOwnershipGate gate = new TestProjectOwnershipGate(
        (projectId, actorId, _) => Task.FromResult(projectId == ownedProject && actorId == owner));
    Assert(gate.OwnsProjectAsync(ownedProject, owner, default).Result, "Owner access was not preserved.");
    Assert(!gate.OwnsProjectAsync(Guid.NewGuid(), owner, default).Result, "Cross-project access was granted.");
});

Run("M1 lifecycle eligibility exposes allowed and blocked responses", () =>
{
    IProjectLifecycleEligibilityPort allowed = new TestProjectLifecycleEligibilityPort(
        (_, _, _) => Task.FromResult(new ProjectLifecycleEligibility(true, true, null)));
    IProjectLifecycleEligibilityPort blocked = new TestProjectLifecycleEligibilityPort(
        (_, _, _) => Task.FromResult(new ProjectLifecycleEligibility(true, false, "PROJECT_STATE_BLOCKED")));
    Assert(allowed.GetConfirmationEligibilityAsync(Guid.NewGuid(), Guid.NewGuid(), default).Result.CanConfirm,
        "Allowed lifecycle response was lost.");
    Assert(blocked.GetConfirmationEligibilityAsync(Guid.NewGuid(), Guid.NewGuid(), default).Result.BlockingCode
        == "PROJECT_STATE_BLOCKED", "Blocked lifecycle reason was lost.");
});

Run("M2 confirmed land test double distinguishes success missing and cross-project response", () =>
{
    var projectId = Guid.NewGuid();
    var revisionId = Guid.NewGuid();
    using var boundary = JsonDocument.Parse("{\"type\":\"Polygon\",\"coordinates\":[]}");
    IConfirmedLandRevisionPort port = new TestConfirmedLandRevisionPort((requestedProject, requestedRevision, _) =>
        Task.FromResult<ConfirmedLandRevision?>(requestedRevision == revisionId
            ? new ConfirmedLandRevision(requestedProject, revisionId, "test-only/1", boundary.RootElement.Clone())
            : null));
    var found = port.GetAsync(projectId, revisionId, default).Result;
    Assert(found?.ProjectId == projectId, "Confirmed land response identity was lost.");
    Assert(port.GetAsync(projectId, Guid.NewGuid(), default).Result is null, "Missing land revision was fabricated.");

    IConfirmedLandRevisionPort invalid = new TestConfirmedLandRevisionPort((_, _, _) =>
        Task.FromResult<ConfirmedLandRevision?>(new(
            Guid.NewGuid(), revisionId, "test-only/1", boundary.RootElement.Clone())));
    var invalidResponse = invalid.GetAsync(projectId, revisionId, default).Result;
    Assert(invalidResponse?.ProjectId != projectId,
        "The invalid-response fixture must remain cross-project for future adapter rejection tests.");
});

Run("M5 requirement revision test double distinguishes success and missing", () =>
{
    var projectId = Guid.NewGuid();
    var revisionId = Guid.NewGuid();
    IRequirementRevisionPort port = new TestRequirementRevisionPort((requestedProject, requestedRevision, _) =>
        Task.FromResult<RequirementRevisionReference?>(requestedRevision == revisionId
            ? new RequirementRevisionReference(requestedProject, revisionId, "test-only/1")
            : null));
    Assert(port.GetAsync(projectId, revisionId, default).Result?.ProjectId == projectId,
        "Requirement revision response identity was lost.");
    Assert(port.GetAsync(projectId, Guid.NewGuid(), default).Result is null,
        "Missing requirement revision was fabricated.");
});

Run("M4 compute client propagates correlation and validates successful response", () =>
{
    var geometry = MinimalGeometry(0);
    var request = new PlanEditorComputeRequest(
        geometry, EditRequest(PlanEditCommandTypes.MoveRoom, "{\"deltaX\":1,\"deltaY\":0}").Command,
        "contract-correlation-id");
    string? observedCorrelation = null;
    IPlanEditorComputeClient client = new TestPlanEditorComputeClient((received, _) =>
    {
        observedCorrelation = received.CorrelationId;
        return Task.FromResult(new PlanEditResultDto
        {
            SchemaVersion = geometry.SchemaVersion,
            CommandId = received.Command.CommandId,
            BaseRevisionId = Guid.NewGuid(),
            Status = PlanEditStatus.Accepted,
            CandidateGeometry = geometry,
            Diagnostics = [],
            ChangedEntityIds = []
        });
    });
    var response = client.PreviewAsync(request, default).Result;
    PlanEditorContractValidator.Validate(response);
    Assert(observedCorrelation == request.CorrelationId, "Compute correlation ID was not propagated.");
});

Run("M4 compute client exposes unavailability and cancellation", () =>
{
    IPlanEditorComputeClient unavailable = new TestPlanEditorComputeClient(
        (_, _) => Task.FromException<PlanEditResultDto>(new HttpRequestException("Explicit unavailable test double.")));
    AssertThrows<HttpRequestException>(() => unavailable.PreviewAsync(
        new(MinimalGeometry(0), EditRequest(PlanEditCommandTypes.RemoveOpening, "{}").Command, "c"), default)
        .GetAwaiter().GetResult());

    IPlanEditorComputeClient timeout = new TestPlanEditorComputeClient(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("Unreachable.");
    });
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    AssertThrows<TaskCanceledException>(() => timeout.PreviewAsync(
        new(MinimalGeometry(0), EditRequest(PlanEditCommandTypes.RemoveOpening, "{}").Command, "c"),
        cancellation.Token).GetAwaiter().GetResult());
});

Run("M4 persistence failure leaves revision history unchanged", () =>
{
    var geometry = MinimalGeometry(0);
    var baseRevision = Revision(geometry, "sha256:base", 1);
    var store = new TestPlanRevisionStore(baseRevision) { ThrowOnAppend = true };
    var hash = PlanRevisionService.ComputeGeometryHash(JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions));
    var service = new PlanRevisionService(store, new TestPlanValidationEvidenceVerifier(
        (_, _, _, _, _) => Task.FromResult(ValidationEvidenceStatus.Verified)), TimeProvider.System);
    AssertThrows<InvalidOperationException>(() => service.CreateAsync(
        new(geometry.ProjectId, geometry.PlanId, baseRevision.RevisionId, geometry,
            Validation(hash), Guid.NewGuid(), "must roll back"), Guid.NewGuid()).GetAwaiter().GetResult());
    Assert(store.GetHistoryAsync(geometry.ProjectId, geometry.PlanId, default).Result.Count == 1,
        "Persistence failure changed revision history.");
});

Run("M6 validation test double rejects mismatched authoritative evidence", () =>
{
    IPlanValidationEvidenceVerifier verifier = new TestPlanValidationEvidenceVerifier(
        (_, _, evidence, exactHash, _) => Task.FromResult(
            evidence.GeometryHash == exactHash ? ValidationEvidenceStatus.Verified : ValidationEvidenceStatus.Rejected));
    var status = verifier.VerifyAsync(Guid.NewGuid(), Guid.NewGuid(), Validation("sha256:wrong"),
        "sha256:expected", default).Result;
    Assert(status == ValidationEvidenceStatus.Rejected, "Mismatched validation evidence was accepted.");
});

Run("M5 report publication failure remains visible after confirmed transaction", () =>
{
    var geometry = MinimalGeometry(0);
    var hash = PlanRevisionService.ComputeGeometryHash(JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions));
    var revision = Revision(geometry, hash, 1, Validation(hash));
    var sink = new TestConfirmedPlanSnapshotSink((_, _, _) =>
        Task.FromException(new InvalidOperationException("Explicit report publication test-double failure.")));
    var service = new FinalPlanConfirmationService(
        new TestProjectOwnershipGate((_, _, _) => Task.FromResult(true)), new TestPlanRevisionStore(revision),
        new TestApplicableInputRevisionSource((_, _) => Task.FromResult(
            new ApplicableInputRevisions(geometry.LandRevisionId, geometry.RequirementsRevisionId))),
        new TestValidationFreshnessPolicy(true),
        new TestFinalConfirmationTransaction((confirmation, _, _) =>
            Task.FromResult(new ConfirmationResult(ConfirmationStatus.Confirmed, confirmation))),
        sink, TimeProvider.System);
    AssertThrows<InvalidOperationException>(() => service.ConfirmAsync(
        new(geometry.ProjectId, geometry.PlanId, revision.RevisionId, hash, true), Guid.NewGuid())
        .GetAwaiter().GetResult());
    Assert(sink.PublishedRevisionId is null, "Failed report publication was recorded as delivered.");
});

Run("M7 AI proposal test double exposes unavailable and malformed proposals", () =>
{
    IAiEditProposalPort unavailable = new TestAiEditProposalPort((_, _, _, _, _, _) =>
        Task.FromResult(new AiEditProposal(AiEditProposalStatus.Unavailable, [], [])));
    Assert(unavailable.ProposeAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "move room", "c", default)
        .Result.Status == AiEditProposalStatus.Unavailable, "Unavailable AI state was fabricated as success.");

    using var malformedPayload = JsonDocument.Parse("{}");
    IAiEditProposalPort malformed = new TestAiEditProposalPort((_, _, _, _, _, _) =>
        Task.FromResult(new AiEditProposal(AiEditProposalStatus.Proposed,
        [new PlanEditCommandDto
        {
            CommandId = Guid.NewGuid(), CommandType = PlanEditCommandTypes.MoveRoom,
            FloorId = Guid.NewGuid(), TargetId = Guid.NewGuid(), Payload = malformedPayload.RootElement.Clone()
        }], [])));
    var proposal = malformed.ProposeAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "move room", "c", default).Result;
    var proposedCommand = proposal.Commands.Single();
    AssertThrows<ArgumentException>(() => PlanEditorContractValidator.Validate(new PlanEditRequestDto
    {
        SchemaVersion = GeometrySchemaInfo.DraftVersion,
        ProjectId = Guid.NewGuid(), PlanId = Guid.NewGuid(), BaseRevisionId = Guid.NewGuid(), Command = proposedCommand
    }));
});

CandidatePreviewContractScenarios.Register(Run);
DiagnosticContractScenarios.Register(Run);
PersistenceContractScenarios.Register(Run);
AdvancedGeometryContractScenarios.Register(Run);

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{passed} passed, {failures.Count} failed");
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    return 1;
}

Console.WriteLine($"{passed} passed, 0 failed");
return 0;

void Run(string name, Action test)
{
    try { test(); passed++; }
    catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static PlanGeometryDto MinimalGeometry(double originX)
{
    var floorId = Guid.NewGuid();
    return new PlanGeometryDto
    {
        SchemaVersion = GeometrySchemaInfo.DraftVersion,
        ProjectId = Guid.NewGuid(),
        PlanId = Guid.NewGuid(),
        LandRevisionId = Guid.NewGuid(),
        RequirementsRevisionId = Guid.NewGuid(),
        CoordinateSystem = new CoordinateSystemDto
        {
            Type = "localCartesian", Unit = "m", Origin = new CoordinatePointDto { X = originX, Y = 0 },
            XDirection = "right", YDirection = "up"
        },
        Floors = [new FloorDto { Id = floorId, Level = 0, Rooms = [], Walls = [], Openings = [] }]
    };
}

static PlanEditRequestDto EditRequest(string commandType, string payloadJson)
{
    using var document = JsonDocument.Parse(payloadJson);
    return new PlanEditRequestDto
    {
        SchemaVersion = GeometrySchemaInfo.DraftVersion,
        ProjectId = Guid.NewGuid(),
        PlanId = Guid.NewGuid(),
        BaseRevisionId = Guid.NewGuid(),
        Command = new PlanEditCommandDto
        {
            CommandId = Guid.NewGuid(),
            CommandType = commandType,
            FloorId = Guid.NewGuid(),
            TargetId = commandType is PlanEditCommandTypes.AddRoom or PlanEditCommandTypes.AddOpening
                ? null : Guid.NewGuid(),
            Payload = document.RootElement.Clone()
        }
    };
}

static IReadOnlyDictionary<string, string> ValidCommandPayloads() => new Dictionary<string, string>
{
    [PlanEditCommandTypes.AddRoom] = """
        {"room":{"id":"10000000-0000-4000-8000-000000000001","name":"Room","type":"bedroom","polygon":[{"x":0,"y":0},{"x":4,"y":0},{"x":0,"y":3},{"x":0,"y":0}],"wallIds":["20000000-0000-4000-8000-000000000001","20000000-0000-4000-8000-000000000002","20000000-0000-4000-8000-000000000003"]},"walls":[{"id":"20000000-0000-4000-8000-000000000001","start":{"x":0,"y":0},"end":{"x":4,"y":0},"thickness":0.2},{"id":"20000000-0000-4000-8000-000000000002","start":{"x":4,"y":0},"end":{"x":0,"y":3},"thickness":0.2},{"id":"20000000-0000-4000-8000-000000000003","start":{"x":0,"y":3},"end":{"x":0,"y":0},"thickness":0.2}],"openings":[]}
        """,
    [PlanEditCommandTypes.RemoveRoom] = "{\"removeOrphanOpenings\":true}",
    [PlanEditCommandTypes.MoveRoom] = "{\"deltaX\":1.25,\"deltaY\":-0.5}",
    [PlanEditCommandTypes.ResizeRoom] = "{\"scaleX\":1.2,\"scaleY\":0.8,\"anchorX\":null,\"anchorY\":0}",
    [PlanEditCommandTypes.UpdateWall] = "{\"thickness\":0.25}",
    [PlanEditCommandTypes.AddOpening] = "{\"opening\":{\"id\":\"30000000-0000-4000-8000-000000000001\",\"type\":\"door\",\"wallId\":\"20000000-0000-4000-8000-000000000001\",\"offset\":0.5,\"width\":0.9}}",
    [PlanEditCommandTypes.MoveOpening] = "{\"offset\":1.5}",
    [PlanEditCommandTypes.RemoveOpening] = "{}"
};

static PlanValidationReferenceDto Validation(string hash) => new()
{
    ValidationId = Guid.NewGuid(), ValidatorVersion = "explicit-test-double/1", GeometryHash = hash,
    GeometryValid = true, RequirementsMatch = true, RegulatoryEvidenceStatus = "NOT_VERIFIED",
    HasBlockingIssues = false, ValidatedAtUtc = DateTimeOffset.UtcNow
};

static FinalPlanConfirmationService ConfirmationService(
    PlanGeometryDto geometry, PlanRevisionSnapshot revision, IProjectOwnershipGate ownership) => new(
        ownership, new MemoryRevisionStore(revision),
        new InputSource(geometry.LandRevisionId, geometry.RequirementsRevisionId),
        new FreshnessPolicy(true), new ConfirmationTransaction(), new ConfirmationSink(), TimeProvider.System);

static PlanRevisionSnapshot Revision(
    PlanGeometryDto geometry,
    string hash,
    long version,
    PlanValidationReferenceDto? validation = null) => new(
        Guid.NewGuid(), geometry.PlanId, geometry.ProjectId, null, version, geometry.SchemaVersion,
        JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions), hash, geometry.LandRevisionId, geometry.RequirementsRevisionId,
        validation ?? Validation(hash), null, Guid.NewGuid(), DateTimeOffset.UtcNow, null);

sealed class MemoryRevisionStore(PlanRevisionSnapshot initial) : IPlanRevisionStore
{
    private readonly List<PlanRevisionSnapshot> revisions = [initial];
    private readonly object sync = new();

    public Task<PlanRevisionSnapshot?> GetAsync(Guid projectId, Guid planId, Guid revisionId, CancellationToken cancellationToken) =>
        Task.FromResult(Read(() => revisions.SingleOrDefault(item => item.ProjectId == projectId && item.PlanId == planId && item.RevisionId == revisionId)));

    public Task<PlanRevisionSnapshot?> GetCurrentAsync(Guid projectId, Guid planId, CancellationToken cancellationToken) =>
        Task.FromResult(Read(() => revisions.Where(item => item.ProjectId == projectId && item.PlanId == planId).MaxBy(item => item.VersionNumber)));

    public Task<IReadOnlyList<PlanRevisionSnapshot>> GetHistoryAsync(Guid projectId, Guid planId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PlanRevisionSnapshot>>(Read(() => revisions.Where(item => item.ProjectId == projectId && item.PlanId == planId).ToArray()));

    public Task<AppendRevisionResult> TryAppendAtomicallyAsync(Guid expectedBaseRevisionId, PlanRevisionSnapshot proposedRevision, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (proposedRevision.EditCommandId is not null
                && revisions.Any(item => item.ProjectId == proposedRevision.ProjectId
                    && item.PlanId == proposedRevision.PlanId
                    && item.EditCommandId == proposedRevision.EditCommandId))
                return Task.FromResult(new AppendRevisionResult(AppendRevisionStatus.DuplicateCommand));
            if (revisions.MaxBy(item => item.VersionNumber)!.RevisionId != expectedBaseRevisionId)
                return Task.FromResult(new AppendRevisionResult(AppendRevisionStatus.RevisionConflict));
            revisions.Add(proposedRevision);
            return Task.FromResult(new AppendRevisionResult(AppendRevisionStatus.Created, proposedRevision));
        }
    }

    private T Read<T>(Func<T> action) { lock (sync) return action(); }
}

sealed class OwnershipGate(bool result) : IProjectOwnershipGate
{
    public Task<bool> OwnsProjectAsync(Guid projectId, Guid authenticatedUserId, CancellationToken cancellationToken) => Task.FromResult(result);
}

sealed class InputSource(Guid land, Guid requirements) : IApplicableInputRevisionSource
{
    public Task<ApplicableInputRevisions> GetCurrentAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult(new ApplicableInputRevisions(land, requirements));
}

sealed class FreshnessPolicy(bool result) : IValidationFreshnessPolicy
{
    public bool IsFresh(PlanRevisionSnapshot revision, DateTimeOffset now) => result;
}

sealed class ConfirmationTransaction(
    ConfirmationStatus status = ConfirmationStatus.Confirmed,
    bool throwFailure = false) : IFinalConfirmationTransaction
{
    public Task<ConfirmationResult> ConfirmAtomicallyAsync(PlanConfirmationSnapshot confirmation, PlanRevisionSnapshot exactRevision, CancellationToken cancellationToken)
    {
        if (throwFailure) throw new InvalidOperationException("Explicit transaction test-double failure.");
        return Task.FromResult(new ConfirmationResult(
            status, status == ConfirmationStatus.Confirmed ? confirmation : null));
    }
}

sealed class ConfirmationSink : IConfirmedPlanSnapshotSink
{
    public Guid? PublishedRevisionId { get; private set; }
    public Task PublishAsync(PlanConfirmationSnapshot confirmation, PlanRevisionSnapshot exactRevision, CancellationToken cancellationToken)
    {
        PublishedRevisionId = exactRevision.RevisionId;
        return Task.CompletedTask;
    }
}

sealed class ValidationVerifier(ValidationEvidenceStatus status) : IPlanValidationEvidenceVerifier
{
    public Task<ValidationEvidenceStatus> VerifyAsync(
        Guid projectId, Guid planId, PlanValidationReferenceDto evidence, string exactGeometryHash,
        CancellationToken cancellationToken) => Task.FromResult(status);
}
