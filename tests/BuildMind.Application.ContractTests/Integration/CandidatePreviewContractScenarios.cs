using System.Text.Json;
using BuildMind.Application.Features.PlanEditor.Confirmation;
using BuildMind.Application.Features.PlanEditor.Contracts;
using BuildMind.Application.Features.PlanEditor.Editing;
using BuildMind.Application.Features.PlanEditor.Integration;
using BuildMind.Application.Features.PlanEditor.Versioning;

namespace BuildMind.Application.ContractTests.Integration;

internal static class CandidatePreviewContractScenarios
{
    public static void Register(Action<string, Action> run)
    {
        run("candidate preview creates bound immutable pending candidate", () =>
        {
            var scenario = new Scenario();
            var result = scenario.Preview();
            Check(result.Status == CandidatePreviewResultStatus.Created, "Candidate was not created.");
            Check(result.Candidate is { Status: CandidatePreviewStatus.Pending }, "Candidate was not pending.");
            var candidate = result.Candidate ?? throw new Exception("Candidate payload was missing.");
            Check(candidate.OwnerId == scenario.OwnerId, "Trusted owner binding was not preserved.");
            Check(candidate.GeometryHash == PlanRevisionService.ComputeGeometryHash(candidate.GeometryJson),
                "Candidate hash was not bound to its snapshot.");
        });

        run("candidate preview leaves original revision unchanged", () =>
        {
            var scenario = new Scenario();
            var originalJson = scenario.Persistence.CurrentRevision.GeometryJson;
            scenario.Preview();
            Check(scenario.Persistence.RevisionCount == 1, "Preview created a saved revision.");
            Check(scenario.Persistence.CurrentRevision.GeometryJson == originalJson, "Preview mutated saved geometry.");
        });

        run("candidate apply creates exactly one immutable revision", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            var result = scenario.Apply(candidate);
            Check(result.Status == CandidateActionStatus.Applied, "Candidate was not applied.");
            Check(scenario.Persistence.RevisionCount == 2, "Apply did not create exactly one revision.");
            Check(result.Revision?.ParentRevisionId == candidate.BaseRevisionId, "Revision parent was not bound.");
            Check(scenario.Persistence.GetCandidate(candidate.CandidateId)!.Status == CandidatePreviewStatus.Applied,
                "Candidate was not consumed.");
        });

