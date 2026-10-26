using System.Collections.Concurrent;
using NUnit.Framework.Interfaces;

namespace Shop.Tests.Framework;

/// <summary>
/// Base class for every test fixture. Gives you:
///
///   Client(service, user)   a client bound to the right base URL and principal, cached per test
///   Cleanup(what, action)   deletes registered in reverse order at teardown
///   Unique(...)             run-scoped values that will not collide with earlier runs
///
/// On failure it prints every HTTP exchange the test made, so a red test rarely needs a re-run
/// to understand.
/// </summary>
public abstract class ApiTest
{
    private readonly ConcurrentDictionary<string, ApiClient> _clients = new();
    private readonly ConcurrentQueue<ApiResponse> _log = new();
    private readonly ConcurrentStack<(string What, Func<Task> Action)> _cleanups = new();

    protected TestSettings Settings => TestConfig.Settings;

    protected TokenProvider Tokens => TestSetup.Tokens;

    [TearDown]
    public async Task ApiTestTearDown()
    {
        // Cleanup first — its calls belong in the log too.
        while (_cleanups.TryPop(out var entry))
        {
            try
            {
                await entry.Action();
            }
            catch (Exception ex)
            {
                // A cleanup failure must never turn a passing test red or mask a real failure,
                // but it must be visible: leaked rows cause confusing conflicts later.
                TestContext.Out.WriteLine($"[cleanup] could not remove {entry.What}: {ex.Message}");
            }
        }

        if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed)
        {
            DumpRequestLog();
        }

        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync();
        }

        _clients.Clear();
        _log.Clear();
    }

    // ---------------------------------------------------------------- clients

    /// <summary>
    /// A client for the given service, acting as the given user (anonymous by default).
    /// Uses the run's Target mode; pass <paramref name="mode"/> to force Direct or Gateway.
    /// Clients are cached per test, so asking twice is free.
    /// </summary>
    protected async Task<ApiClient> Client(
        ShopService service, ShopUser? user = null, TargetMode? mode = null)
    {
        user ??= ShopUser.Anonymous;
        var baseUrl = Settings.BaseUrl(service, mode);
        var key = $"{baseUrl}|{user.Name}";

        if (_clients.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // A token source, not a token: it is invoked before every request the client makes, so
        // a long-running test can never send one that has since expired. TokenProvider caches,
        // so this is a dictionary lookup unless a refresh is genuinely due.
        var capturedUser = user;
        var created = await ApiClient.CreateAsync(
            baseUrl, () => Tokens.GetTokenAsync(capturedUser), _log.Enqueue);

        if (_clients.TryAdd(key, created))
        {
            return created;
        }

        // Lost a race within this test; keep whichever landed first.
        await created.DisposeAsync();
        return _clients[key];
    }

    /// <summary>
    /// A client that sends exactly this token, always — for malformed and tampered ones.
    /// Deliberately never refreshes: the whole point is to send something invalid.
    /// </summary>
    protected async Task<ApiClient> ClientWithToken(ShopService service, string token, TargetMode? mode = null)
    {
        var client = await ApiClient.CreateWithFixedTokenAsync(
            Settings.BaseUrl(service, mode), token, _log.Enqueue);

        _clients[$"raw:{Guid.NewGuid():N}"] = client;
        return client;
    }

    // ---------------------------------------------------------------- cleanup

    /// <summary>
    /// Registers something to remove at teardown. Cleanups run in reverse order and tolerate a
    /// 404 (the test may have deleted the resource itself).
    ///
    /// There is deliberately no "wipe the database between tests" hook: fixtures run in parallel
    /// against one shared stack, so a blanket wipe would delete another test's rows mid-flight.
    /// </summary>
    protected void Cleanup(string what, Func<Task> action) => _cleanups.Push((what, action));

    // ---------------------------------------------------------------- test data

    /// <summary>
    /// A value unique to this run, e.g. "AutoProduct-260911-142233-9f2c-7".
    ///
    /// This matters more than it looks. Product.BarCode, Product.QrCode and Customer.Email are
    /// uniqueness-checked server-side (409 Conflict), and the application's seeders only run
    /// against an empty table — so the second execution of a suite would otherwise start
    /// colliding with its own leftovers.
    /// </summary>
    protected static string Unique(string prefix) => $"{prefix}-{RunId}-{Interlocked.Increment(ref _sequence)}";

    /// <summary>Valid, unique, and obviously synthetic to anyone reading the database.</summary>
    protected static string UniqueEmail(string localPrefix = "auto") =>
        $"{localPrefix}.{RunId}.{Interlocked.Increment(ref _sequence)}@example.com".ToLowerInvariant();

    /// <summary>A 13-digit numeric barcode, unique per call.</summary>
    protected static string UniqueBarCode() =>
        "9" + DateTime.UtcNow.ToString("HHmmssfff") + Interlocked.Increment(ref _sequence).ToString("D3")[^3..];

    internal static readonly string RunId =
        DateTime.UtcNow.ToString("yyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];

    private static int _sequence;

    // ---------------------------------------------------------------- diagnostics

    protected void DumpRequestLog()
    {
        if (_log.IsEmpty)
        {
            return;
        }

        TestContext.Out.WriteLine();
        TestContext.Out.WriteLine($"--- HTTP exchanges for {TestContext.CurrentContext.Test.Name} ---");

        var index = 1;
        foreach (var entry in _log)
        {
            TestContext.Out.WriteLine($"[{index++}] {entry}");
        }

        TestContext.Out.WriteLine("--- end ---");
    }
}
