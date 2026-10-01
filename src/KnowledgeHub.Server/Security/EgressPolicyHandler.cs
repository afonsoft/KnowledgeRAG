using System.Net;
using System.Net.Sockets;

namespace KnowledgeHub.Server.Security;

/// <summary>
/// SPEC-20260929-connector-security-sync-safety RF-005: egress policy for
/// connector HTTP clients.
/// Blocks RFC1918/loopback targets by default (opt-in:
/// <c>Security:Egress:AllowPrivateNetworks=true</c> globally, or per-request
/// via <see cref="AllowPrivateHostsKey"/> for connectors exposing their own
/// <c>allowPrivateHosts</c> source setting). Link-local/cloud-metadata
/// (169.254.0.0/16, fe80::/10), CGNAT, multicast and 0.0.0.0/8 stay blocked
/// under every configuration.
/// Redirects are followed manually: a hop to another host — or an HTTPS→HTTP
/// downgrade — strips every request header outside a small safe allowlist, so
/// Authorization, PRIVATE-TOKEN and user-configured secret headers can never
/// leak to a different origin. Non-http(s) redirect targets are refused.
/// Residual risk: DNS answers are validated once per request while the socket
/// resolves again at connect time (DNS-rebinding TOCTOU); closing that gap
/// needs a ConnectCallback on SocketsHttpHandler, noted as follow-up.
/// </summary>
public sealed class EgressPolicyHandler(bool allowPrivateNetworks = false) : DelegatingHandler
{
    /// <summary>Reads the flag from configuration at construction time.</summary>
    public static EgressPolicyHandler FromConfiguration(IConfiguration configuration) =>
        new(configuration.GetValue("Security:Egress:AllowPrivateNetworks", false));

    /// <summary>Per-request opt-in for private-network targets. Connectors
    /// with a per-source <c>allowPrivateHosts</c> setting set this option on
    /// their requests; the global flag still applies when unset.</summary>
    public static readonly HttpRequestOptionsKey<bool> AllowPrivateHostsKey =
        new("KnowledgeHub.Egress.AllowPrivateHosts");

