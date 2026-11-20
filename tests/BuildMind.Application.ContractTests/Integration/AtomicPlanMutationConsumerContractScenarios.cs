using System.Text.Json;
using BuildMind.Application.Abstractions;
using BuildMind.Application.Abstractions.Errors;
using BuildMind.Application.Features.PlanEditor.Contracts;
using BuildMind.Application.Features.PlanEditor.Editing;
using BuildMind.Application.Features.PlanEditor.Integration;
using BuildMind.Application.Features.PlanEditor.Mutations;
using BuildMind.Application.Features.PlanEditor.Versioning;

namespace BuildMind.Application.ContractTests.Integration;

/// <summary>
/// Test-only mock-orchestration evidence for STEP 07-C3-A. These scenarios do not establish
/// database rollback, durable authorization, transaction locking or production authentication.
/// </summary>
internal static class AtomicPlanMutationConsumerContractScenarios
{
    private const string Schema = "0.1-draft";

    public static void Register(Action<string, Action> run)
    {
        run("AM-A03 atomic consumer rejects client actor injection for all six operations", () =>
        {
            foreach (var item in Cases(OtherActor))
            {
                var currentUser = new CurrentUserDouble(Actor);
                var authorization = Authorized(Project, Actor, 7);
                var port = new AtomicMutationPortDouble();
                var consumer = new AtomicPlanMutationConsumer(currentUser, authorization, port);

                var result = item.Invoke(consumer, CancellationToken.None).GetAwaiter().GetResult();

                CheckFailure(result, AtomicMutationStatus.AuthorizationDenied, item.Name);
                Check(currentUser.RequiredUserCalls == 1, $"{item.Name} did not acquire the trusted actor exactly once.");
                Check(authorization.Calls == 0, $"{item.Name} actor injection reached authorization.");
                Check(port.TotalMutationCalls == 0 && port.ReconcileCalls == 0,
                    $"{item.Name} actor injection reached mutation or reconciliation.");
            }

            foreach (var item in Cases(Actor))
            {
                var currentUser = new CurrentUserDouble(Actor);
                var authorization = Authorized(Project, Actor, 7);
                var port = new AtomicMutationPortDouble
                {
                    Status = AtomicMutationStatus.PersistenceFailure,
                };
                var consumer = new AtomicPlanMutationConsumer(currentUser, authorization, port);

                var result = item.Invoke(consumer, CancellationToken.None).GetAwaiter().GetResult();

                CheckFailure(result, AtomicMutationStatus.PersistenceFailure, item.Name);
                Check(authorization.Calls == 1 && authorization.LastActorId == Actor,
                    $"{item.Name} did not authorize the trusted actor.");
                Check(port.TotalMutationCalls == 1 && port.Calls(item.Operation) == 1,
                    $"{item.Name} did not make exactly one matching atomic-port call.");
                Check(port.ReconcileCalls == 0, $"{item.Name} automatically reconciled.");
                Check(port.WriteAttempts == 1, $"{item.Name} did not reach the simulated write boundary.");
            }

            foreach (var item in Cases(Actor))
            {
                var authorization = new ProjectAuthorizationPortDouble(
                    new ProjectAuthorizationResult(ProjectAuthorizationStatus.Denied));
                var port = new AtomicMutationPortDouble();
                var consumer = new AtomicPlanMutationConsumer(
                    new CurrentUserDouble(Actor), authorization, port);

                var result = item.Invoke(consumer, CancellationToken.None).GetAwaiter().GetResult();

                CheckFailure(result, AtomicMutationStatus.AuthorizationDenied, item.Name);
                Check(port.TotalMutationCalls == 0, $"{item.Name} denial reached mutation.");
            }
        });

        run("AM-A09 atomic consumer preserves preflight and transaction-time authorization version mismatch", () =>
        {
            foreach (var item in Cases(Actor, version: 7))
            {
                var authorization = Authorized(Project, Actor, version: 8);
                var port = new AtomicMutationPortDouble();
                var consumer = new AtomicPlanMutationConsumer(
                    new CurrentUserDouble(Actor), authorization, port);

                var result = item.Invoke(consumer, CancellationToken.None).GetAwaiter().GetResult();

                CheckFailure(result, AtomicMutationStatus.AuthorizationVersionMismatch, item.Name);
                Check(authorization.Calls == 1, $"{item.Name} did not perform preflight authorization.");
                Check(port.TotalMutationCalls == 0, $"{item.Name} version mismatch reached mutation.");
            }

            foreach (var item in Cases(Actor, version: 7))
            {
                var authorization = Authorized(Project, Actor, version: 7);
                var port = new AtomicMutationPortDouble
                {
                    Status = AtomicMutationStatus.AuthorizationVersionMismatch,
                };
                var consumer = new AtomicPlanMutationConsumer(
                    new CurrentUserDouble(Actor), authorization, port);

                var result = item.Invoke(consumer, CancellationToken.None).GetAwaiter().GetResult();

                CheckFailure(result, AtomicMutationStatus.AuthorizationVersionMismatch, item.Name);
                Check(port.TotalMutationCalls == 1 && port.Calls(item.Operation) == 1,
                    $"{item.Name} transaction-time mismatch call count changed.");
                Check(port.ReconcileCalls == 0 && port.RetryCalls == 0,
                    $"{item.Name} transaction-time mismatch was retried or reconciled.");
            }

            foreach (var item in Cases(Actor))
            {
                var nullAuthorization = new ProjectAuthorizationPortDouble(null);
                var nullPort = new AtomicMutationPortDouble();
                var nullConsumer = new AtomicPlanMutationConsumer(
                    new CurrentUserDouble(Actor), nullAuthorization, nullPort);
                var nullResult = item.Invoke(nullConsumer, CancellationToken.None).GetAwaiter().GetResult();
                CheckFailure(nullResult, AtomicMutationStatus.InvalidDependencyResponse, item.Name);
                Check(nullPort.TotalMutationCalls == 0,
                    $"{item.Name} null authorization response reached mutation.");

                var contradictoryAuthorization = Authorized(OtherProject, Actor, 7);
                var contradictoryPort = new AtomicMutationPortDouble();
                var contradictoryConsumer = new AtomicPlanMutationConsumer(
                    new CurrentUserDouble(Actor), contradictoryAuthorization, contradictoryPort);
                var contradictoryResult = item.Invoke(
                    contradictoryConsumer, CancellationToken.None).GetAwaiter().GetResult();
                CheckFailure(contradictoryResult, AtomicMutationStatus.InvalidDependencyResponse, item.Name);
                Check(contradictoryPort.TotalMutationCalls == 0,
                    $"{item.Name} contradictory authorization response reached mutation.");
            }

            var create = Cases(Actor)[0];
            var malformedPort = new AtomicMutationPortDouble { ReturnNullResponse = true };
            var malformedConsumer = new AtomicPlanMutationConsumer(
                new CurrentUserDouble(Actor), Authorized(Project, Actor, 7), malformedPort);
            var malformedResult = create.Invoke(
                malformedConsumer, CancellationToken.None).GetAwaiter().GetResult();
            CheckFailure(malformedResult, AtomicMutationStatus.InvalidDependencyResponse, create.Name);
            Check(malformedPort.TotalMutationCalls == 1 && malformedPort.RetryCalls == 0 &&
                  malformedPort.ReconcileCalls == 0,
                "Malformed mutation response was retried or reconciled.");
        });

        run("AM-T01 atomic consumer propagates cancellation before mutation", () =>
        {
            foreach (var item in Cases(Actor))
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                var currentUser = new CurrentUserDouble(Actor);
                var authorization = Authorized(Project, Actor, 7);
                var port = new AtomicMutationPortDouble();
                var consumer = new AtomicPlanMutationConsumer(currentUser, authorization, port);

                CaptureThrowsAsync<OperationCanceledException>(() =>
                    item.Invoke(consumer, cancelled.Token));

                Check(currentUser.RequiredUserCalls == 0,
                    $"{item.Name} acquired identity after caller cancellation.");
                Check(authorization.Calls == 0 && port.TotalMutationCalls == 0,
                    $"{item.Name} caller cancellation reached a dependency.");
            }

            foreach (var item in Cases(Actor))
            {
                using var cancelled = new CancellationTokenSource();
                var authorization = Authorized(Project, Actor, 7);
                authorization.CancelSource = cancelled;
                authorization.CancelDuringCall = true;
                var port = new AtomicMutationPortDouble();
                var consumer = new AtomicPlanMutationConsumer(
                    new CurrentUserDouble(Actor), authorization, port);

                CaptureThrowsAsync<OperationCanceledException>(() =>
                    item.Invoke(consumer, cancelled.Token));

                Check(authorization.Calls == 1 && authorization.LastToken == cancelled.Token,
                    $"{item.Name} did not preserve the exact preflight cancellation token.");
                Check(port.TotalMutationCalls == 0,
                    $"{item.Name} mutation ran after cancelled preflight.");
            }
        });

