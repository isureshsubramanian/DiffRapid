using System.IO;
using System.Net;

namespace DiffRapid.Ai;

/// <summary>A request failure with a safe, actionable message and no server response body.</summary>
public sealed class AiException : IOException
{
    /// <summary>Creates an endpoint failure without including credentials or source content.</summary>
    public AiException(string message, HttpStatusCode? statusCode = null) : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status, when a response was received.</summary>
    public HttpStatusCode? StatusCode { get; }
}
