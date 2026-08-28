// Composition root. Each stream contributes an AddXxx extension method rather than editing
// this file, so the wiring stays one readable page.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();
