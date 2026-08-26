using System;
using System.Net;

namespace PromptFloat.Models;

public enum GenerationFailureKind
{
    RequestRejected,
    Authentication,
    ResourceMissing,
    RateLimited,
    Timeout,
    ProviderUnavailable,
    Network,
    InvalidResponse
}

public sealed class GenerationFailureException : InvalidOperationException
{
    public GenerationFailureException(
        GenerationFailureKind kind,
        string message,
        HttpStatusCode? httpStatusCode = null,
        bool isTransient = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        HttpStatusCode = httpStatusCode;
        IsTransient = isTransient;
    }

    public GenerationFailureKind Kind { get; }
    public HttpStatusCode? HttpStatusCode { get; }
    public bool IsTransient { get; }
}