        run("candidate reject is terminal and creates no revision", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            var rejected = scenario.Reject(candidate);
            var apply = scenario.Apply(candidate);
            Check(rejected.Status == CandidateActionStatus.Rejected, "Candidate was not rejected.");
            Check(apply.Status == CandidateActionStatus.AlreadyRejected, "Rejected candidate was reusable.");
            Check(scenario.Persistence.RevisionCount == 1, "Reject created a revision.");
        });

        run("candidate preview and action reject unauthorized access", () =>
        {
            var scenario = new Scenario();
            Check(scenario.Preview(Guid.NewGuid()).Status == CandidatePreviewResultStatus.Unauthorized,
                "Unauthorized preview was allowed.");
            var candidate = scenario.Preview().Candidate!;
            Check(scenario.Apply(candidate, Guid.NewGuid()).Status == CandidateActionStatus.Unauthorized,
                "Unauthorized candidate apply was allowed.");
        });

        run("candidate rejects cross-project and cross-plan use", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            Check(scenario.Service.ApplyAsync(
                new(candidate.CandidateId, Guid.NewGuid(), candidate.PlanId, true), scenario.OwnerId).Result.Status
                == CandidateActionStatus.Unauthorized, "Cross-project candidate was accepted.");
            Check(scenario.Service.ApplyAsync(
                new(candidate.CandidateId, candidate.ProjectId, Guid.NewGuid(), true), scenario.OwnerId).Result.Status
                == CandidateActionStatus.WrongProjectOrPlan, "Cross-plan candidate was accepted.");
        });

        run("candidate missing lookup fails closed", () =>
        {
            var scenario = new Scenario();
            var result = scenario.Service.ApplyAsync(
                new(Guid.NewGuid(), scenario.ProjectId, scenario.PlanId, true), scenario.OwnerId).Result;
            Check(result.Status == CandidateActionStatus.NotFound, "Missing candidate was fabricated.");
        });

        run("candidate expiry boundary is terminal", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            scenario.Clock.Advance(scenario.Lifetime);
            var result = scenario.Apply(candidate);
            Check(result.Status == CandidateActionStatus.Expired, "Expiry boundary remained usable.");
            Check(scenario.Persistence.GetCandidate(candidate.CandidateId)!.Status == CandidatePreviewStatus.Expired,
                "Expired status was not persisted by the transaction double.");
            Check(scenario.Persistence.RevisionCount == 1, "Expired candidate created a revision.");
        });

        run("candidate duplicate apply is idempotently rejected", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            Check(scenario.Apply(candidate).Status == CandidateActionStatus.Applied, "First apply failed.");
            Check(scenario.Apply(candidate).Status == CandidateActionStatus.AlreadyApplied, "Duplicate apply was accepted.");
            Check(scenario.Persistence.RevisionCount == 2, "Duplicate apply created another revision.");
        });

        run("candidate repeated reject reports terminal status", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            Check(scenario.Reject(candidate).Status == CandidateActionStatus.Rejected, "First reject failed.");
            Check(scenario.Reject(candidate).Status == CandidateActionStatus.AlreadyRejected,
                "Repeated reject did not preserve terminal status.");
        });

        run("candidate apply rejects stale base revision", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            scenario.Persistence.AdvanceCurrentRevision();
            Check(scenario.Apply(candidate).Status == CandidateActionStatus.StaleBaseRevision,
                "Stale candidate overwrote a newer revision.");
        });

        run("candidate apply rejects changed land revision", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            scenario.CurrentLandRevisionId = Guid.NewGuid();
            Check(scenario.Apply(candidate).Status == CandidateActionStatus.StaleInputs,
                "Changed land revision was ignored.");
        });

        run("candidate apply rejects changed requirements revision", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            scenario.CurrentRequirementsRevisionId = Guid.NewGuid();
            Check(scenario.Apply(candidate).Status == CandidateActionStatus.StaleInputs,
                "Changed requirement revision was ignored.");
        });

        run("candidate apply detects geometry hash mismatch", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            scenario.Persistence.Replace(candidate with { GeometryHash = "sha256:tampered" });
            Check(scenario.Apply(candidate).Status == CandidateActionStatus.GeometryTampered,
                "Tampered geometry hash was accepted.");
        });

        run("candidate apply detects tampered geometry snapshot", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            scenario.Persistence.Replace(candidate with { GeometryJson = "{\"schemaVersion\":\"tampered\"}" });
            Check(scenario.Apply(candidate).Status == CandidateActionStatus.GeometryTampered,
                "Tampered geometry snapshot was accepted.");
        });

        run("candidate preview fails closed when validation is unavailable", () =>
        {
            var scenario = new Scenario { ValidationStatus = ValidationEvidenceStatus.Unavailable };
            Check(scenario.Preview().Status == CandidatePreviewResultStatus.ValidationUnavailable,
                "Unavailable validation produced a candidate.");
            Check(scenario.Persistence.CandidateCount == 0, "Unavailable validation persisted a candidate.");
        });

        run("candidate preview rejects invalid validation evidence", () =>
        {
            var scenario = new Scenario { ReturnMismatchedEvidenceHash = true };
            Check(scenario.Preview().Status == CandidatePreviewResultStatus.ValidationRejected,
                "Mismatched evidence produced a candidate.");
        });

        run("candidate preview rejects blocking validation issues", () =>
        {
            var scenario = new Scenario { HasBlockingIssues = true };
            Check(scenario.Preview().Status == CandidatePreviewResultStatus.ValidationRejected,
                "Blocking evidence produced a candidate.");
        });

        run("candidate apply requires warning acknowledgement", () =>
        {
            var scenario = new Scenario { HasWarnings = true };
            var candidate = scenario.Preview().Candidate!;
            Check(scenario.Apply(candidate, acknowledgeWarnings: false).Status
                == CandidateActionStatus.WarningAcknowledgementRequired, "Warnings were silently accepted.");
            Check(scenario.Apply(candidate, acknowledgeWarnings: true).Status == CandidateActionStatus.Applied,
                "Acknowledged warning candidate was not applied.");
        });

        run("candidate concurrent apply creates one revision", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            var results = Task.WhenAll(
                scenario.Service.ApplyAsync(new(candidate.CandidateId, candidate.ProjectId, candidate.PlanId, true), scenario.OwnerId),
                scenario.Service.ApplyAsync(new(candidate.CandidateId, candidate.ProjectId, candidate.PlanId, true), scenario.OwnerId)).Result;
            Check(results.Count(result => result.Status == CandidateActionStatus.Applied) == 1,
                "Concurrent apply did not have exactly one winner.");
            Check(results.Count(result => result.Status == CandidateActionStatus.AlreadyApplied) == 1,
                "Concurrent replay did not observe consumed state.");
            Check(scenario.Persistence.RevisionCount == 2, "Concurrent apply created multiple revisions.");
        });

        run("candidate apply versus reject race has one terminal winner", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            var apply = Task.Run(() => scenario.Apply(candidate));
            var reject = Task.Run(() => scenario.Reject(candidate));
            Task.WaitAll(apply, reject);
            var statuses = new[] { apply.Result.Status, reject.Result.Status };
            Check(statuses.Count(status => status is CandidateActionStatus.Applied or CandidateActionStatus.Rejected) == 1,
                "Apply/reject race did not have exactly one winner.");
            Check(scenario.Persistence.RevisionCount is 1 or 2, "Race produced an impossible revision count.");
            Check(scenario.Persistence.RevisionCount == (statuses.Contains(CandidateActionStatus.Applied) ? 2 : 1),
                "Revision count does not match the race winner.");
        });

        run("candidate transaction failure rolls back candidate and revision", () =>
        {
            var scenario = new Scenario();
            var candidate = scenario.Preview().Candidate!;
            scenario.Persistence.ThrowBeforeCommit = true;
            Check(scenario.Apply(candidate).Status == CandidateActionStatus.PersistenceFailure,
                "Transaction failure was hidden.");
            Check(scenario.Persistence.RevisionCount == 1, "Failed transaction appended a revision.");
            Check(scenario.Persistence.GetCandidate(candidate.CandidateId)!.Status == CandidatePreviewStatus.Pending,
                "Failed transaction consumed the candidate.");
        });

        run("candidate preview rejects stale requested base", () =>
        {
            var scenario = new Scenario();
            Check(scenario.Preview(baseRevisionId: Guid.NewGuid()).Status
                == CandidatePreviewResultStatus.StaleBaseRevision, "Stale preview base was accepted.");
        });

        run("candidate preview rejects missing land or requirement dependency", () =>
        {
            var missingLand = new Scenario { LandAvailable = false };
            var missingRequirements = new Scenario { RequirementsAvailable = false };
            Check(missingLand.Preview().Status == CandidatePreviewResultStatus.DependencyUnavailable,
                "Missing land dependency produced a candidate.");
            Check(missingRequirements.Preview().Status == CandidatePreviewResultStatus.DependencyUnavailable,
                "Missing requirement dependency produced a candidate.");
        });

        run("candidate preview rejects invalid command", () =>
        {
            var scenario = new Scenario();
            using var payload = JsonDocument.Parse("{}");
            var invalid = new PlanEditCommandDto
            {
                CommandId = Guid.NewGuid(), CommandType = PlanEditCommandTypes.MoveRoom,
                FloorId = Guid.NewGuid(), TargetId = Guid.NewGuid(), Payload = payload.RootElement.Clone()
            };
            Check(scenario.Preview(command: invalid).Status == CandidatePreviewResultStatus.InvalidCommand,
                "Invalid command reached compute dependency.");
        });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Scenario
    {
        public Guid OwnerId { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = Guid.NewGuid();
        public Guid PlanId { get; } = Guid.NewGuid();
        public Guid CurrentLandRevisionId { get; set; } = Guid.NewGuid();
        public Guid CurrentRequirementsRevisionId { get; set; } = Guid.NewGuid();
        public TimeSpan Lifetime { get; } = TimeSpan.FromMinutes(15);
        public MutableTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-11-17T00:00:00Z"));
        public CandidatePersistenceTestDouble Persistence { get; }
        public CandidatePreviewService Service { get; }
        public ValidationEvidenceStatus ValidationStatus { get; set; } = ValidationEvidenceStatus.Verified;
        public bool ReturnMismatchedEvidenceHash { get; set; }
        public bool HasBlockingIssues { get; set; }
        public bool HasWarnings { get; set; }
        public bool LandAvailable { get; set; } = true;
        public bool RequirementsAvailable { get; set; } = true;

        public Scenario()
        {
            var geometry = Geometry(ProjectId, PlanId, CurrentLandRevisionId, CurrentRequirementsRevisionId);
            var geometryJson = JsonSerializer.Serialize(geometry, PlanEditorJson.StrictOptions);
            var baseRevision = new PlanRevisionSnapshot(
                Guid.NewGuid(), PlanId, ProjectId, null, 1, geometry.SchemaVersion, geometryJson,
                PlanRevisionService.ComputeGeometryHash(geometryJson), CurrentLandRevisionId,
                CurrentRequirementsRevisionId, Evidence(PlanRevisionService.ComputeGeometryHash(geometryJson)),
                null, OwnerId, Clock.GetUtcNow(), "base");
            Persistence = new CandidatePersistenceTestDouble(baseRevision);

            Service = new CandidatePreviewService(
                new TestProjectOwnershipGate((project, actor, _) =>
                    Task.FromResult(project == ProjectId && actor == OwnerId)),
                Persistence,
                new TestApplicableInputRevisionSource((_, _) => Task.FromResult(
                    new ApplicableInputRevisions(CurrentLandRevisionId, CurrentRequirementsRevisionId))),
                new TestConfirmedLandRevisionPort((project, revision, _) => Task.FromResult<ConfirmedLandRevision?>(
                    LandAvailable ? Land(project, revision) : null)),
                new TestRequirementRevisionPort((project, revision, _) => Task.FromResult<RequirementRevisionReference?>(
                    RequirementsAvailable ? new(project, revision, "test-only/1") : null)),
                new TestPlanEditorComputeClient((request, _) =>
                {
                    var candidate = Geometry(ProjectId, PlanId, CurrentLandRevisionId, CurrentRequirementsRevisionId);
                    return Task.FromResult(new PlanEditResultDto
                    {
                        SchemaVersion = candidate.SchemaVersion,
                        CommandId = request.Command.CommandId,
                        BaseRevisionId = Persistence.CurrentRevision.RevisionId,
                        Status = PlanEditStatus.Accepted,
                        CandidateGeometry = candidate,
                        Diagnostics = [],
                        ChangedEntityIds = []
                    });
                }),
                new CandidateValidationEvidenceSourceTestDouble((_, _, hash, _) =>
                    Task.FromResult<PlanValidationReferenceDto?>(Evidence(
                        ReturnMismatchedEvidenceHash ? "sha256:mismatch" : hash))),
                new TestPlanValidationEvidenceVerifier((_, _, _, _, _) => Task.FromResult(ValidationStatus)),
                Persistence, Persistence, Clock, Lifetime);
        }

        public CandidatePreviewResult Preview(
            Guid? actor = null, Guid? baseRevisionId = null, PlanEditCommandDto? command = null) =>
            Service.PreviewAsync(new(
                ProjectId, PlanId, baseRevisionId ?? Persistence.CurrentRevision.RevisionId,
                command ?? ValidCommand(), "candidate-contract-test"), actor ?? OwnerId).Result;

        public CandidateActionResult Apply(
            CandidatePreviewSnapshot candidate, Guid? actor = null, bool acknowledgeWarnings = true) =>
            Service.ApplyAsync(new(candidate.CandidateId, candidate.ProjectId, candidate.PlanId,
                acknowledgeWarnings, "candidate apply"), actor ?? OwnerId).Result;

        public CandidateActionResult Reject(CandidatePreviewSnapshot candidate) =>
            Service.RejectAsync(new(candidate.CandidateId, candidate.ProjectId, candidate.PlanId), OwnerId).Result;

        private PlanValidationReferenceDto Evidence(string hash) => new()
        {
            ValidationId = Guid.NewGuid(), ValidatorVersion = "explicit-candidate-test-double/1",
            GeometryHash = hash, GeometryValid = !HasBlockingIssues,
            RequirementsMatch = HasWarnings ? false : true,
            RegulatoryEvidenceStatus = HasWarnings ? "NOT_VERIFIED" : "VERIFIED",
            HasBlockingIssues = HasBlockingIssues, ValidatedAtUtc = Clock.GetUtcNow()
        };
    }

    private sealed class CandidateValidationEvidenceSourceTestDouble(
        Func<Guid, Guid, string, CancellationToken, Task<PlanValidationReferenceDto?>> handler)
        : ICandidateValidationEvidenceSource
    {
        public Task<PlanValidationReferenceDto?> GetAsync(
            Guid projectId, Guid planId, string exactGeometryHash, CancellationToken cancellationToken) =>
            handler(projectId, planId, exactGeometryHash, cancellationToken);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private sealed class CandidatePersistenceTestDouble(PlanRevisionSnapshot initial)
        : ICandidatePreviewStore, ICandidatePreviewTransaction, IPlanRevisionStore
    {
        private readonly object sync = new();
        private readonly Dictionary<Guid, CandidatePreviewSnapshot> candidates = [];
        private readonly List<PlanRevisionSnapshot> revisions = [initial];
        public bool ThrowBeforeCommit { get; set; }
        public int RevisionCount { get { lock (sync) return revisions.Count; } }
        public int CandidateCount { get { lock (sync) return candidates.Count; } }
        public PlanRevisionSnapshot CurrentRevision { get { lock (sync) return revisions.MaxBy(x => x.VersionNumber)!; } }

        public Task CreateAsync(CandidatePreviewSnapshot candidate, CancellationToken cancellationToken)
        {
            lock (sync) candidates.Add(candidate.CandidateId, candidate);
            return Task.CompletedTask;
        }

        public Task<CandidatePreviewSnapshot?> GetAsync(Guid candidateId, CancellationToken cancellationToken)
        {
            lock (sync) return Task.FromResult(candidates.GetValueOrDefault(candidateId));
        }

        public CandidatePreviewSnapshot? GetCandidate(Guid id) { lock (sync) return candidates.GetValueOrDefault(id); }
        public void Replace(CandidatePreviewSnapshot candidate) { lock (sync) candidates[candidate.CandidateId] = candidate; }

        public void AdvanceCurrentRevision()
        {
            lock (sync)
            {
                var current = revisions.MaxBy(x => x.VersionNumber)!;
                revisions.Add(current with
                {
                    RevisionId = Guid.NewGuid(), ParentRevisionId = current.RevisionId,
                    VersionNumber = current.VersionNumber + 1
                });
            }
        }

        public Task<CandidateActionResult> TryApplyAtomicallyAsync(
            CandidatePreviewSnapshot expected, PlanRevisionSnapshot proposed, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            lock (sync)
            {
                if (ThrowBeforeCommit) throw new InvalidOperationException("Explicit candidate transaction failure.");
                var currentCandidate = candidates.GetValueOrDefault(expected.CandidateId);
                var terminal = Guard(currentCandidate, expected, now);
                if (terminal is not null) return Task.FromResult(terminal);
                if (revisions.MaxBy(x => x.VersionNumber)!.RevisionId != expected.BaseRevisionId)
                    return Task.FromResult(new CandidateActionResult(CandidateActionStatus.StaleBaseRevision, currentCandidate));
                var applied = currentCandidate! with { Status = CandidatePreviewStatus.Applied };
                candidates[expected.CandidateId] = applied;
                revisions.Add(proposed);
                return Task.FromResult(new CandidateActionResult(CandidateActionStatus.Applied, applied, proposed));
            }
        }

        public Task<CandidateActionResult> TryRejectAtomicallyAsync(
            CandidatePreviewSnapshot expected, DateTimeOffset now, CancellationToken cancellationToken)
        {
            lock (sync)
            {
                if (ThrowBeforeCommit) throw new InvalidOperationException("Explicit candidate transaction failure.");
                var current = candidates.GetValueOrDefault(expected.CandidateId);
                var terminal = Guard(current, expected, now);
                if (terminal is not null) return Task.FromResult(terminal);
                var rejected = current! with { Status = CandidatePreviewStatus.Rejected };
                candidates[expected.CandidateId] = rejected;
                return Task.FromResult(new CandidateActionResult(CandidateActionStatus.Rejected, rejected));
            }
        }

        public Task<CandidateActionResult> TryExpireAtomicallyAsync(
            CandidatePreviewSnapshot expected, DateTimeOffset now, CancellationToken cancellationToken)
        {
            lock (sync)
            {
                var current = candidates.GetValueOrDefault(expected.CandidateId);
                var terminal = Guard(current, expected, now, allowExpiry: true);
                if (terminal is not null) return Task.FromResult(terminal);
                if (now < current!.ExpiresAtUtc)
                    return Task.FromResult(new CandidateActionResult(CandidateActionStatus.PersistenceFailure, current));
                var expired = current with { Status = CandidatePreviewStatus.Expired };
                candidates[expected.CandidateId] = expired;
                return Task.FromResult(new CandidateActionResult(CandidateActionStatus.Expired, expired));
            }
        }

        private static CandidateActionResult? Guard(
            CandidatePreviewSnapshot? current, CandidatePreviewSnapshot expected, DateTimeOffset now,
            bool allowExpiry = false)
        {
            if (current is null) return new(CandidateActionStatus.NotFound);
            if (current.OwnerId != expected.OwnerId || current.ProjectId != expected.ProjectId ||
                current.PlanId != expected.PlanId)
                return new(CandidateActionStatus.Unauthorized, current);
            if (current.Status == CandidatePreviewStatus.Applied)
                return new(CandidateActionStatus.AlreadyApplied, current);
            if (current.Status == CandidatePreviewStatus.Rejected)
                return new(CandidateActionStatus.AlreadyRejected, current);
            if (current.Status == CandidatePreviewStatus.Expired)
                return new(CandidateActionStatus.Expired, current);
            if (!allowExpiry && now >= current.ExpiresAtUtc)
                return new(CandidateActionStatus.Expired, current);
            return null;
        }

        public Task<PlanRevisionSnapshot?> GetAsync(
            Guid projectId, Guid planId, Guid revisionId, CancellationToken cancellationToken)
        {
            lock (sync) return Task.FromResult(revisions.SingleOrDefault(
                x => x.ProjectId == projectId && x.PlanId == planId && x.RevisionId == revisionId));
        }

        public Task<PlanRevisionSnapshot?> GetCurrentAsync(
            Guid projectId, Guid planId, CancellationToken cancellationToken)
        {
            lock (sync) return Task.FromResult(revisions.Where(
                x => x.ProjectId == projectId && x.PlanId == planId).MaxBy(x => x.VersionNumber));
        }

        public Task<IReadOnlyList<PlanRevisionSnapshot>> GetHistoryAsync(
            Guid projectId, Guid planId, CancellationToken cancellationToken)
        {
            lock (sync) return Task.FromResult<IReadOnlyList<PlanRevisionSnapshot>>(
                revisions.Where(x => x.ProjectId == projectId && x.PlanId == planId).ToArray());
        }

        public Task<AppendRevisionResult> TryAppendAtomicallyAsync(
            Guid expectedBaseRevisionId, PlanRevisionSnapshot proposedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Candidate tests require the combined candidate/revision transaction.");
    }

    private static ConfirmedLandRevision Land(Guid projectId, Guid revisionId)
    {
        using var document = JsonDocument.Parse("{\"type\":\"Polygon\",\"coordinates\":[]}");
        return new(projectId, revisionId, "test-only/1", document.RootElement.Clone());
    }

    private static PlanEditCommandDto ValidCommand()
    {
        using var payload = JsonDocument.Parse("{\"deltaX\":1,\"deltaY\":0}");
        return new()
        {
            CommandId = Guid.NewGuid(), CommandType = PlanEditCommandTypes.MoveRoom,
            FloorId = Guid.NewGuid(), TargetId = Guid.NewGuid(), Payload = payload.RootElement.Clone()
        };
    }

    private static PlanGeometryDto Geometry(Guid projectId, Guid planId, Guid landId, Guid requirementsId) => new()
    {
        SchemaVersion = GeometrySchemaInfo.DraftVersion,
        ProjectId = projectId,
        PlanId = planId,
        LandRevisionId = landId,
        RequirementsRevisionId = requirementsId,
        CoordinateSystem = new CoordinateSystemDto
        {
            Type = "localCartesian", Unit = "m", Origin = new CoordinatePointDto { X = 0, Y = 0 },
            XDirection = "right", YDirection = "up"
        },
        Floors = [new FloorDto { Id = Guid.NewGuid(), Level = 0, Rooms = [], Walls = [], Openings = [] }]
    };
}
