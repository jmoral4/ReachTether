using ReachyMini.Sdk;
using ReachyMini.Sdk.Configuration;
using ReachyMini.Sdk.Models;
using Microsoft.Extensions.Options;

// Configure the client
var options = Options.Create(new ReachyMiniOptions
{
    BaseUrl = "http://localhost:8000",
    Timeout = TimeSpan.FromSeconds(30)
});

var httpClient = new HttpClient();
var client = new ReachyMiniClient(httpClient, options);

try
{
    Console.WriteLine("=== Reachy Mini Robot Control Demo ===\n");

    // Get daemon status
    Console.WriteLine("Getting daemon status...");
    var status = await client.Daemon.GetStatusAsync();
    Console.WriteLine($"Robot Name: {status.RobotName}");
    Console.WriteLine($"State: {status.State}");
    Console.WriteLine($"Version: {status.Version}");
    Console.WriteLine($"Backend Status: {status.BackendStatus}\n");

    // Wake up the robot
    Console.WriteLine("Waking up the robot...");
    var wakeUpMove = await client.Move.WakeUpAsync();
    Console.WriteLine($"Wake up move started: {wakeUpMove.Uuid}\n");

    // Wait a bit for wake up to complete
    await Task.Delay(2000);

    // Get full robot state
    Console.WriteLine("Getting robot state...");
    var state = await client.State.GetFullStateAsync();
    Console.WriteLine($"Control Mode: {state.ControlMode}");
    Console.WriteLine($"Body Yaw: {state.BodyYaw}°");
    if (state.HeadPose != null && state.HeadPose is XYZRPYPose headPose)
    {
        Console.WriteLine($"Head Position: X={headPose.X:F2}, Y={headPose.Y:F2}, Z={headPose.Z:F2}");
    }
    Console.WriteLine();

    // Get motor status
    Console.WriteLine("Getting motor status...");
    var motors = await client.Motors.GetStatusAsync();
    Console.WriteLine($"Motor control mode: {motors.Mode}\n");

    // Put robot to sleep
    Console.WriteLine("Putting robot to sleep...");
    var sleepMove = await client.Move.GotoSleepAsync();
    Console.WriteLine($"Sleep move started: {sleepMove.Uuid}");

    Console.WriteLine("\n=== Demo Complete ===");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    if (ex.InnerException != null)
    {
        Console.WriteLine($"Inner Error: {ex.InnerException.Message}");
    }
}
