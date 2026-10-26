using Microsoft.Extensions.Configuration;

namespace Shop.Tests.Framework;

/// <summary>The services the suite can address.</summary>
public enum ShopService
{
    Gateway,
    Product,
    Customer,
    Order,
}

/// <summary>
/// Which entry point to use.
///
/// The application publishes each service on its own port AND puts an Ocelot gateway in front of
/// all three on port 5000, routing by path prefix. Both are supposed to behave identically, so
/// the whole suite can be pointed at either: set Target in appsettings.json, or run
/// <c>SHOP_TEST_Target=Gateway dotnet test</c>.
/// </summary>
public enum TargetMode
{
    Direct,
    Gateway,
}

/// <summary>Loads test settings once per run.</summary>
public static class TestConfig
{
    private static readonly Lazy<TestSettings> Lazy = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static TestSettings Settings => Lazy.Value;

    /// <summary>Picks the overlay file: appsettings.{this}.json. Defaults to "local".</summary>
    public static string EnvironmentName =>
        Environment.GetEnvironmentVariable("SHOP_TEST_ENV") is { Length: > 0 } value ? value : "local";

    private static TestSettings Load()
    {
        // appsettings.json  ->  appsettings.{env}.json  ->  SHOP_TEST_* environment variables.
        // Environment variables use double underscore for nesting, e.g.
        //   SHOP_TEST_Target=Gateway
        //   SHOP_TEST_Services__Product=http://localhost:15001
        //   SHOP_TEST_Keycloak__Users__Admin__Password=...
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{EnvironmentName}.json", optional: true)
            .AddEnvironmentVariables("SHOP_TEST_")
            .Build();

        var settings = new TestSettings();
        configuration.Bind(settings);
        return settings;
    }
}

public sealed class TestSettings
{
    public TargetMode Target { get; set; } = TargetMode.Direct;

    /// <summary>Base URL per service, keyed by <see cref="ShopService"/> name.</summary>
    public Dictionary<string, string> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public KeycloakSettings Keycloak { get; set; } = new();

    public int RequestTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Resolves the base URL for a service. In Gateway mode every service resolves to the
    /// gateway's URL, because routing there happens on the path prefix, not the port.
    /// Pass <paramref name="mode"/> to force one entry point regardless of the run's default.
    /// </summary>
    public string BaseUrl(ShopService service, TargetMode? mode = null)
    {
        var useGateway = service == ShopService.Gateway || (mode ?? Target) == TargetMode.Gateway;
        var key = useGateway ? nameof(ShopService.Gateway) : service.ToString();

        if (!Services.TryGetValue(key, out var url) || string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                $"No base URL configured for '{key}'. Add Services:{key} to appsettings.json " +
                $"or set SHOP_TEST_Services__{key}.");
        }

        return url.TrimEnd('/');
    }
}

public sealed class KeycloakSettings
{
    public string BaseUrl { get; set; } = "http://localhost:8081";

    public string Realm { get; set; } = "shop";

    /// <summary>Public client with direct access grants enabled — see keycloak/realm-export.json.</summary>
    public string ClientId { get; set; } = "shop-test-client";

    /// <summary>Expected "aud" claim, injected by the realm's audience protocol mapper.</summary>
    public string Audience { get; set; } = "shop-api";

    public Dictionary<string, Credentials> Users { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string TokenEndpoint => $"{BaseUrl.TrimEnd('/')}/realms/{Realm}/protocol/openid-connect/token";

    public string Issuer => $"{BaseUrl.TrimEnd('/')}/realms/{Realm}";
}

public sealed class Credentials
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
