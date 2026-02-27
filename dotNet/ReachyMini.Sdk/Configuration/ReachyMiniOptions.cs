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
}
