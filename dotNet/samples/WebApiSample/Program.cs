using Microsoft.AspNetCore.Mvc;
using ReachyMini.Sdk;
using ReachyMini.Sdk.Models;

var builder = WebApplication.CreateBuilder(args);

// Add ReachyMini client with configuration from appsettings.json
builder.Services.AddReachyMiniClient(
    builder.Configuration.GetSection("ReachyMini"));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Get daemon status
app.MapGet("/api/status", async (ReachyMiniClient client) =>
{
    var status = await client.Daemon.GetStatusAsync();
    return Results.Ok(new
    {
        robotName = status.RobotName,
        state = status.State.ToString(),
        version = status.Version,
        backendStatus = status.BackendStatus
    });
})
.WithName("GetDaemonStatus")
.WithOpenApi();

// Wake up robot
app.MapPost("/api/robot/wakeup", async (ReachyMiniClient client) =>
{
    var moveId = await client.Move.WakeUpAsync();
    return Results.Ok(new { moveId = moveId.Uuid, action = "wake_up" });
})
.WithName("WakeUpRobot")
.WithOpenApi();

// Put robot to sleep
app.MapPost("/api/robot/sleep", async (ReachyMiniClient client) =>
{
    var moveId = await client.Move.GotoSleepAsync();
    return Results.Ok(new { moveId = moveId.Uuid, action = "sleep" });
})
.WithName("SleepRobot")
.WithOpenApi();

// Get robot state
app.MapGet("/api/robot/state", async (ReachyMiniClient client) =>
{
    var state = await client.State.GetFullStateAsync();
    return Results.Ok(new
    {
        controlMode = state.ControlMode.ToString(),
        bodyYaw = state.BodyYaw,
        headPose = state.HeadPose,
        antennasPosition = state.AntennasPosition,
        timestamp = state.Timestamp
    });
})
.WithName("GetRobotState")
.WithOpenApi();

// Move robot head
app.MapPost("/api/robot/goto", async (
    [FromBody] GotoModelRequest request,
    ReachyMiniClient client) =>
{
    var moveId = await client.Move.GotoAsync(request);
    return Results.Ok(new { moveId = moveId.Uuid });
})
.WithName("MoveRobot")
.WithOpenApi();

// List installed apps
app.MapGet("/api/apps", async (ReachyMiniClient client) =>
{
    var apps = await client.Apps.GetInstalledAppsAsync();
    return Results.Ok(apps);
})
.WithName("GetInstalledApps")
.WithOpenApi();

// Start daemon
app.MapPost("/api/daemon/start", async (ReachyMiniClient client) =>
{
    var status = await client.Daemon.StartAsync();
    return Results.Ok(status);
})
.WithName("StartDaemon")
.WithOpenApi();

// Stop daemon
app.MapPost("/api/daemon/stop", async (ReachyMiniClient client) =>
{
    var status = await client.Daemon.StopAsync();
    return Results.Ok(status);
})
.WithName("StopDaemon")
.WithOpenApi();

app.Run();
