using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace Shop.Tests.Framework;

/// <summary>
/// A thin HTTP client over Playwright's IAPIRequestContext, bound to one base URL and (optionally)
/// one bearer token. Everything the tests send or receive goes through here.
/// </summary>
public sealed class ApiClient : IAsyncDisposable
{
    /// <summary>
    /// Matches what the services actually put on the wire: camelCase names, and OrderStatus as
    /// its string name (Order.Api registers a plain JsonStringEnumConverter, so it writes
    /// "InProgress", not 0 and not "inProgress").
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static IPlaywright? _playwright;

    private readonly IAPIRequestContext _context;
    private readonly Func<Task<string?>>? _tokenSource;
    private readonly Action<ApiResponse>? _onResponse;

    private ApiClient(
        IAPIRequestContext context, Func<Task<string?>>? tokenSource, Action<ApiResponse>? onResponse)
    {
        _context = context;
        _tokenSource = tokenSource;
        _onResponse = onResponse;
    }

    /// <summary>
    /// Starts the Playwright driver process. Called once per test run from TestSetup — creating
    /// it per test would spawn a process per test.
    /// </summary>
    public static async Task InitializeAsync() => _playwright ??= await Playwright.CreateAsync();

    public static void Shutdown()
    {
        _playwright?.Dispose();
        _playwright = null;
    }

    /// <param name="baseUrl">Every request path is relative to this.</param>
    /// <param name="tokenSource">
    /// Resolved immediately before EVERY request, not once when the client is built.
    ///
    /// That matters because a Playwright request context's headers are fixed at construction: a
    /// token baked in there can go stale while a long test is still running, and the test then
    /// fails with a 401 that reads like an authorization bug rather than an expiry. Resolving per
    /// request means TokenProvider's cache decides freshness at the last possible moment, so the
    /// window is gone. The cost is a dictionary lookup, not a network call — the provider only
    /// contacts Keycloak when the cached token is actually near expiry.
    ///
    /// Null means an anonymous caller, and no Authorization header is sent at all.
    ///
    /// Note there is deliberately no retry-on-401 anywhere: a large part of this suite asserts
    /// 401 on purpose, so retrying would double those tests' runtime and mask real regressions.
    /// </param>
    /// <param name="onResponse">Called for every response — used to build the per-test request log.</param>
    public static async Task<ApiClient> CreateAsync(
        string baseUrl, Func<Task<string?>>? tokenSource = null, Action<ApiResponse>? onResponse = null)
    {
        if (_playwright is null)
        {
            throw new InvalidOperationException(
                $"Playwright is not initialised. {nameof(TestSetup)}.OneTimeSetUp must run first.");
        }

        var context = await _playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = baseUrl.TrimEnd('/'),
            // Only headers that never change live here. Authorization is added per request.
            ExtraHTTPHeaders = new Dictionary<string, string> { ["Accept"] = "application/json" },
            Timeout = TestConfig.Settings.RequestTimeoutMs,
            IgnoreHTTPSErrors = true,
            // Off deliberately: this suite asserts on 400/401/403/404/409 constantly, so a
            // non-2xx must be a value to inspect, never a thrown exception.
            FailOnStatusCode = false,
        });

        return new ApiClient(context, tokenSource, onResponse);
    }

    /// <summary>A client that always sends exactly this token — for tampered and malformed ones.</summary>
    public static Task<ApiClient> CreateWithFixedTokenAsync(
        string baseUrl, string token, Action<ApiResponse>? onResponse = null) =>
        CreateAsync(baseUrl, () => Task.FromResult<string?>(token), onResponse);

    // ---------------------------------------------------------------- verbs

    /// <param name="query">An anonymous object: <c>new { page = 1, search = "x" }</c>. Nulls are omitted.</param>
    public Task<ApiResponse> GetAsync(string path, object? query = null) =>
        SendAsync("GET", path + BuildQuery(query), body: null);

    public Task<ApiResponse> PostAsync(string path, object? body = null) => SendAsync("POST", path, body);

    public Task<ApiResponse> PutAsync(string path, object? body = null) => SendAsync("PUT", path, body);

    public Task<ApiResponse> PatchAsync(string path, object? body = null) => SendAsync("PATCH", path, body);

    public Task<ApiResponse> DeleteAsync(string path) => SendAsync("DELETE", path, body: null);

    /// <summary>
    /// Sends a body verbatim instead of serializing one. Use it for payloads a model cannot
    /// express — malformed JSON, or a field deliberately left out.
    /// </summary>
    public Task<ApiResponse> SendRawAsync(
        string method, string path, string rawBody, string contentType = "application/json") =>
        SendCoreAsync(method, path, rawBody, contentType);

    private Task<ApiResponse> SendAsync(string method, string path, object? body) =>
        SendCoreAsync(
            method,
            path,
            body is null ? null : JsonSerializer.Serialize(body, Json),
            body is null ? null : "application/json");

    private async Task<ApiResponse> SendCoreAsync(string method, string path, string? payload, string? contentType)
    {
        var headers = new Dictionary<string, string>();

        if (contentType is not null)
        {
            headers["Content-Type"] = contentType;
        }

        // Resolved here, at the last possible moment, so a token cannot go stale mid-test.
        var token = _tokenSource is null ? null : await _tokenSource();
        var authenticated = !string.IsNullOrWhiteSpace(token);

        if (authenticated)
        {
            headers["Authorization"] = "Bearer " + token;
        }

        // Data (a raw string) rather than DataObject: DataObject would use Playwright's own
        // serializer, which writes enums as integers. The services expect the string names.
        var options = new APIRequestContextOptions
        {
            Data = payload,
            Headers = headers.Count == 0 ? null : headers,
        };

        var stopwatch = Stopwatch.StartNew();

        var response = method switch
        {
            "GET" => await _context.GetAsync(path, options),
            "POST" => await _context.PostAsync(path, options),
            "PUT" => await _context.PutAsync(path, options),
            "PATCH" => await _context.PatchAsync(path, options),
            "DELETE" => await _context.DeleteAsync(path, options),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported HTTP method."),
        };

        // Drain the body now: Playwright's response is a live handle tied to the context's
        // lifetime, so it cannot be read twice or read after disposal.
        var text = await response.TextAsync();
        stopwatch.Stop();

        var result = new ApiResponse
        {
            Method = method,
            Url = response.Url,
            Status = response.Status,
            Body = text,
            Headers = response.Headers,
            RequestBody = payload,
            Elapsed = stopwatch.Elapsed,
            WasAuthenticated = authenticated,
        };

        await response.DisposeAsync();

        _onResponse?.Invoke(result);
        return result;
    }

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    // ---------------------------------------------------------------- query strings

    private static string BuildQuery(object? query)
    {
        if (query is null)
        {
            return string.Empty;
        }

        var pairs = query.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (property.Name, Value: property.GetValue(query)))
            .Where(pair => pair.Value is not null)
            .Select(pair => $"{Uri.EscapeDataString(pair.Name)}={Uri.EscapeDataString(Format(pair.Value!))}")
            .ToArray();

        return pairs.Length == 0 ? string.Empty : "?" + string.Join('&', pairs);
    }

    private static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        // Round-trip format — unambiguous for the DateTime model binder, and keeps the UTC marker.
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
