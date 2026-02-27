# ReachyMini.Sdk

**Unofficial, open source** .NET SDK for the Reachy Mini Robot API. This library provides a strongly-typed, production-ready client for controlling and interacting with Reachy Mini robots.

> ⚠️ **Note**: This is an unofficial SDK created as an open source project by Akshay Kokane. It is not affiliated with or endorsed by Pollen Robotics.

[![NuGet](https://img.shields.io/nuget/v/ReachyMini.Sdk.svg)](https://www.nuget.org/packages/ReachyMini.Sdk/)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)

## Features

- 🎯 **Strongly-typed** - Full type safety with C# models
- 🔌 **Dependency Injection** - First-class DI support for ASP.NET Core
- ⚡ **Async/Await** - Modern async API throughout
- 🛡️ **Error Handling** - Comprehensive exception handling
- 📦 **Production Ready** - Built for reliability and performance
- 🎨 **Clean API** - Intuitive and easy to use
- 🔄 **Retry Logic** - Configurable retry policies
- 📝 **Well Documented** - XML documentation for IntelliSense

## Installation

Install via NuGet Package Manager:

```bash
dotnet add package ReachyMini.Sdk
```

Or via Package Manager Console:

```powershell
Install-Package ReachyMini.Sdk
```

## Quick Start

### Basic Usage (Without DI)

```csharp
using ReachyMini.Sdk;
using ReachyMini.Sdk.Configuration;
using Microsoft.Extensions.Options;

var options = Options.Create(new ReachyMiniOptions
{
    BaseUrl = "http://localhost:8080",
    Timeout = TimeSpan.FromSeconds(30)
});

var httpClient = new HttpClient();
var client = new ReachyMiniClient(httpClient, options);

// Get daemon status
var status = await client.Daemon.GetStatusAsync();
Console.WriteLine($"Robot: {status.RobotName}, State: {status.State}");

// Wake up the robot
var moveId = await client.Move.WakeUpAsync();
Console.WriteLine($"Wake up started: {moveId.Uuid}");

// Get current robot state
var state = await client.State.GetFullStateAsync();
Console.WriteLine($"Body Yaw: {state.BodyYaw}");
```

### ASP.NET Core Integration

#### 1. Configure in `Program.cs` or `Startup.cs`

```csharp
using ReachyMini.Sdk;

var builder = WebApplication.CreateBuilder(args);

// Simple configuration
builder.Services.AddReachyMiniClient("http://192.168.1.100:8080");

// Or with options
builder.Services.AddReachyMiniClient(options =>
{
    options.BaseUrl = "http://192.168.1.100:8080";
    options.Timeout = TimeSpan.FromSeconds(60);
    options.RetryCount = 3;
});

// Or from appsettings.json
builder.Services.AddReachyMiniClient(
    builder.Configuration.GetSection("ReachyMini"));

var app = builder.Build();
```

#### 2. Configure in `appsettings.json`

```json
{
  "ReachyMini": {
    "BaseUrl": "http://192.168.1.100:8080",
    "Timeout": "00:00:30",
    "ThrowOnError": true,
    "RetryCount": 3,
    "RetryDelay": "00:00:01"
  }
}
```

#### 3. Use in Controllers or Services

```csharp
using Microsoft.AspNetCore.Mvc;
using ReachyMini.Sdk;
using ReachyMini.Sdk.Models;

[ApiController]
[Route("api/[controller]")]
public class RobotController : ControllerBase
{
    private readonly ReachyMiniClient _reachyClient;

    public RobotController(ReachyMiniClient reachyClient)
    {
        _reachyClient = reachyClient;
    }

    [HttpGet("status")]
    public async Task<ActionResult<DaemonStatus>> GetStatus()
    {
        var status = await _reachyClient.Daemon.GetStatusAsync();
        return Ok(status);
    }

    [HttpPost("wake-up")]
    public async Task<ActionResult<MoveUUID>> WakeUp()
    {
        var result = await _reachyClient.Move.WakeUpAsync();
        return Ok(result);
    }

    [HttpPost("goto")]
    public async Task<ActionResult<MoveUUID>> Goto([FromBody] GotoModelRequest request)
    {
        var result = await _reachyClient.Move.GotoAsync(request);
        return Ok(result);
    }

    [HttpGet("state")]
    public async Task<ActionResult<FullState>> GetState()
    {
        var state = await _reachyClient.State.GetFullStateAsync();
        return Ok(state);
    }
}
```

## API Clients

The SDK is organized into specialized clients:

### `Apps` - Application Management
```csharp
// List all available apps
var apps = await client.Apps.ListAllAvailableAppsAsync();

// Install an app
var jobId = await client.Apps.InstallAppAsync(appInfo);

// Start an app
var status = await client.Apps.StartAppAsync("my-app");

// Get current app status
var currentApp = await client.Apps.GetCurrentAppStatusAsync();
```

### `Daemon` - Daemon Control
```csharp
// Start daemon and wake up robot
await client.Daemon.StartAsync(wakeUp: true);

// Get daemon status
var status = await client.Daemon.GetStatusAsync();

// Stop daemon and put robot to sleep
await client.Daemon.StopAsync(gotoSleep: true);
```

### `Motors` - Motor Control
```csharp
// Get motor status
var status = await client.Motors.GetStatusAsync();

// Enable motors
await client.Motors.SetModeAsync(MotorControlMode.Enabled);

// Enable gravity compensation
await client.Motors.SetModeAsync(MotorControlMode.GravityCompensation);
```

### `Move` - Movement Control
```csharp
// Wake up the robot
var wakeUpId = await client.Move.WakeUpAsync();

// Move to a specific pose
var gotoRequest = new GotoModelRequest
{
    HeadPose = new XYZRPYPose { X = 0.2, Y = 0, Z = 0.3, Roll = 0, Pitch = 0, Yaw = 0 },
    Antennas = new[] { 0.0, 0.0 },
    BodyYaw = 0.0,
    Duration = 2.0,
    Interpolation = InterpolationMode.Minjerk
};
var moveId = await client.Move.GotoAsync(gotoRequest);

// Play a recorded move
await client.Move.PlayRecordedMoveAsync("dataset1", "wave");

// Stop a move
await client.Move.StopMoveAsync(moveId);
```

### `State` - Robot State
```csharp
// Get full robot state
var state = await client.State.GetFullStateAsync(
    withControlMode: true,
    withHeadPose: true,
    withBodyYaw: true,
    withAntennaPositions: true
);

// Get specific state information
var headPose = await client.State.GetHeadPoseAsync();
var bodyYaw = await client.State.GetBodyYawAsync();
var antennas = await client.State.GetAntennaJointPositionsAsync();
```

### `Volume` - Audio Control
```csharp
// Get current volume
var volume = await client.Volume.GetVolumeAsync();

// Set volume
await client.Volume.SetVolumeAsync(new VolumeRequest { Volume = 75 });

// Play test sound
await client.Volume.PlayTestSoundAsync();

// Control microphone volume
await client.Volume.SetMicrophoneVolumeAsync(new VolumeRequest { Volume = 80 });
```

### `Auth` - HuggingFace Authentication
```csharp
// Save HuggingFace token
var response = await client.Auth.SaveTokenAsync(new TokenRequest { Token = "hf_..." });

// Check auth status
var status = await client.Auth.GetAuthStatusAsync();

// Delete token
await client.Auth.DeleteTokenAsync();
```

## Error Handling

The SDK provides custom exceptions for different error scenarios:

```csharp
using ReachyMini.Sdk.Exceptions;

try
{
    var status = await client.Daemon.GetStatusAsync();
}
catch (RobotNotAvailableException ex)
{
    // Robot is not reachable
    Console.WriteLine($"Cannot connect to robot: {ex.Message}");
}
catch (ReachyMiniApiException ex)
{
    // API returned an error
    Console.WriteLine($"API Error [{ex.StatusCode}]: {ex.Message}");
    Console.WriteLine($"Response: {ex.ResponseContent}");
}
catch (ReachyMiniException ex)
{
    // General SDK error
    Console.WriteLine($"SDK Error: {ex.Message}");
}
```

## Configuration Options

```csharp
public class ReachyMiniOptions
{
    // Base URL of the Reachy Mini API
    public string BaseUrl { get; set; } = "http://localhost:8080";
    
    // HTTP request timeout
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    
    // Throw exceptions on API errors
    public bool ThrowOnError { get; set; } = true;
    
    // Number of retry attempts
    public int RetryCount { get; set; } = 3;
    
    // Delay between retries
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}
```

## Examples

### Complete Movement Sequence

```csharp
// Wake up the robot
var wakeUpMove = await client.Move.WakeUpAsync();

// Wait for wake up to complete
await Task.Delay(3000);

// Enable motors
await client.Motors.SetModeAsync(MotorControlMode.Enabled);

// Move head to look around
var lookRequest = new GotoModelRequest
{
    HeadPose = new XYZRPYPose 
    { 
        X = 0.25, Y = 0.1, Z = 0.3, 
        Roll = 0, Pitch = 0.2, Yaw = 0.3 
    },
    Duration = 2.0,
    Interpolation = InterpolationMode.Minjerk
};
await client.Move.GotoAsync(lookRequest);

// Return to center
var centerRequest = new GotoModelRequest
{
    HeadPose = new XYZRPYPose 
    { 
        X = 0.25, Y = 0, Z = 0.3, 
        Roll = 0, Pitch = 0, Yaw = 0 
    },
    Duration = 1.5
};
await client.Move.GotoAsync(centerRequest);

// Go to sleep
await client.Move.GotoSleepAsync();
```

### Monitor Robot State

```csharp
// Continuously monitor robot state
var cts = new CancellationTokenSource();

Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            var state = await client.State.GetFullStateAsync(
                cancellationToken: cts.Token);
            
            Console.WriteLine($"Control Mode: {state.ControlMode}");
            Console.WriteLine($"Body Yaw: {state.BodyYaw:F2} rad");
            
            if (state.AntennasPosition != null)
            {
                Console.WriteLine($"Antennas: L={state.AntennasPosition[0]:F2}, " +
                                $"R={state.AntennasPosition[1]:F2}");
            }
            
            await Task.Delay(100, cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}, cts.Token);

// Stop monitoring after 10 seconds
await Task.Delay(10000);
cts.Cancel();
```

## Requirements

- .NET 8.0 or .NET 9.0
- Reachy Mini robot with API running on network

## License

Apache 2.0 - See [LICENSE](LICENSE) for details

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Support

For issues and questions:
- GitHub Issues: [Create an issue](https://github.com/pollen-robotics/reachy-mini/issues)
- Documentation: [Reachy Mini Docs](https://docs.pollen-robotics.com/)

## Related Projects

- [Reachy Mini](https://github.com/pollen-robotics/reachy-mini) - Main robot project
- [Pollen Robotics](https://www.pollen-robotics.com/) - Official website
