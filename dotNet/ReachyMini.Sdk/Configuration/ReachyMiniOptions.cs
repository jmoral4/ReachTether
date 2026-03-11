namespace ReachyMini.Sdk.Configuration;

/// <summary>
/// Configuration options for the Reachy Mini SDK.
/// </summary>
public class ReachyMiniOptions
{
    /// <summary>
    /// The base URL of the Reachy Mini API. Default is http://localhost:8080
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Timeout for HTTP requests. Default is 30 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to throw exceptions on API errors. Default is true.
    /// </summary>
    public bool ThrowOnError { get; set; } = true;

    /// <summary>
    /// Number of retry attempts for failed requests. Default is 3.
    /// </summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// Delay between retry attempts. Default is 1 second.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Camera source kind used for capture. Default is unix-socket for the Reachy Mini local unix-fd feed.
    /// </summary>
    public string CameraSourceKind { get; set; } = "unix-socket";

    /// <summary>
    /// Path to the local camera source. For unix-socket this is typically /tmp/reachymini_camera_socket.
    /// </summary>
    public string CameraSourcePath { get; set; } = "/tmp/reachymini_camera_socket";

    /// <summary>
    /// Requested snapshot width. Default is 1280.
    /// </summary>
    public int CameraWidth { get; set; } = 1280;

    /// <summary>
    /// Requested snapshot height. Default is 720.
    /// </summary>
    public int CameraHeight { get; set; } = 720;

    /// <summary>
    /// Requested snapshot framerate. Default is 30.
    /// </summary>
    public int CameraFramerate { get; set; } = 30;

    /// <summary>
    /// Timeout for a single camera capture attempt. Default is 20 seconds.
    /// </summary>
    public int CameraCaptureTimeoutSeconds { get; set; } = 20;
}
