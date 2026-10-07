using System.Net;

namespace Huaxiazi.Models;

public enum ConnectionTestStatus
{
    Success,
    MissingKey,
    InvalidEndpoint,
    AuthFailed,
    PermissionDenied,
    BillingIssue,
    AccountPrerequisite,
    RequestTooLarge,
    ModelUnavailable,
    RateLimited,
    NetworkError,
    Timeout,
    ProviderError,
    InvalidResponse
}

public sealed record ConnectionTestResult(
    ConnectionTestStatus Status,
    string UserMessage,
    HttpStatusCode? HttpStatusCode,
    long ElapsedMilliseconds,
    string DiagnosticSummary)
{
    public bool CanSaveAsVerified => Status == ConnectionTestStatus.Success;
}
