var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { service = "buildmind-api", status = "ok" }));
app.Run();
