using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Options;
using ReachyMini.Sdk.Configuration;
using ReachyMini.Sdk.Internal;
using ReachyMini.Sdk.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace ReachyMini.Sdk.Clients;

/// <summary>
/// Camera client that captures snapshots from a long-lived local GStreamer pipeline.
/// </summary>
public sealed class CameraClient : IDisposable
{
    private const string AppSinkName = "reachtether_sink";
    private static readonly object GStreamerInitLock = new();
    private static bool s_gstreamerInitialized;

    private readonly ReachyMiniOptions _options;
    private readonly object _sync = new();

    private IntPtr _mainLoop = IntPtr.Zero;
    private Thread? _mainLoopThread;
    private IntPtr _pipeline = IntPtr.Zero;
    private IntPtr _appSink = IntPtr.Zero;
    private string? _pipelineKey;
    private string? _pipelineDescription;
    private string? _backend;
    private string? _attemptDescription;
    private bool _disposed;

    public CameraClient(IOptions<ReachyMiniOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Captures a single JPEG snapshot from the robot camera.
    /// </summary>
    public Task<CameraSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => CaptureSnapshotCore(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Opens and starts the camera pipeline ahead of the first snapshot.
    /// </summary>
    public Task<CameraWarmupResult> WarmupAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => WarmupCore(cancellationToken), cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            DestroyPipelineNoLock();
            StopMainLoopNoLock();
            _disposed = true;
        }
    }

    private CameraSnapshot CaptureSnapshotCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sourceKind = NormalizeSourceKind(_options.CameraSourceKind);
        var sourcePath = _options.CameraSourcePath;
        var width = _options.CameraWidth;
        var height = _options.CameraHeight;
        var framerate = _options.CameraFramerate;
        ValidateCaptureSource(sourceKind, sourcePath);

        var startedAt = DateTimeOffset.UtcNow;
        var failures = new List<string>();
        var pipelineAttempts = BuildPipelineDefinitions(sourceKind, sourcePath, width, height, framerate);

        foreach (var attempt in pipelineAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptStopwatch = Stopwatch.StartNew();
            var result = TryCaptureSnapshotWithDefinition(
                attempt,
                startedAt,
                width,
                height,
                attemptStopwatch,
                cancellationToken);
            if (result.Snapshot is not null)
            {
                return result.Snapshot;
            }

            failures.Add($"{attempt.Description}: {result.FailureReason}");
        }

