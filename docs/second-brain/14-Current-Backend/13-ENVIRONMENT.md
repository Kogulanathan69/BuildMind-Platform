# Environment and setup

Historical baseline .NET SDK 10.0.401, Python 3.13.16; target net10.0, MySQL 8.4 LTS, Angular 20, TypeScript, Tailwind, Konva.js, Mapbox. Verify exact versions and compatible EF Core MySQL provider before package selection. No unrequested Next.js/MSSQL/PostgreSQL. Python dependencies as actual requirements, add Shapely/PyProj/GeoPandas/OCR only where required; approve lock/pins. Never install arbitrary packages solely to hide errors.

Read-only Windows audit from real repo root:
```powershell
git status -sb
git branch --show-current
git remote -v
git log -5 --oneline
dotnet --info
py -3.13 --version
Get-Service *mysql* -ErrorAction SilentlyContinue
Get-ChildItem src -Recurse -Filter *.csproj
Get-ChildItem python-services -Force
```
Inspect current repository and `.env.example` redacting values. Local installation commands are conditional after approval; use project venv (not global packages). Existing smoke commands historically: `dotnet build src/BuildMind.API/BuildMind.API.csproj`; in python-services `\.venv\Scripts\python.exe -m pytest` (use `.\.venv\Scripts\python.exe` in PowerShell). Verify DB port/target and credentials securely; do not print secrets. Use separate disposable DB for tests. Do not claim MySQL integrated merely because server installed. C# /health liveness separate readiness. Ports/config names discover from actual appsettings and environment; proposed configuration keys tracked after audit.
