namespace Sofia.Client;

/// <summary>An API call that the backend answered with a non-success status.</summary>
public sealed class SofiaApiException(int status, string message) : Exception(message)
{
    /// <summary>HTTP status code, or 400 for a request the in-process transport rejected.</summary>
    public int Status { get; } = status;

    public bool IsNotFound => Status == 404;
    public bool IsConflict => Status == 409;
    public bool IsUnauthorized => Status == 401;
}