        throw new InvalidOperationException(
            $"Camera capture failed after {pipelineAttempts.Count} in-process attempt(s): {string.Join(" | ", failures)}");
    }

    private CameraWarmupResult WarmupCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sourceKind = NormalizeSourceKind(_options.CameraSourceKind);
        var sourcePath = _options.CameraSourcePath;
        var width = _options.CameraWidth;
        var height = _options.CameraHeight;
        var framerate = _options.CameraFramerate;
        ValidateCaptureSource(sourceKind, sourcePath);

        var failures = new List<string>();
        var pipelineAttempts = BuildPipelineDefinitions(sourceKind, sourcePath, width, height, framerate);

        foreach (var attempt in pipelineAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string pipelineDescription;
                lock (_sync)
                {
                    EnsurePipelineNoLock(attempt, width, height);
                    pipelineDescription = _pipelineDescription ?? string.Empty;
                }

                return new CameraWarmupResult(
                    attempt.Backend,
                    pipelineDescription,
                    DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                failures.Add($"{attempt.Description}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Camera warmup failed after {pipelineAttempts.Count} in-process attempt(s): {string.Join(" | ", failures)}");
    }

    private CaptureAttemptResult TryCaptureSnapshotWithDefinition(
        PipelineDefinition attempt,
        DateTimeOffset startedAt,
        int width,
        int height,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;

        for (var restartIndex = 0; restartIndex < 2; restartIndex++)
        {
            IntPtr appSink;
            IntPtr pipeline;
            string backend;
            string pipelineDescription;

            try
            {
                lock (_sync)
                {
                    EnsurePipelineNoLock(attempt, width, height);
                    appSink = _appSink;
                    pipeline = _pipeline;
                    backend = _backend!;
                    pipelineDescription = _pipelineDescription!;
                }
            }
            catch (Exception ex)
            {
                return CaptureAttemptResult.Fail(ex.Message);
            }

            var sample = TryPullFreshSample(
                appSink,
                TimeSpan.FromSeconds(_options.CameraCaptureTimeoutSeconds),
                cancellationToken);
            if (sample != IntPtr.Zero)
            {
                try
                {
                    var rawBytes = CopySampleBytes(sample);
                    var encodeStopwatch = Stopwatch.StartNew();
                    var imageBytes = EncodeJpeg(rawBytes, width, height);
                    encodeStopwatch.Stop();
                    stopwatch.Stop();

                    return CaptureAttemptResult.Success(
                        new CameraSnapshot(
                            imageBytes,
                            "image/jpeg",
                            startedAt,
                            new CameraCaptureStats(
                                Backend: backend,
                                Width: width,
                                Height: height,
                                Channels: 3,
                                RawBytes: checked(width * height * 3),
                                EncodedBytes: imageBytes.Length,
                                CaptureDurationMs: stopwatch.Elapsed.TotalMilliseconds,
                                EncodeDurationMs: encodeStopwatch.Elapsed.TotalMilliseconds,
                                TotalDurationMs: stopwatch.Elapsed.TotalMilliseconds)));
                }
                finally
                {
                    GStreamerInterop.gst_sample_unref(sample);
                }
            }

            var busError = TryReadPipelineError(pipeline);
            lastFailure = new InvalidOperationException(
                string.IsNullOrWhiteSpace(busError)
                    ? $"Timed out waiting for a fresh camera frame from pipeline `{pipelineDescription}`."
                    : $"Camera pipeline `{pipelineDescription}` reported an error: {busError}");

            lock (_sync)
            {
                DestroyPipelineNoLock();
            }
        }

        return CaptureAttemptResult.Fail(lastFailure?.Message ?? $"Attempt '{attempt.Description}' failed.");
    }

    private void EnsurePipelineNoLock(PipelineDefinition definition, int width, int height)
    {
        ThrowIfDisposedNoLock();
        EnsureGStreamerInitialized();
        EnsureMainLoopNoLock();

        var requestedKey = $"{definition.Backend}|{width}|{height}|{definition.FrameRate}";
        if (_pipeline != IntPtr.Zero && string.Equals(_pipelineKey, requestedKey, StringComparison.Ordinal))
        {
            return;
        }

        DestroyPipelineNoLock();

        var pipelineDescription = BuildPipelineDescription(definition.SourceSegment, width, height, definition.FrameRate);
        var pipeline = CreatePipeline(pipelineDescription);
        var appSink = GStreamerInterop.gst_bin_get_by_name(pipeline, AppSinkName);
        if (appSink == IntPtr.Zero)
        {
            GStreamerInterop.gst_object_unref(pipeline);
            throw new InvalidOperationException(
                $"GStreamer pipeline did not expose appsink '{AppSinkName}': {pipelineDescription}");
        }

        try
        {
            StartPipeline(pipeline, pipelineDescription);
        }
        catch
        {
            GStreamerInterop.gst_object_unref(appSink);
            GStreamerInterop.gst_object_unref(pipeline);
            throw;
        }

        _pipeline = pipeline;
        _appSink = appSink;
        _pipelineKey = requestedKey;
        _pipelineDescription = pipelineDescription;
        _backend = definition.Backend;
        _attemptDescription = definition.Description;
    }

    private static void EnsureGStreamerInitialized()
    {
        if (s_gstreamerInitialized)
        {
            return;
        }

        lock (GStreamerInitLock)
        {
            if (s_gstreamerInitialized)
            {
                return;
            }

            IntPtr error;
            if (GStreamerInterop.gst_init_check(IntPtr.Zero, IntPtr.Zero, out error) == 0)
            {
                var message = GStreamerInterop.TakeErrorMessage(error);
                throw new InvalidOperationException(
                    $"Failed to initialize GStreamer: {message}");
            }

            s_gstreamerInitialized = true;
        }
    }

    private static string CopyErrorMessage(IntPtr error)
    {
        return GStreamerInterop.TakeErrorMessage(error);
    }

    private static IntPtr TryPullFreshSample(
        IntPtr appSink,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        const ulong pollIntervalNs = 200_000_000;
        var timeoutNs = ToNanoseconds(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : timeout);
        var deadline = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var elapsedNs = ToNanoseconds(deadline.Elapsed);
            if (elapsedNs >= timeoutNs)
            {
                return IntPtr.Zero;
            }

            var remainingNs = timeoutNs - elapsedNs;
            var waitNs = Math.Min(pollIntervalNs, remainingNs);
            var sample = GStreamerInterop.gst_app_sink_try_pull_sample(appSink, waitNs);
            if (sample != IntPtr.Zero)
            {
                return sample;
            }
        }
    }

    private static byte[] CopySampleBytes(IntPtr sample)
    {
        var buffer = GStreamerInterop.gst_sample_get_buffer(sample);
        if (buffer == IntPtr.Zero)
        {
            throw new InvalidOperationException("GStreamer sample did not contain a buffer.");
        }

        GStreamerInterop.GstMapInfo mapInfo = default;
        if (GStreamerInterop.gst_buffer_map(buffer, out mapInfo, GStreamerInterop.GstMapFlags.Read) == 0)
        {
            throw new InvalidOperationException("Failed to map GStreamer sample buffer.");
        }

        try
        {
            if (mapInfo.Data == IntPtr.Zero || mapInfo.Size == 0)
            {
                throw new InvalidOperationException("GStreamer sample buffer was empty.");
            }

            var length = checked((int)mapInfo.Size);
            var bytes = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy(mapInfo.Data, bytes, 0, length);
            return bytes;
        }
        finally
        {
            GStreamerInterop.gst_buffer_unmap(buffer, ref mapInfo);
        }
    }

    private static byte[] EncodeJpeg(byte[] rawBytes, int width, int height)
    {
        using var image = Image.WrapMemory<Bgr24>(rawBytes, width, height);
        using var output = new MemoryStream();
        image.Save(output, new JpegEncoder
        {
            Quality = 90
        });

        return output.ToArray();
    }

    private static IntPtr CreatePipeline(string pipelineDescription)
    {
        IntPtr error;
        var pipeline = GStreamerInterop.gst_parse_launch(pipelineDescription, out error);
        if (pipeline != IntPtr.Zero)
        {
            return pipeline;
        }

        var message = CopyErrorMessage(error);
        throw new InvalidOperationException(
            $"Failed to create GStreamer camera pipeline `{pipelineDescription}`: {message}");
    }

    private static void StartPipeline(IntPtr pipeline, string pipelineDescription)
    {
        var changeResult = GStreamerInterop.gst_element_set_state(
            pipeline,
            GStreamerInterop.GstState.Playing);

        if (changeResult == GStreamerInterop.GstStateChangeReturn.Failure)
        {
            var busError = TryReadPipelineError(pipeline);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(busError)
                    ? $"Failed to start GStreamer pipeline `{pipelineDescription}`."
                    : $"Failed to start GStreamer pipeline `{pipelineDescription}`: {busError}");
        }

        GStreamerInterop.GstState current;
        GStreamerInterop.GstState pending;
        var stateResult = GStreamerInterop.gst_element_get_state(
            pipeline,
            out current,
            out pending,
            ToNanoseconds(TimeSpan.FromSeconds(2)));

        if (stateResult == GStreamerInterop.GstStateChangeReturn.Failure)
        {
            var busError = TryReadPipelineError(pipeline);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(busError)
                    ? $"GStreamer pipeline `{pipelineDescription}` failed while transitioning to PLAYING."
                    : $"GStreamer pipeline `{pipelineDescription}` failed while transitioning to PLAYING: {busError}");
        }
    }

    private void DestroyPipelineNoLock()
    {
        if (_pipeline != IntPtr.Zero)
        {
            GStreamerInterop.gst_element_set_state(_pipeline, GStreamerInterop.GstState.Null);
            GStreamerInterop.gst_element_get_state(
                _pipeline,
                out _,
                out _,
                ToNanoseconds(TimeSpan.FromSeconds(2)));
        }

        if (_appSink != IntPtr.Zero)
        {
            GStreamerInterop.gst_object_unref(_appSink);
            _appSink = IntPtr.Zero;
        }

        if (_pipeline != IntPtr.Zero)
        {
            GStreamerInterop.gst_object_unref(_pipeline);
            _pipeline = IntPtr.Zero;
        }

        _pipelineKey = null;
        _pipelineDescription = null;
        _backend = null;
        _attemptDescription = null;
    }

    private void EnsureMainLoopNoLock()
    {
        if (_mainLoop != IntPtr.Zero)
        {
            return;
        }

        _mainLoop = GStreamerInterop.g_main_loop_new(IntPtr.Zero, false);
        if (_mainLoop == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create GLib main loop for camera pipeline.");
        }

        _mainLoopThread = new Thread(static state =>
        {
            GStreamerInterop.g_main_loop_run((IntPtr)state!);
        })
        {
            IsBackground = true,
            Name = "ReachyMini.Camera.GLibMainLoop"
        };
        _mainLoopThread.Start(_mainLoop);
    }

    private void StopMainLoopNoLock()
    {
        if (_mainLoop == IntPtr.Zero)
        {
            return;
        }

        GStreamerInterop.g_main_loop_quit(_mainLoop);
        if (_mainLoopThread is not null && _mainLoopThread.IsAlive)
        {
            _mainLoopThread.Join(TimeSpan.FromSeconds(2));
        }

        GStreamerInterop.g_main_loop_unref(_mainLoop);
        _mainLoop = IntPtr.Zero;
        _mainLoopThread = null;
    }

    private static string? TryReadPipelineError(IntPtr pipeline)
    {
        if (pipeline == IntPtr.Zero)
        {
            return null;
        }

        var bus = GStreamerInterop.gst_element_get_bus(pipeline);
        if (bus == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var message = GStreamerInterop.gst_bus_timed_pop_filtered(
                bus,
                0,
                GStreamerInterop.GstMessageType.Error);
            if (message == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                IntPtr error;
                IntPtr debug;
                GStreamerInterop.gst_message_parse_error(message, out error, out debug);

                var errorMessage = GStreamerInterop.TakeErrorMessage(error);
                var debugMessage = GStreamerInterop.TakeUtf8StringAndFree(debug);
                if (string.IsNullOrWhiteSpace(debugMessage))
                {
                    return errorMessage;
                }

                return $"{errorMessage} (debug: {debugMessage})";
            }
            finally
            {
                GStreamerInterop.gst_message_unref(message);
            }
        }
        finally
        {
            GStreamerInterop.gst_object_unref(bus);
        }
    }

    private static ulong ToNanoseconds(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        var ticks = duration.Ticks;
        return checked((ulong)ticks * 100);
    }

    private static List<PipelineDefinition> BuildPipelineDefinitions(string sourceKind, string sourcePath, int width, int height, int framerate)
    {
        return sourceKind switch
        {
            "unix-socket" =>
            [
                new PipelineDefinition(
                    Backend: "unix-fd/v4l2-raw/appsink",
                    Description: "unix-fd socket carrying raw v4l2 frames",
                    SourceSegment: $"unixfdsrc socket-path={QuoteValue(sourcePath)} ! queue leaky=downstream max-size-buffers=1 ! v4l2convert",
                    FrameRate: framerate)
            ],
            "unix-fd-raw" =>
            [
                new PipelineDefinition(
                    Backend: "unix-fd/v4l2-raw/appsink",
                    Description: "unix-fd socket carrying raw v4l2 frames",
                    SourceSegment: $"unixfdsrc socket-path={QuoteValue(sourcePath)} ! queue leaky=downstream max-size-buffers=1 ! v4l2convert",
                    FrameRate: framerate)
            ],
            "shm-socket" =>
            [
                new PipelineDefinition(
                    Backend: "shm-socket/jpeg/appsink",
                    Description: "shared-memory socket carrying JPEG frames",
                    SourceSegment: $"shmsrc socket-path={QuoteValue(sourcePath)} is-live=true do-timestamp=true ! queue leaky=downstream max-size-buffers=1 ! image/jpeg,width={width},height={height},framerate={framerate}/1 ! jpegparse ! jpegdec ! videoconvert",
                    FrameRate: framerate),
                new PipelineDefinition(
                    Backend: "shm-socket/raw/appsink",
                    Description: "shared-memory socket carrying raw frames",
                    SourceSegment: $"shmsrc socket-path={QuoteValue(sourcePath)} is-live=true do-timestamp=true ! queue leaky=downstream max-size-buffers=1",
                    FrameRate: framerate)
            ],
            "unix-fd-jpeg" =>
            [
                new PipelineDefinition(
                    Backend: "unix-fd/jpeg/appsink",
                    Description: "unix-fd socket carrying JPEG frames",
                    SourceSegment: $"unixfdsrc socket-path={QuoteValue(sourcePath)} ! queue leaky=downstream max-size-buffers=1 ! jpegparse ! jpegdec ! videoconvert",
                    FrameRate: framerate)
            ],
            "v4l2" =>
            [
                new PipelineDefinition(
                    Backend: "v4l2/jpeg/appsink",
                    Description: "v4l2 device carrying JPEG frames",
                    SourceSegment: $"v4l2src device={QuoteValue(sourcePath)} ! queue leaky=downstream max-size-buffers=1 ! jpegdec ! videoconvert",
                    FrameRate: framerate)
            ],
            _ => throw new NotSupportedException(
                $"Unsupported camera source kind '{sourceKind}'. Supported values: unix-socket, shm-socket, unix-fd-jpeg, unix-fd-raw, v4l2.")
        };
    }

    private static string BuildPipelineDescription(string sourceSegment, int width, int height, int framerate)
    {
        return string.Join(
            " ! ",
            sourceSegment,
            $"video/x-raw,format=BGR,width={width},height={height},framerate={framerate}/1",
            $"appsink name={AppSinkName} emit-signals=false sync=false max-buffers=1 drop=true wait-on-eos=false");
    }

    private static string QuoteValue(string value)
    {
        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string NormalizeSourceKind(string sourceKind)
    {
        return sourceKind.Trim().ToLowerInvariant() switch
        {
            "unix-socket" or "unix_socket" or "socket" => "unix-socket",
            "shm-socket" or "shm_socket" or "shm" => "shm-socket",
            "unix-fd-jpeg" or "unixfd-jpeg" => "unix-fd-jpeg",
            "unix-fd-raw" or "unixfd-raw" => "unix-fd-raw",
            "v4l2" or "device" => "v4l2",
            _ => throw new NotSupportedException(
                $"Unsupported camera source kind '{sourceKind}'. Supported values: unix-socket, shm-socket, unix-fd-jpeg, unix-fd-raw, v4l2.")
        };
    }

    private static void ValidateCaptureSource(string sourceKind, string sourcePath)
    {
        if (sourceKind is "unix-socket" or "shm-socket" or "unix-fd-jpeg" or "unix-fd-raw")
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"Camera unix socket '{sourcePath}' was not found. The robot media daemon may not be ready.");
            }

            return;
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Camera device path '{sourcePath}' was not found.");
        }
    }

    private void ThrowIfDisposedNoLock()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record PipelineDefinition(string Backend, string Description, string SourceSegment, int FrameRate);

    private sealed record CaptureAttemptResult(CameraSnapshot? Snapshot, string FailureReason)
    {
        public static CaptureAttemptResult Success(CameraSnapshot snapshot) => new(snapshot, string.Empty);

        public static CaptureAttemptResult Fail(string reason) => new(null, reason);
    }
}
