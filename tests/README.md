# Tests

C# tests are in the solution `BuildMind.sln` (added BE-M4-D01a, 2026-11-17):

- `BuildMind.Infrastructure.Tests` (M4): API liveness contract, truthful readiness, Clean Architecture layer-direction rules (csproj and compiled references), DI container validation. xUnit v2 + `Microsoft.AspNetCore.Mvc.Testing` (in-memory test host; no MySQL, Python or network required).

Also present (M3, merged `b03430e`): `BuildMind.Application.ContractTests` — a console contract-check runner (`OutputType Exe`, not xUnit), not in `BuildMind.sln` and not run by CI; adding it to the solution/CI needs M3 agreement (G18).

Run from the repository root: `dotnet test BuildMind.sln`. Feature owners add their own test projects in coordination with M4 (solution file custodian). The Python starter test lives under `python-services/tests`.

Known local limitation: on a Windows machine with Smart App Control enabled, the test host may be blocked from loading the unsigned API assembly (see G17 in [open decisions](../docs/second-brain/14-Current-Backend/15-OPEN-DECISIONS.md)). Do not disable or work around the control; CI on Linux is the intended execution path pending a decision.
