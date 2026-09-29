using System.Net;
using System.Net.Sockets;

namespace KnowledgeHub.Server.Security;

/// <summary>
/// SPEC-20260929-connector-security-sync-safety RF-005: egress policy for
/// connector HTTP clients. Always strips credentials on cross-origin redirects
/// (PAT/apiKey must never leave the configured host) and always blocks
/// link-local/cloud-metadata targets. RFC1918/loopback allowed by default —
/// self-hosted connectors (Ollama, whisper, internal GitLab, on-prem
/// Unstructured) legitimately target private networks; set
/// <c>Security:Egress:AllowPrivateNetworks=false</c> to harden.
/// </summary>
public sealed class EgressPolicyHandler(bool allowPrivateNetworks = true) : DelegatingHandler
{
    /// <summary>Reads the flag from configuration at construction time.</summary>
    public static EgressPolicyHandler FromConfiguration(IConfiguration configuration) =>
        new(configuration.GetValue("Security:Egress:AllowPrivateNetworks", true));

    private const int MaxRedirects = 5;
    private string? _originHost;

    /// <summary>Register the credential-bearing origin host once — redirects
    /// off this host lose Authorization/X-Api-Key (PAT/header leak guard).</summary>
    public EgressPolicyHandler ForOrigin(Uri origin)
    {
        _originHost = origin.Host;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new HttpRequestException(
                $"egress blocked: unsupported URI scheme '{uri?.Scheme ?? "null"}'");

        if (await IsBlockedHostAsync(uri.Host, cancellationToken))
            throw new HttpRequestException(
                $"egress blocked: '{uri.Host}' resolves to a restricted address");

        _originHost ??= uri.Host;
        var response = await base.SendAsync(request, cancellationToken);

        // Manual redirect handling — auto-redirect resends Authorization to the
        // new host; we strip credentials whenever the redirect leaves origin.
        for (var hop = 0;
             IsRedirect(response) && response.Headers.Location is { } next
             && hop < MaxRedirects;
             hop++)
        {
            var nextUri = next.IsAbsoluteUri ? next : new Uri(uri, next);
            if (await IsBlockedHostAsync(nextUri.Host, cancellationToken))
                return response; // surface the redirect; caller treats as error
            // A sent request's content stream may be consumed — only bodyless
            // requests (the connector norm: GET/HEAD) can be replayed safely.
            if (request.Content is not null)
                return response;
            request.RequestUri = nextUri;
            StripCredentialsOffOrigin(request, _originHost);
            response.Dispose();
            response = await base.SendAsync(request, cancellationToken);
            uri = nextUri;
        }
        return response;
    }

    private static bool IsRedirect(HttpResponseMessage r) =>
        (int)r.StatusCode is >= 300 and <= 399
        && r.StatusCode != HttpStatusCode.NotModified;

    /// <summary>Strip authorization before following a redirect to another
    /// host — .NET keeps request headers on redirect unless cleared.</summary>
    public static void StripCredentialsOffOrigin(HttpRequestMessage request, string originHost)
    {
        if (request.RequestUri?.Host is { } target
            && !string.Equals(target, originHost, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = null;
            request.Headers.Remove("X-Api-Key");
        }
    }

    private async Task<bool> IsBlockedHostAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return IsBlockedAddress(literal);

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            return false; // DNS failure surfaces downstream as a normal HTTP error
        }
        return addresses.Any(IsBlockedAddress);
    }

    private bool IsBlockedAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            // 169.254.0.0/16 — link-local incl. cloud metadata (169.254.169.254).
            if (b[0] == 169 && b[1] == 254)
                return true;
            // 0.0.0.0/8, 100.64.0.0/10 CGNAT, multicast/reserved 224.0.0.0+.
            if (b[0] == 0 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224)
                return true;
            if (!allowPrivateNetworks &&
                (IPAddress.IsLoopback(address)
                 || b[0] == 10
                 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                 || (b[0] == 192 && b[1] == 168)))
                return true;
            return false;
        }

        // IPv6: link-local fe80::/10 always (metadata equivalents); loopback
        // ::1 and unique-local fc00::/7 only when private nets are denied.
        var v6 = address.GetAddressBytes();
        if (v6[0] == 0xfe && (v6[1] & 0xc0) == 0x80)
            return true;
        if (!allowPrivateNetworks
            && (IPAddress.IsLoopback(address) || (v6[0] & 0xfe) == 0xfc))
            return true;
        return false;
    }
}