    /// <summary>Headers safe to forward across origins on a redirect. Every
    /// other request header (Authorization, PRIVATE-TOKEN, X-Api-Key, custom
    /// secret headers) is dropped when the redirect leaves the origin or
    /// downgrades to plain HTTP.</summary>
    private static readonly HashSet<string> RedirectSafeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Charset", "Accept-Encoding", "Accept-Language",
        "Cache-Control", "If-Match", "If-Modified-Since", "If-None-Match",
        "If-Range", "If-Unmodified-Since", "Range", "User-Agent"
    };

    private const int MaxRedirects = 5;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new HttpRequestException(
                $"egress blocked: unsupported URI scheme '{uri?.Scheme ?? "null"}'");

        var allowPrivate = allowPrivateNetworks
            || (request.Options.TryGetValue(AllowPrivateHostsKey, out var opt) && opt);
        if (await IsBlockedHostAsync(uri.Host, allowPrivate, cancellationToken))
            throw new HttpRequestException(
                $"egress blocked: '{uri.Host}' resolves to a restricted address");

        // Origin is per-request — the handler instance is shared for the whole
        // named-client lifetime, so a handler-level origin field would bleed
        // one caller's trust domain into another's.
        var response = await base.SendAsync(request, cancellationToken);
        return await FollowRedirectsAsync(request, response, uri, allowPrivate, cancellationToken);
    }

    // Manual redirect handling — auto-redirect resends Authorization to the
    // new host; we strip credentials whenever the redirect leaves origin.
    private async Task<HttpResponseMessage> FollowRedirectsAsync(
        HttpRequestMessage request, HttpResponseMessage response, Uri uri,
        bool allowPrivate, CancellationToken cancellationToken)
    {
        // Origin is per-request — the handler instance is shared for the whole
        // named-client lifetime, so a handler-level origin field would bleed
        // one caller's trust domain into another's.
        var originHost = uri.Host;
        var originScheme = uri.Scheme;
        for (var hop = 0;
             IsRedirect(response) && response.Headers.Location is { } next
             && hop < MaxRedirects;
             hop++)
        {
            var nextUri = next.IsAbsoluteUri ? next : new Uri(uri, next);
            // Only http(s) hops are followed — a Location pointing at
            // file:///etc/passwd or gopher:// must not reach the inner handler.
            if (nextUri.Scheme is not ("http" or "https"))
                return response; // surface the redirect; caller treats as error
            if (await IsBlockedHostAsync(nextUri.Host, allowPrivate, cancellationToken))
                return response; // surface the redirect; caller treats as error
            // A sent request's content stream may be consumed — only bodyless
            // requests (the connector norm: GET/HEAD) can be replayed safely.
            if (request.Content is not null)
                return response;
            request.RequestUri = nextUri;
            StripCredentialsOffOrigin(request, originHost, originScheme);
            response.Dispose();
            response = await base.SendAsync(request, cancellationToken);
            uri = nextUri;
        }
        return response;
    }

    private static bool IsRedirect(HttpResponseMessage r) =>
        (int)r.StatusCode is >= 300 and <= 399
        && r.StatusCode != HttpStatusCode.NotModified;

    /// <summary>When a redirect leaves the origin host — or drops from HTTPS
    /// to HTTP — strip every header outside the safe allowlist. Covers
    /// Authorization, PRIVATE-TOKEN, X-Api-Key and arbitrary user-configured
    /// secret headers without needing to enumerate credential names.</summary>
    public static void StripCredentialsOffOrigin(
        HttpRequestMessage request, string originHost, string originScheme)
    {
        var target = request.RequestUri;
        var leavesOrigin = target?.Host is { } host
            && !string.Equals(host, originHost, StringComparison.OrdinalIgnoreCase);
        var downgrades = originScheme == "https" && target?.Scheme == "http";
        if (!leavesOrigin && !downgrades)
            return;

        var drop = request.Headers
            .Where(h => !RedirectSafeHeaders.Contains(h.Key))
            .Select(h => h.Key)
            .ToList();
        foreach (var name in drop)
            request.Headers.Remove(name);
        request.Headers.Authorization = null;
    }

    /// <summary>SPEC-20261001-a2a-task-durability RF-003: validates a URL
    /// against the same egress rules without sending a request — used to
    /// screen A2A push webhook URLs at registration time.</summary>
    public static async Task<bool> IsBlockedAsync(
        Uri uri, bool allowPrivateNetworks, CancellationToken cancellationToken = default)
    {
        if (uri.Scheme is not ("http" or "https"))
            return true;
        return await IsBlockedHostAsync(uri.Host, allowPrivateNetworks, cancellationToken);
    }

    private static async Task<bool> IsBlockedHostAsync(
        string host, bool allowPrivate, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return IsBlockedAddress(literal, allowPrivate);

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            return false; // DNS failure surfaces downstream as a normal HTTP error
        }
        return addresses.Any(a => IsBlockedAddress(a, allowPrivate));
    }

    private static bool IsBlockedAddress(IPAddress address, bool allowPrivate) =>
        address.AddressFamily == AddressFamily.InterNetwork
            ? IsBlockedIPv4(address, allowPrivate)
            : IsBlockedIPv6(address, allowPrivate);

    /// <summary>IPv4 blocklist: link-local (incl. cloud metadata), 0.0.0.0/8,
    /// CGNAT, multicast/reserved; loopback + RFC1918 only when private is denied.</summary>
    private static bool IsBlockedIPv4(IPAddress address, bool allowPrivate)
    {
        var b = address.GetAddressBytes();
        // 169.254.0.0/16 — link-local incl. cloud metadata (169.254.169.254).
        if (b[0] == 169 && b[1] == 254)
            return true;
        // 0.0.0.0/8, 100.64.0.0/10 CGNAT, multicast/reserved 224.0.0.0+.
        if (b[0] == 0 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224)
            return true;
        if (!allowPrivate &&
            (IPAddress.IsLoopback(address)
             || b[0] == 10
             || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
             || (b[0] == 192 && b[1] == 168)))
            return true;
        return false;
    }

    /// <summary>IPv6 blocklist: link-local fe80::/10 always (metadata
    /// equivalents); loopback ::1 and unique-local fc00::/7 only when private
    /// nets are denied.</summary>
    private static bool IsBlockedIPv6(IPAddress address, bool allowPrivate)
    {
        var v6 = address.GetAddressBytes();
        if (v6[0] == 0xfe && (v6[1] & 0xc0) == 0x80)
            return true;
        if (!allowPrivate
            && (IPAddress.IsLoopback(address) || (v6[0] & 0xfe) == 0xfc))
            return true;
        return false;
    }
}
