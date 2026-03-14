using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc;
using ReachTether.Server;
using ReachTether.Server.Components;
using ReachTether.Server.Services;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
builder.Services.AddSingleton<ISqliteSchemaInitializer, SqliteSchemaInitializer>();
builder.Services.AddSingleton<ISessionStore, SqliteSessionStore>();
builder.Services.AddSingleton<IMemoryPromotionService, MemoryPromotionService>();
builder.Services.AddSingleton<IMemoryRetrievalService, MemoryRetrievalService>();
builder.Services.AddSingleton<ISnapshotStore, FileSnapshotStore>();
builder.Services.AddSingleton<IToolExecutionService, ToolExecutionService>();
builder.Services.AddSingleton<ISmartyModeService, SmartyModeService>();
builder.Services.AddSingleton<INamedMemoryEmbeddingProvider, LocalMemoryEmbeddingProviderStub>();
builder.Services.AddSingleton<IMemoryEmbeddingProvider, ConfigurableMemoryEmbeddingProvider>();
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
builder.Services.AddHttpClient<OpenAiMemoryEmbeddingProvider>((sp, client) =>
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
builder.Services.AddSingleton<INamedMemoryEmbeddingProvider>(sp => sp.GetRequiredService<OpenAiMemoryEmbeddingProvider>());

var app = builder.Build();
await app.Services.GetRequiredService<ISqliteSchemaInitializer>().InitializeAsync(CancellationToken.None);

app.UseStaticFiles();
app.UseAntiforgery();

app.MapPost("/api/sessions/start-or-resume", async (
    [FromBody] StartOrResumeSessionRequest request,
    ISessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    var response = await sessionStore.StartOrResumeSessionAsync(request, cancellationToken);
    return Results.Json(response);
});

app.MapPost("/api/session-turns", async (
    [FromBody] PersistSessionTurnRequest request,
    ISessionStore sessionStore,
    IMemoryPromotionService promotionService,
    CancellationToken cancellationToken) =>
{
    var response = await sessionStore.PersistSessionTurnAsync(request, cancellationToken);
    await promotionService.ProcessPersistedTurnAsync(request, cancellationToken);
    return Results.Json(response with
    {
        SessionSummary = await sessionStore.GetSessionSummaryAsync(request.SessionId, cancellationToken)
    });
});

app.MapPost("/api/knowledge/query", async (
    [FromBody] KnowledgeQueryRequest request,
    IMemoryRetrievalService retrievalService,
    CancellationToken cancellationToken) =>
{
    var response = await retrievalService.QueryAsync(request, cancellationToken);
    return Results.Json(response);
});

app.MapPost("/api/memory/promote", async (
    [FromBody] PromoteMemoryRequest request,
    IMemoryPromotionService promotionService,
    CancellationToken cancellationToken) =>
{
    var response = await promotionService.PromoteAsync(request, cancellationToken);
    return Results.Json(response);
});

app.MapPost("/api/memory/reindex", async (
    [FromBody] ReindexMemoryRequest request,
    IMemoryPromotionService promotionService,
    CancellationToken cancellationToken) =>
{
    var response = await promotionService.ReindexAsync(request, cancellationToken);
    return Results.Json(response);
});

app.MapGet("/api/memory/search", async (
    [FromQuery] string? sessionId,
    [FromQuery] string? query,
    [FromQuery] bool includeArchived,
    [FromQuery] int? topK,
    ISessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    var hits = await sessionStore.SearchMemoryForAdminAsync(sessionId, query, includeArchived, topK.GetValueOrDefault(10), cancellationToken);
    return Results.Json(new MemorySearchResponse(hits.Select(static hit => new RetrievedMemoryItem(
        hit.MemoryId,
        hit.Title,
        hit.Summary ?? MemoryPromotionService.Summarize(hit.Content, 180),
        hit.Kind,
        hit.Scope,
        hit.SourceTurnId,
        hit.TextScore)).ToArray()));
});

app.MapPost("/api/memory/{id}/archive", async (
    string id,
    ISessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    var response = await sessionStore.ArchiveMemoryAsync(id, cancellationToken);
    return Results.Json(response);
});

app.MapPost("/api/memory/{id}/restore", async (
    string id,
    ISessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    var response = await sessionStore.RestoreMemoryAsync(id, cancellationToken);
    return Results.Json(response);
});

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
    ISessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    var snapshot = await snapshotStore.SaveAsync(request, cancellationToken);
    var contentUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/artifacts/{snapshot.ArtifactId}/content";
    await sessionStore.RecordArtifactMetadataAsync(
        new PersistedArtifactDescriptor(
            snapshot.ArtifactId,
            snapshot.TurnId,
            snapshot.ToolCallId,
            request.Kind,
            snapshot.ContentType,
            snapshot.FilePath,
            JsonSerializer.Serialize(snapshot.Metadata),
            snapshot.CreatedAt),
        snapshot.SessionId,
        cancellationToken);
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

public partial class Program;
