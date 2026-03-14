using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc;
using ReachTether.Server;
using ReachTether.Server.Components;
using ReachTether.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ISnapshotStore, FileSnapshotStore>();
builder.Services.AddSingleton<IToolExecutionService, ToolExecutionService>();
builder.Services.AddSingleton<ISmartyModeService, SmartyModeService>();
builder.Services.AddHttpClient<ISmartyModeClient, SmartyModeClient>((sp, client) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var baseUrl = configuration["OpenAI:BaseUrl"] ?? "https://api.openai.com/v1/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(60);
    var apiKey = configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }
    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
});

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapPost("/api/tools/execute", async (
    [FromBody] RemoteToolExecutionRequest request,
    IToolExecutionService toolExecutionService,
    CancellationToken cancellationToken) =>
{
    var response = await toolExecutionService.ExecuteAsync(request, cancellationToken);
    return Results.Json(response);
});

app.MapPost("/api/snapshots", async (
    [FromBody] SnapshotUploadRequest request,
    ISnapshotStore snapshotStore,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var snapshot = await snapshotStore.SaveAsync(request, cancellationToken);
    var contentUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/artifacts/{snapshot.ArtifactId}/content";
    return Results.Json(new SnapshotUploadResponse(snapshot.ArtifactId, contentUrl, snapshot.CreatedAt));
});

app.MapGet("/artifacts/{artifactId}/content", (
    string artifactId,
    ISnapshotStore snapshotStore) =>
{
    if (!snapshotStore.TryGet(artifactId, out var artifact))
    {
        return Results.NotFound();
    }

    return Results.File(File.OpenRead(artifact.FilePath), artifact.ContentType);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
