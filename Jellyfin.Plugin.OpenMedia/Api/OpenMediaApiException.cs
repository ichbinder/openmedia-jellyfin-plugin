using System;
using System.Net;

namespace Jellyfin.Plugin.OpenMedia.Api;

/// <summary>
/// Wird geworfen wenn die openmedia-API einen nicht-erfolgreichen Status zurückgibt oder die Antwort nicht parsbar ist.
/// </summary>
public class OpenMediaApiException : Exception
{
    public OpenMediaApiException(string message)
        : base(message)
    {
    }

    public OpenMediaApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OpenMediaApiException(string message, HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP-Statuscode der API-Antwort, falls vorhanden.</summary>
    public HttpStatusCode? StatusCode { get; }
}
