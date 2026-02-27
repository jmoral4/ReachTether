namespace ReachyMini.Sdk.Exceptions;

/// <summary>
/// Base exception for Reachy Mini SDK errors.
/// </summary>
public class ReachyMiniException : Exception
{
    public ReachyMiniException(string message) : base(message)
    {
    }

    public ReachyMiniException(string message, Exception innerException) 
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when the API request fails.
/// </summary>
public class ReachyMiniApiException : ReachyMiniException
{
    public int? StatusCode { get; }
    public string? ResponseContent { get; }

    public ReachyMiniApiException(string message, int? statusCode = null, string? responseContent = null) 
        : base(message)
    {
        StatusCode = statusCode;
        ResponseContent = responseContent;
    }

    public ReachyMiniApiException(string message, Exception innerException, int? statusCode = null) 
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Exception thrown when the robot is not available or not responding.
/// </summary>
public class RobotNotAvailableException : ReachyMiniException
{
    public RobotNotAvailableException(string message) : base(message)
    {
    }

    public RobotNotAvailableException(string message, Exception innerException) 
        : base(message, innerException)
    {
    }
}
