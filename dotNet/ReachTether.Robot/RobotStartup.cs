using System.Diagnostics;
using ReachyMini.Sdk;
using ReachyMini.Sdk.Models;

internal static class RobotStartup
{
    private static readonly TimeSpan WakeMoveTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MovePollInterval = TimeSpan.FromMilliseconds(200);

    public static async Task EnableMotorsAndWakeAsync(
        ReachyMiniClient reachyClient,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("Checking Reachy Mini motor backend...");
        MotorStatus initialStatus;
        try
        {
            initialStatus = await reachyClient.Motors.GetStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Reachy Mini startup failed because the protected motor-status endpoint is unavailable.",
                ex);
        }

        Console.WriteLine($"Current motor mode: {initialStatus.Mode}.");
        Console.WriteLine("Enabling Reachy Mini motors...");
        try
        {
            await reachyClient.Motors.SetModeAsync(MotorControlMode.Enabled, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Reachy Mini startup failed while enabling the motors.", ex);
        }

        MotorStatus enabledStatus;
        try
        {
            enabledStatus = await reachyClient.Motors.GetStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Reachy Mini startup failed while verifying that the motors were enabled.",
                ex);
        }

        if (enabledStatus.Mode != MotorControlMode.Enabled)
        {
            throw new InvalidOperationException(
                $"Reachy Mini startup failed: requested motor mode Enabled, but the daemon reported {enabledStatus.Mode}.");
        }

        Console.WriteLine("Motor mode verified as enabled. Waking up Reachy Mini...");
        MoveUUID wakeMove;
        try
        {
            wakeMove = await reachyClient.Move.WakeUpAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Reachy Mini startup failed while requesting the wake move.", ex);
        }

        try
        {
            await WaitForMoveCompletionAsync(reachyClient, wakeMove.Uuid, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            throw new InvalidOperationException(
                $"Reachy Mini startup failed while waiting for wake move {wakeMove.Uuid} to complete.",
                ex);
        }

        Console.WriteLine($"Wake move {wakeMove.Uuid} completed.");
    }

    private static async Task WaitForMoveCompletionAsync(
        ReachyMiniClient reachyClient,
        Guid moveUuid,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < WakeMoveTimeout)
        {
            var runningMoves = await reachyClient.Move.GetRunningMovesAsync(cancellationToken);
            if (runningMoves.All(move => move.Uuid != moveUuid))
            {
                return;
            }

            await Task.Delay(MovePollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"Reachy Mini startup timed out after {WakeMoveTimeout.TotalSeconds:0} seconds waiting for wake move {moveUuid} to complete.");
    }
}
