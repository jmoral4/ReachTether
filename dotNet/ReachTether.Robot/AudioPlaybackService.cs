using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachTether.Audio.Alsa;

internal interface IAudioPlaybackPipeline
{
    Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken = default);
    void Flush();
}

internal sealed class AudioPlaybackService(
    LocalAudioSession audioSession,
    ILogger<AudioPlaybackService> logger) : BackgroundService, IAudioPlaybackPipeline
{
    private readonly Channel<PlaybackItem> _playbackQueue = Channel.CreateBounded<PlaybackItem>(new BoundedChannelOptions(8)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = false,
        SingleReader = true
    });

    private CancellationTokenSource? _currentPlaybackCts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _playbackQueue.Reader.ReadAllAsync(stoppingToken))
        {
            using var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, item.CancellationToken);
            _currentPlaybackCts = playbackCts;

            try
            {
                await audioSession.PlayWaveAsync(item.WavBytes, playbackCts.Token);
                item.Completion.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                item.Completion.TrySetCanceled(playbackCts.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Playback failure.");
                item.Completion.TrySetException(ex);
            }
            finally
            {
                _currentPlaybackCts = null;
            }
        }
    }

    public async Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new PlaybackItem(wavBytes, completion, cancellationToken);

        await _playbackQueue.Writer.WriteAsync(item, cancellationToken);
        await completion.Task;
    }

    public void Flush()
    {
        while (_playbackQueue.Reader.TryRead(out var dropped))
        {
            dropped.Completion.TrySetCanceled();
        }

        _currentPlaybackCts?.Cancel();
        audioSession.FlushPlayback();
    }

    private sealed record PlaybackItem(
        byte[] WavBytes,
        TaskCompletionSource<bool> Completion,
        CancellationToken CancellationToken);
}
