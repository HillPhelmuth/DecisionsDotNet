using System.Net;

namespace DecisionsDotNet;

/// <summary>A Decisions provider answered with an HTTP error that was not retried, or that outlasted its retries.</summary>
public sealed class DecisionsApiException : HttpRequestException
{
    /// <summary>Creates the exception from the provider's status and response body.</summary>
    public DecisionsApiException(HttpStatusCode statusCode, string responseBody, string service = "TypeSafe")
        : base($"{service} API returned HTTP {(int)statusCode} ({statusCode}).", null, statusCode)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        Service = service;
    }

    /// <summary>The HTTP status the provider returned.</summary>
    public new HttpStatusCode StatusCode { get; }

    /// <summary>The raw response body, which usually carries the provider's error detail.</summary>
    public string ResponseBody { get; }

    /// <summary>The provider that returned the error: TypeSafe, OpenRouter, or OpenAI.</summary>
    public string Service { get; }
}