        run("AM-T02 atomic consumer preserves cancellation before the test-only first write", () =>
        {
            foreach (var item in Cases(Actor))
            {
                using var cancelled = new CancellationTokenSource();
                var authorization = Authorized(Project, Actor, 7);
                var port = new AtomicMutationPortDouble
                {
                    CancelBeforeFirstWrite = true,
                    CancelSource = cancelled,
                };
                var consumer = new AtomicPlanMutationConsumer(
                    new CurrentUserDouble(Actor), authorization, port);

                CaptureThrowsAsync<OperationCanceledException>(() =>
                    item.Invoke(consumer, cancelled.Token));

                Check(authorization.Calls == 1 && authorization.LastToken == cancelled.Token,
                    $"{item.Name} authorization received a different token.");
                Check(port.TotalMutationCalls == 1 && port.Calls(item.Operation) == 1,
                    $"{item.Name} did not make exactly one matching mutation call.");
                Check(port.LastToken == cancelled.Token,
                    $"{item.Name} mutation received a different caller token.");
                Check(port.WriteAttempts == 0,
                    $"{item.Name} cancellation occurred after a simulated write.");
                Check(port.RetryCalls == 0 && port.ReconcileCalls == 0,
                    $"{item.Name} cancellation triggered retry or reconciliation.");
            }
        });
    }

    private static OperationCase[] Cases(Guid actorId, long version = 7)
    {
        var create = CreateRequest(actorId, version);
        var apply = ApplyRequest(actorId, version);
        var reject = RejectRequest(actorId, version);
        var expire = ExpireRequest(actorId, version);
        var append = AppendRequest(actorId, version);
        var confirm = ConfirmRequest(actorId, version);

        return
        [
            new("Candidate Create", ProvisionalMutationOperation.CandidateCreate, async (consumer, token) =>
            {
                var response = await consumer.CreateCandidateAsync(create, token);
                return new(response.Result, response.Candidate is not null);
            }),
            new("Candidate Apply", ProvisionalMutationOperation.CandidateApply, async (consumer, token) =>
            {
                var response = await consumer.ApplyCandidateAsync(apply, token);
                return new(response.Result, response.Candidate is not null || response.Revision is not null);
            }),
            new("Candidate Reject", ProvisionalMutationOperation.CandidateReject, async (consumer, token) =>
            {
                var response = await consumer.RejectCandidateAsync(reject, token);
                return new(response.Result, response.Candidate is not null);
            }),
            new("Candidate Expire", ProvisionalMutationOperation.CandidateExpire, async (consumer, token) =>
            {
                var response = await consumer.ExpireCandidateAsync(expire, token);
                return new(response.Result, response.Candidate is not null);
            }),
            new("Revision Append", ProvisionalMutationOperation.PlanRevisionAppend, async (consumer, token) =>
            {
                var response = await consumer.AppendRevisionAsync(append, token);
                return new(response.Result, response.Revision is not null);
            }),
            new("Final Confirmation", ProvisionalMutationOperation.FinalPlanConfirmation, async (consumer, token) =>
            {
                var response = await consumer.ConfirmFinalPlanAsync(confirm, token);
                return new(response.Result, response.Confirmation is not null);
            }),
        ];
    }

    private static CandidateCreateMutationRequest CreateRequest(Guid actorId, long version)
    {
        var scope = Scope(ProvisionalMutationOperation.CandidateCreate, actorId, version);
        var command = Command();
        var fingerprint = MutationFingerprintProfileV1.Compute(new CandidateCreateFingerprintInput(
            FingerprintScope(scope), Base, Land, Requirements, Schema, command));
        return CandidateCreateMutationRequest.Create(
            scope, Authorization(scope), Identity(scope, fingerprint), Base, Land, Requirements,
            Schema, command);
    }

    private static CandidateApplyMutationRequest ApplyRequest(Guid actorId, long version)
    {
        var scope = Scope(ProvisionalMutationOperation.CandidateApply, actorId, version);
        var prepared = PreparedRevision();
        var fingerprint = MutationFingerprintProfileV1.Compute(new CandidateApplyFingerprintInput(
            FingerprintScope(scope), Candidate, Base, Land, Requirements,
            prepared.Geometry.GeometryHash, ValidationId));
        return CandidateApplyMutationRequest.Create(
            scope, Authorization(scope), Identity(scope, fingerprint), Candidate, Base, Land,
            Requirements, prepared.Geometry.GeometryHash, ValidationId, prepared);
    }

    private static CandidateRejectMutationRequest RejectRequest(Guid actorId, long version)
    {
        var scope = Scope(ProvisionalMutationOperation.CandidateReject, actorId, version);
        var fingerprint = MutationFingerprintProfileV1.Compute(new CandidateRejectFingerprintInput(
            FingerprintScope(scope), Candidate, "USER_REJECTED"));
        return CandidateRejectMutationRequest.Create(
            scope, Authorization(scope), Identity(scope, fingerprint), Candidate, "USER_REJECTED");
    }

    private static CandidateExpireMutationRequest ExpireRequest(Guid actorId, long version)
    {
        var scope = Scope(ProvisionalMutationOperation.CandidateExpire, actorId, version);
        var fingerprint = MutationFingerprintProfileV1.Compute(new CandidateExpireFingerprintInput(
            FingerprintScope(scope), Candidate, Timestamp(2)));
        return CandidateExpireMutationRequest.Create(
            scope, Authorization(scope), Identity(scope, fingerprint), Candidate, Timestamp(2));
    }

    private static PlanRevisionAppendMutationRequest AppendRequest(Guid actorId, long version)
    {
        var scope = Scope(ProvisionalMutationOperation.PlanRevisionAppend, actorId, version);
        var prepared = PreparedRevision();
        var fingerprint = MutationFingerprintProfileV1.Compute(new PlanRevisionAppendFingerprintInput(
            FingerprintScope(scope), Base, prepared.Geometry.GeometryHash, Land,
            Requirements, ValidationId, Schema));
        return PlanRevisionAppendMutationRequest.Create(
            scope, Authorization(scope), Identity(scope, fingerprint), prepared);
    }

    private static FinalPlanConfirmationMutationRequest ConfirmRequest(Guid actorId, long version)
    {
        var scope = Scope(ProvisionalMutationOperation.FinalPlanConfirmation, actorId, version);
        var geometry = Geometry();
        var prepared = PreparedConfirmationSnapshot.Create(
            Revision, geometry.GeometryHash, ValidationId, Land, Requirements, true);
        var fingerprint = MutationFingerprintProfileV1.Compute(new FinalPlanConfirmationFingerprintInput(
            FingerprintScope(scope), Revision, geometry.GeometryHash,
            ValidationId, Land, Requirements, true));
        return FinalPlanConfirmationMutationRequest.Create(
            scope, Authorization(scope), Identity(scope, fingerprint), prepared);
    }

    private static PreparedRevisionSnapshot PreparedRevision()
    {
        var geometry = Geometry();
        return PreparedRevisionSnapshot.Create(
            Base, Land, Requirements, geometry,
            FrozenValidationEvidence.Create(
                ValidationId, "validator-v1", geometry.GeometryHash, Land, Requirements,
                true, true, "VERIFIED", false, Timestamp(0)));
    }

    private static FrozenGeometrySnapshot Geometry()
    {
        var json = File.ReadAllText(FixtureLocator.ResolveRepositoryFile(
            "docs/proposals/m3-plan-editor/PLAN-GEOMETRY-SAMPLE.json"))
            .Replace("00000000-0000-4000-8000-000000000001", Project.ToString(), StringComparison.Ordinal)
            .Replace("00000000-0000-4000-8000-000000000002", Plan.ToString(), StringComparison.Ordinal)
            .Replace("00000000-0000-4000-8000-000000000003", Land.ToString(), StringComparison.Ordinal)
            .Replace("00000000-0000-4000-8000-000000000004", Requirements.ToString(), StringComparison.Ordinal);
        return FrozenGeometrySnapshot.Create(
            Schema, json, PlanRevisionService.ComputeGeometryHash(json));
    }

    private static PlanEditCommandDto Command()
    {
        using var document = JsonDocument.Parse("{\"deltaX\":1.25,\"deltaY\":-0.5}");
        return new PlanEditCommandDto
        {
            CommandId = CommandId,
            CommandType = PlanEditCommandTypes.MoveRoom,
            FloorId = Floor,
            TargetId = Target,
            Payload = document.RootElement.Clone(),
        };
    }

    private static AtomicMutationScope Scope(
        ProvisionalMutationOperation operation, Guid actorId, long version) =>
        AtomicMutationScope.Create(
            Project, Plan, Operation, operation, actorId, new AuthorizationVersion(version));

    private static AuthorizationBinding Authorization(AtomicMutationScope scope) =>
        new(scope.ProjectId, scope.TrustedActorId, scope.AuthorizationVersion);

    private static MutationIdentity Identity(
        AtomicMutationScope scope, CommandFingerprint fingerprint) =>
        new(scope.OperationId, scope.Operation, MutationFingerprintProfileV1.ProfileNumber, fingerprint);

    private static MutationFingerprintScope FingerprintScope(AtomicMutationScope scope) =>
        new(scope.ProjectId, scope.PlanId, scope.OperationId,
            scope.TrustedActorId, scope.AuthorizationVersion);

    private static ProjectAuthorizationPortDouble Authorized(
        Guid projectId, Guid actorId, long version) =>
        new(new ProjectAuthorizationResult(
            ProjectAuthorizationStatus.Authorized,
            new ProjectAuthorizationSnapshot(projectId, actorId, new AuthorizationVersion(version))));

    private static void CheckFailure(
        Observation observation, AtomicMutationStatus expected, string operation)
    {
        Check(observation.Result.Status == expected, $"{operation} returned {observation.Result.Status}, expected {expected}.");
        Check(!observation.HasProtectedPayload && observation.Result.Evidence is null &&
              observation.Result.Reconciliation is null && !observation.Result.AllowsAutomaticRetry,
            $"{operation} failure exposed protected state or retry authority.");
    }

    private static T CaptureThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { action().GetAwaiter().GetResult(); }
        catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static DateTimeOffset Timestamp(int seconds) =>
        DateTimeOffset.UnixEpoch.AddSeconds(seconds).AddTicks(10);

    private static Guid Id(int value) =>
        Guid.Parse($"00000000-0000-0000-0000-{value:x12}");

    private static readonly Guid Project = Id(1);
    private static readonly Guid OtherProject = Id(2);
    private static readonly Guid Plan = Id(3);
    private static readonly Guid Operation = Id(4);
    private static readonly Guid Actor = Id(5);
    private static readonly Guid OtherActor = Id(6);
    private static readonly Guid Base = Id(7);
    private static readonly Guid Land = Id(8);
    private static readonly Guid Requirements = Id(9);
    private static readonly Guid CommandId = Id(10);
    private static readonly Guid Floor = Id(11);
    private static readonly Guid Target = Id(12);
    private static readonly Guid Candidate = Id(13);
    private static readonly Guid ValidationId = Id(14);
    private static readonly Guid Revision = Id(15);

    private sealed record Observation(AtomicMutationResult Result, bool HasProtectedPayload);

    private sealed record OperationCase(
        string Name,
        ProvisionalMutationOperation Operation,
        Func<AtomicPlanMutationConsumer, CancellationToken, Task<Observation>> Invoke);

    private sealed class CurrentUserDouble(Guid? userId) : ICurrentUser
    {
        public bool IsAuthenticated => userId.HasValue;
        public Guid? UserId => userId;
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>();
        public int RequiredUserCalls { get; private set; }

        public Guid GetRequiredUserId()
        {
            RequiredUserCalls++;
            return userId ?? throw new UnauthenticatedAccessException();
        }
    }

    private sealed class ProjectAuthorizationPortDouble(ProjectAuthorizationResult? result)
        : IProjectAuthorizationPort
    {
        public int Calls { get; private set; }
        public Guid LastProjectId { get; private set; }
        public Guid LastActorId { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public bool CancelDuringCall { get; set; }
        public CancellationTokenSource? CancelSource { get; set; }

        public Task<ProjectAuthorizationResult> AuthorizeOwnerAsync(
            Guid projectId, Guid authenticatedActorId, CancellationToken cancellationToken)
        {
            Calls++;
            LastProjectId = projectId;
            LastActorId = authenticatedActorId;
            LastToken = cancellationToken;
            if (CancelDuringCall)
            {
                CancelSource!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(result!);
        }
    }

    private sealed class AtomicMutationPortDouble : IAtomicPlanMutationPort
    {
        private readonly Dictionary<ProvisionalMutationOperation, int> _calls = [];

        public AtomicMutationStatus Status { get; set; } = AtomicMutationStatus.PersistenceFailure;
        public bool ReturnNullResponse { get; set; }
        public bool CancelBeforeFirstWrite { get; set; }
        public CancellationTokenSource? CancelSource { get; set; }
        public int TotalMutationCalls { get; private set; }
        public int ReconcileCalls { get; private set; }
        public int RetryCalls { get; private set; }
        public int WriteAttempts { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public int Calls(ProvisionalMutationOperation operation) =>
            _calls.TryGetValue(operation, out var count) ? count : 0;

        public Task<CandidateCreateMutationResponse> CreateCandidateAsync(
            CandidateCreateMutationRequest request, CancellationToken cancellationToken) =>
            Execute(request.Scope.Operation, cancellationToken,
                result => CandidateCreateMutationResponse.Failed(result));

        public Task<CandidateApplyMutationResponse> ApplyCandidateAsync(
            CandidateApplyMutationRequest request, CancellationToken cancellationToken) =>
            Execute(request.Scope.Operation, cancellationToken,
                result => CandidateApplyMutationResponse.Failed(result));

        public Task<CandidateStateMutationResponse> RejectCandidateAsync(
            CandidateRejectMutationRequest request, CancellationToken cancellationToken) =>
            Execute(request.Scope.Operation, cancellationToken,
                result => CandidateStateMutationResponse.Failed(result));

        public Task<CandidateStateMutationResponse> ExpireCandidateAsUserAsync(
            CandidateExpireMutationRequest request, CancellationToken cancellationToken) =>
            Execute(request.Scope.Operation, cancellationToken,
                result => CandidateStateMutationResponse.Failed(result));

        public Task<PlanRevisionMutationResponse> AppendPlanRevisionAsync(
            PlanRevisionAppendMutationRequest request, CancellationToken cancellationToken) =>
            Execute(request.Scope.Operation, cancellationToken,
                result => PlanRevisionMutationResponse.Failed(result));

        public Task<FinalConfirmationMutationResponse> ConfirmFinalPlanAsync(
            FinalPlanConfirmationMutationRequest request, CancellationToken cancellationToken) =>
            Execute(request.Scope.Operation, cancellationToken,
                result => FinalConfirmationMutationResponse.Failed(result));

        public Task<ReconciliationResult> ReconcileAsync(
            AtomicMutationReconciliationRequest request, CancellationToken cancellationToken)
        {
            ReconcileCalls++;
            throw new InvalidOperationException("Automatic reconciliation is forbidden in this test double.");
        }

        private Task<T> Execute<T>(
            ProvisionalMutationOperation operation,
            CancellationToken cancellationToken,
            Func<AtomicMutationResult, T> response)
            where T : class
        {
            TotalMutationCalls++;
            LastToken = cancellationToken;
            _calls[operation] = Calls(operation) + 1;

            if (CancelBeforeFirstWrite)
            {
                CancelSource!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            WriteAttempts++;

            if (ReturnNullResponse)
                return Task.FromResult<T>(null!);

            return Task.FromResult(response(new AtomicMutationResult(Status)));
        }
    }
}
