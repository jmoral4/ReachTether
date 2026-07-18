using Microsoft.Extensions.Logging;

internal sealed class RealtimeVoiceSessionManager(
    IRealtimeVoiceSessionFactory sessionFactory,
    ILogger logger) : IAsyncDisposable
{
    private IRealtimeVoiceSession? session;
    private IAsyncEnumerator<RealtimeServerEvent>? updates;

    public IRealtimeVoiceSession Session => session
        ?? throw new InvalidOperationException("The realtime session is not connected.");

    public IAsyncEnumerator<RealtimeServerEvent> Updates => updates
        ?? throw new InvalidOperationException("The realtime update stream is not connected.");

    public async Task EnsureConnectedAsync(
        RealtimeSessionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (session is not null && updates is not null)
        {
            return;
        }

        var newSession = await sessionFactory.ConnectAsync(cancellationToken);
        try
        {
            await newSession.ConfigureAsync(configuration, cancellationToken);
            var newUpdates = newSession
                .ReceiveEventsAsync(cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            session = newSession;
            updates = newUpdates;
        }
        catch
        {
            await newSession.DisposeAsync();
            throw;
        }
    }

    public async Task<bool> RecoverIfNeededAsync(
        string failureReason,
        RealtimeSessionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!RequiresReset(failureReason))
        {
            return false;
        }

        logger.LogWarning("Resetting realtime session after turn failure: {Reason}", failureReason);
        await ResetAsync();
        await EnsureConnectedAsync(configuration, cancellationToken);
        return true;
    }

    public async Task ResetAsync()
    {
        var updatesToDispose = updates;
        var sessionToDispose = session;
        updates = null;
        session = null;

        if (updatesToDispose is not null)
        {
            try
            {
                await updatesToDispose.DisposeAsync();
            }
            catch (NotSupportedException)
            {
                // Some SDK async enumerators do not support DisposeAsync().
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose realtime update stream cleanly.");
            }
        }

        if (sessionToDispose is not null)
        {
            try
            {
                await sessionToDispose.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose realtime session cleanly.");
            }
        }
    }

    internal static bool RequiresReset(string failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason))
        {
            return false;
        }

        return failureReason.StartsWith("Realtime API error:", StringComparison.OrdinalIgnoreCase)
            || failureReason.StartsWith("Realtime update stream failed:", StringComparison.OrdinalIgnoreCase)
            || failureReason.StartsWith("Realtime input streaming failed:", StringComparison.OrdinalIgnoreCase)
            || failureReason.Contains("stream closed unexpectedly", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync() => await ResetAsync();
}
