using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachTether.Audio;
using ReachTether.Audio.Alsa;

internal interface IAudioCapturePipeline
{
    Task<AudioFrame[]> CaptureWindowAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}

internal sealed class AudioCaptureService(
    LocalAudioSession audioSession,
    ILogger<AudioCaptureService> logger) : BackgroundService, IAudioCapturePipeline
{
    private readonly Channel<AudioFrame> _channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = true,
        SingleReader = false
    });

    private readonly SemaphoreSlim _captureLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var frame = await audioSession.CaptureChunkAsync(stoppingToken);
                await _channel.Writer.WriteAsync(frame, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(100, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Audio capture loop fault; retrying.");

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(250, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _channel.Writer.TryComplete();
    }

    public async Task<AudioFrame[]> CaptureWindowAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            return [];
        }

        await _captureLock.WaitAsync(cancellationToken);
        try
        {
            while (_channel.Reader.TryRead(out _))
            {
            }

            var endAt = DateTime.UtcNow + duration;
            var frames = new List<AudioFrame>();

            while (DateTime.UtcNow < endAt && !cancellationToken.IsCancellationRequested)
            {
                var remaining = endAt - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(remaining);

                try
                {
                    var frame = await _channel.Reader.ReadAsync(readCts.Token);
                    frames.Add(frame);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return [.. frames];
        }
        finally
        {
            _captureLock.Release();
        }
    }
}
