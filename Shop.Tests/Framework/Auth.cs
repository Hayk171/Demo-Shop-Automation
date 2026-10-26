using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Shop.Tests.Framework;

/// <summary>
/// A principal the suite can act as. The two named users are the ones seeded into the realm by
/// keycloak/realm-export.json; <see cref="Anonymous"/> is simply the absence of a token.
/// </summary>
public sealed class ShopUser
{
    /// <summary>Key into Keycloak:Users in appsettings.json. Null for the anonymous caller.</summary>
    public string? ConfigKey { get; }

    public string Name { get; }

    /// <summary>The realm role this user is expected to carry.</summary>
    public string? ExpectedRole { get; }

    private ShopUser(string name, string? configKey, string? expectedRole)
    {
        Name = name;
        ConfigKey = configKey;
        ExpectedRole = expectedRole;
    }

    /// <summary>admin1 — realm role Admin. Everything under /api/customers is open to them.</summary>
    public static readonly ShopUser Admin = new("Admin", "Admin", "Admin");

    /// <summary>alice — realm role Customer. Only /api/customers/me is open to her.</summary>
    public static readonly ShopUser Customer = new("Customer", "Customer", "Customer");

    /// <summary>No Authorization header at all.</summary>
    public static readonly ShopUser Anonymous = new("Anonymous", null, null);

    public bool IsAnonymous => ConfigKey is null;

    public override string ToString() => Name;
}

/// <summary>
/// Mints Keycloak access tokens with the direct access grant (the shop-test-client public client
/// has directAccessGrantsEnabled) and caches them for the run.
///
/// It deliberately does NOT go through Customer.Api's /api/customers/dev/* endpoints. Those are
/// endpoints under test: if the framework authenticated through them, a regression there would
/// take the entire suite down instead of failing the handful of tests that cover them.
/// </summary>
public sealed class TokenProvider : IAsyncDisposable
{
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    private readonly KeycloakSettings _keycloak;
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ApiClient? _client;

    public TokenProvider(KeycloakSettings keycloak) => _keycloak = keycloak;

    /// <summary>A valid access token for the user, or null for <see cref="ShopUser.Anonymous"/>.</summary>
    public async Task<string?> GetTokenAsync(ShopUser user)
    {
        if (user.IsAnonymous)
        {
            return null;
        }

        var key = user.ConfigKey!;

        if (_cache.TryGetValue(key, out var cached) && !cached.IsExpiring)
        {
            return cached.AccessToken;
        }

        await _gate.WaitAsync();
        try
        {
            // Re-check: another fixture may have refreshed it while we waited on the gate.
            if (_cache.TryGetValue(key, out cached) && !cached.IsExpiring)
            {
                return cached.AccessToken;
            }

            if (!_keycloak.Users.TryGetValue(key, out var credentials))
            {
                throw new InvalidOperationException(
                    $"No credentials configured for Keycloak user '{key}'. " +
                    $"Add Keycloak:Users:{key}:Username and :Password to appsettings.json.");
            }

            var response = await RequestTokenAsync(credentials.Username, credentials.Password);

            if (response.Status != 200)
            {
                throw new InvalidOperationException(
                    $"Keycloak refused the token request for '{credentials.Username}'.{Environment.NewLine}{response}");
            }

            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;

            if (!root.TryGetProperty("access_token", out var tokenElement) ||
                tokenElement.GetString() is not { Length: > 0 } accessToken)
            {
                throw new InvalidOperationException(
                    $"Keycloak's response contained no access_token.{Environment.NewLine}{response}");
            }

            var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                ? seconds
                : 60;

            var fresh = new CachedToken(accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
            _cache[key] = fresh;
            return accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The raw password-grant request, so tests can assert on failure modes too — a wrong
    /// password, an unknown user, a disabled client.
    /// </summary>
    public async Task<ApiResponse> RequestTokenAsync(string username, string password, string? clientId = null)
    {
        _client ??= await ApiClient.CreateAsync(_keycloak.BaseUrl);

        var form = new StringBuilder()
            .Append("client_id=").Append(Uri.EscapeDataString(clientId ?? _keycloak.ClientId))
            .Append("&grant_type=password")
            .Append("&username=").Append(Uri.EscapeDataString(username))
            .Append("&password=").Append(Uri.EscapeDataString(password))
            .ToString();

        return await _client.SendRawAsync(
            "POST",
            $"/realms/{_keycloak.Realm}/protocol/openid-connect/token",
            form,
            "application/x-www-form-urlencoded");
    }

    /// <summary>Drops the cached token so the next call re-authenticates.</summary>
    public void Invalidate(ShopUser user)
    {
        if (user.ConfigKey is { } key)
        {
            _cache.TryRemove(key, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        _gate.Dispose();
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt)
    {
        public bool IsExpiring => DateTimeOffset.UtcNow >= ExpiresAt - ExpiryMargin;
    }
}

/// <summary>
/// Reads a JWT payload WITHOUT verifying it, for asserting on claims and for producing a
/// deliberately broken token. This is a test helper — it is not, and must never become,
/// a validation routine.
/// </summary>
public static class Jwt
{
    public static JsonElement Payload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            throw new ArgumentException(
                $"Not a JWT: expected at least two dot-separated segments, got {parts.Length}.", nameof(token));
        }

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
        return document.RootElement.Clone();
    }

    public static string? Claim(string token, string name) =>
        Payload(token).TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>"aud" is a string or an array of strings in a JWT, so this always returns a list.</summary>
    public static IReadOnlyList<string> Audiences(string token)
    {
        var payload = Payload(token);
        if (!payload.TryGetProperty("aud", out var aud))
        {
            return [];
        }

        return aud.ValueKind switch
        {
            JsonValueKind.String => [aud.GetString()!],
            JsonValueKind.Array => aud.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList(),
            _ => [],
        };
    }

    /// <summary>Realm roles from realm_access.roles — what KeycloakRoleClaimsTransformation flattens.</summary>
    public static IReadOnlyList<string> RealmRoles(string token)
    {
        var payload = Payload(token);
        if (!payload.TryGetProperty("realm_access", out var realmAccess) ||
            !realmAccess.TryGetProperty("roles", out var roles) ||
            roles.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return roles.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    /// <summary>
    /// The same token with one character of the signature changed: header and payload stay
    /// perfectly well-formed, the signature no longer verifies. Proves the services validate
    /// signatures rather than merely parsing tokens.
    /// </summary>
    public static string Tamper(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw new ArgumentException("Expected a three-segment JWT to tamper with.", nameof(token));
        }

        var signature = parts[2];
        parts[2] = signature[..^1] + (signature[^1] == 'A' ? 'B' : 'A');
        return string.Join('.', parts);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("Invalid base64url length."),
        };

        return Convert.FromBase64String(padded);
    }
}
