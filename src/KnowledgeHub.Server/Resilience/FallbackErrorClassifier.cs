using KnowledgeHub.Server.Chat;

namespace KnowledgeHub.Server.Resilience;

/// <summary>
/// Transient-vs-permanent failure taxonomy (RF-002): 429/502/503/504, socket
/// failures and timeouts trigger fallback; 400/401/403/404 are permanent
/// client/auth faults that must surface immediately.
/// </summary>
public static class FallbackErrorClassifier
{
    /// <summary>Stable reason tag surfaced in fallback metadata/logs.</summary>
    public static string ReasonFor(Exception ex) => ex switch
    {
        ChatProviderException { StatusCode: { } s } => $"HttpError_{s}",
        ChatProviderException { IsTimeout: true } => "Timeout",
        HttpRequestException { StatusCode: { } s } => $"HttpError_{(int)s}",
        HttpRequestException => "NetworkError",
        TimeoutException => "Timeout",
        _ => ex.GetType().Name
    };

    /// <summary>Eligible for fallback (transient upstream fault).</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        ChatProviderException { StatusCode: { } s } => s is 429 or 502 or 503 or 504,
        ChatProviderException { IsTimeout: true } => true,
        ChatProviderException => false, // malformed payload/empty response — config-ish
        HttpRequestException { StatusCode: { } s } => (int)s is 429 or >= 500,
        HttpRequestException => true,    // no response at all (DNS/socket)
        TimeoutException => true,
        _ => false
    };

    /// <summary>Permanent client/auth fault — never triggers provider fallback (RF-002).</summary>
    public static bool IsPermanent(Exception ex) => ex switch
    {
        ChatProviderException { StatusCode: 400 or 401 or 403 or 404 } => true,
        HttpRequestException { StatusCode: { } s }
            => (int)s is 400 or 401 or 403 or 404,
        _ => false
    };
}
