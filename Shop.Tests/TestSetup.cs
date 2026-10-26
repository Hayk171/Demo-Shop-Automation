using Shop.Tests.Framework;

// Fixtures run in parallel with each other; the tests INSIDE a fixture run one at a time.
//
// That combination is deliberate. ApiTest keeps per-test state in instance fields (the client
// cache, the request log, the cleanup stack), and NUnit reuses one fixture instance for all of
// its tests unless told otherwise. ParallelScope.Fixtures keeps that state safe while still
// giving real concurrency, because there are many fixtures.
//
// A fixture that wants its own tests parallel must opt in with BOTH
// [Parallelizable(ParallelScope.All)] and [FixtureLifeCycle(LifeCycle.InstancePerTestCase)].
[assembly: Parallelizable(ParallelScope.Fixtures)]
[assembly: LevelOfParallelism(4)]

namespace Shop.Tests;

/// <summary>
/// Runs once for the whole assembly: starts the Playwright driver and creates the shared token
/// provider, so Keycloak is hit twice per run rather than twice per test.
/// </summary>
[SetUpFixture]
public class TestSetup
{
    public static TokenProvider Tokens { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var settings = TestConfig.Settings;

        await ApiClient.InitializeAsync();
        Tokens = new TokenProvider(settings.Keycloak);

        TestContext.Progress.WriteLine(
            $"""
             Shop API tests
               run id      : {Framework.ApiTest.RunId}
               environment : {TestConfig.EnvironmentName}
               target      : {settings.Target}
               keycloak    : {settings.Keycloak.BaseUrl} (realm {settings.Keycloak.Realm})
             """);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await Tokens.DisposeAsync();
        ApiClient.Shutdown();
    }
}
