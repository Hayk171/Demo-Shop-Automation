using Shop.Tests.Framework;

namespace Shop.Tests.Tests;

/// <summary>
/// Authorization on Customer.Api — the only module in the solution that authenticates.
///
/// Two expectations that are easy to confuse, and that this fixture keeps apart:
///
///   401 Unauthorized — no token, or a token that does not validate (bad signature, wrong
///                      issuer or audience, expired).
///   403 Forbidden    — a perfectly valid token whose roles do not satisfy the policy.
///
/// The customer ids used below deliberately do not exist. Authorization runs before the action,
/// so a 404 here would itself be the bug: it would tell an unauthorised caller which ids exist.
/// </summary>
[TestFixture]
[Category("auth")]
public class AuthorizationTests : ApiTest
{
    // A string const so it can be concatenated inside [TestCase] attributes, which require
    // compile-time constants ("path" + someInt is not one).
    private const string NonExistentId = "987654321";

    // ---------------------------------------------------------------- the tokens themselves

    [Test]
    public async Task AdminToken_CarriesTheExpectedIssuerAudienceAndRole()
    {
        // If the realm is ever re-imported without its audience protocol mapper, every
        // authenticated test turns 401 at once. This fixture says why in one line rather than
        // leaving a screen of red tests to interpret.
        var token = await Tokens.GetTokenAsync(ShopUser.Admin);

        token.ShouldNotBeNullOrWhiteSpace();
        Jwt.Audiences(token!).ShouldContain(Settings.Keycloak.Audience);
        Jwt.RealmRoles(token!).ShouldContain("Admin");
        Jwt.Claim(token!, "iss").ShouldBe(
            Settings.Keycloak.Issuer,
            "Keycloak's KC_HOSTNAME pins the issuer, and the services validate against exactly this value.");
    }

    [Test]
    public async Task CustomerToken_CarriesTheCustomerRoleButNotAdmin()
    {
        var token = await Tokens.GetTokenAsync(ShopUser.Customer);

        token.ShouldNotBeNullOrWhiteSpace();
        Jwt.RealmRoles(token!).ShouldContain("Customer");
        Jwt.RealmRoles(token!).ShouldNotContain("Admin");
    }

    [Test]
    public async Task Keycloak_RefusesAWrongPassword()
    {
        var admin = Settings.Keycloak.Users["Admin"];

        var response = await Tokens.RequestTokenAsync(admin.Username, "definitely-the-wrong-password");

        response.Status.ShouldBe(401);
        response.Body.ShouldNotContain("access_token");
    }

    // ---------------------------------------------------------------- 401: not authenticated

    [Test]
    public async Task ListCustomers_Anonymous_Is401()
    {
        var api = await Client(ShopService.Customer);

        var response = await api.GetAsync("/api/customers");

        response.ShouldBeUnauthorized();
    }

    [Test]
    public async Task Service_Rejects401OnItsOwn_NotOnlyBehindTheGateway()
    {
        // Forced to Direct. The suite normally runs through the gateway, so without this the
        // service's own JWT validation would never be exercised — every 401 would be the
        // gateway's. Customer.Api is documented as never trusting the gateway's check, and this
        // is what proves it.
        var api = await Client(ShopService.Customer, ShopUser.Anonymous, TargetMode.Direct);

        var response = await api.GetAsync("/api/customers");

        response.ShouldBeUnauthorized();
    }

    [Test]
    public async Task Me_Anonymous_Is401()
    {
        var api = await Client(ShopService.Customer);

        var response = await api.GetAsync("/api/customers/me");

        response.ShouldBeUnauthorized();
    }

    [Test]
    public async Task AnyEndpoint_WithGarbageInTheAuthorizationHeader_Is401()
    {
        var api = await ClientWithToken(ShopService.Customer, "this-is-not-a-jwt");

        var response = await api.GetAsync("/api/customers");

        response.ShouldBeUnauthorized();
    }

    [Test]
    public async Task AnyEndpoint_WithATamperedSignature_Is401()
    {
        // Same header and payload as a real token, one character changed in the signature.
        // If this passes, the service is parsing tokens rather than validating them.
        var valid = await Tokens.GetTokenAsync(ShopUser.Admin);
        var api = await ClientWithToken(ShopService.Customer, Jwt.Tamper(valid!));

        var response = await api.GetAsync("/api/customers");

        response.ShouldBeUnauthorized();
    }

    // ---------------------------------------------------------------- 403: authenticated, not allowed

    [TestCase("GET", "/api/customers")]
    [TestCase("GET", "/api/customers/" + NonExistentId)]
    [TestCase("DELETE", "/api/customers/" + NonExistentId)]
    [TestCase("GET", "/api/customers/admin/users/some-keycloak-id")]
    public async Task AdminOnlyEndpoint_AsCustomerRole_Is403(string method, string path)
    {
        // alice authenticates perfectly well — she simply is not an Admin. The difference
        // between 403 and 401 is what tells a client "you will never be allowed" apart from
        // "log in again".
        var api = await Client(ShopService.Customer, ShopUser.Customer);

        var response = method switch
        {
            "GET" => await api.GetAsync(path),
            "DELETE" => await api.DeleteAsync(path),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

        response.ShouldBeForbidden();
    }

    [Test]
    public async Task CreateCustomer_AsCustomerRole_Is403()
    {
        var api = await Client(ShopService.Customer, ShopUser.Customer);

        var response = await api.PostAsync("/api/customers", new
        {
            firstName = "Auth",
            lastName = "Probe",
            email = UniqueEmail(),
        });

        response.ShouldBeForbidden();
    }

    // ---------------------------------------------------------------- 200: allowed

    [Test]
    public async Task ListCustomers_AsAdmin_Is200()
    {
        var api = await Client(ShopService.Customer, ShopUser.Admin);

        var response = await api.GetAsync("/api/customers", new { pageSize = 1 });

        response.ShouldBeOk();
        response.As<PagedResult<object>>().PageSize.ShouldBe(1);
    }

    [Test]
    public async Task Me_AsCustomer_ReturnsAlicesIdentityFromHerToken()
    {
        var api = await Client(ShopService.Customer, ShopUser.Customer);
        var token = await Tokens.GetTokenAsync(ShopUser.Customer);

        var response = await api.GetAsync("/api/customers/me");

        response.ShouldBeOk();
        var me = response.As<MeResponse>();
        me.Username.ShouldBe(Settings.Keycloak.Users["Customer"].Username);
        me.Id.ShouldBe(Jwt.Claim(token!, "sub"), "Id comes from the token's sub claim.");
        me.Roles.ShouldContain("Customer");
        me.Roles.ShouldNotContain("Admin");
    }

    [Test]
    public async Task Me_AsAdmin_IsAlsoAllowed()
    {
        // /me carries the looser CustomerAccess policy: Customer OR Admin.
        var api = await Client(ShopService.Customer, ShopUser.Admin);

        var response = await api.GetAsync("/api/customers/me");

        response.ShouldBeOk();
        response.As<MeResponse>().Roles.ShouldContain("Admin");
    }

    [Test]
    public async Task Me_LeaksNoToken()
    {
        // MeResponse is documented as "no secrets, no raw token". Worth enforcing, because the
        // obvious future 'improvement' to this endpoint is to echo the token back for debugging.
        var api = await Client(ShopService.Customer, ShopUser.Customer);
        var token = await Tokens.GetTokenAsync(ShopUser.Customer);

        var response = await api.GetAsync("/api/customers/me");

        response.ShouldBeOk();
        response.Body.ShouldNotContain(token!);
        response.Body.ShouldNotContain("access_token");
    }

    // ---------------------------------------------------------------- defence in depth

    [Test]
    public async Task Gateway_Rejects401BeforeTheRequestReachesTheService()
    {
        // ocelot.json puts AuthenticationOptions on the /api/customers routes, so an invalid
        // token never reaches Customer.Api at all. Forced to Gateway mode regardless of the
        // run's default, because that is the whole point of the test.
        var api = await Client(ShopService.Customer, ShopUser.Anonymous, TargetMode.Gateway);

        var response = await api.GetAsync("/api/customers");

        response.ShouldBeUnauthorized();
    }

    [Test]
    public async Task Gateway_ForwardsAValidTokenAndLetsTheServiceDecideTheRole()
    {
        // The gateway only asks "is this token valid". alice's token IS valid, so it gets
        // forwarded; Customer.Api is what turns it into a 403. A 401 here would mean the gateway
        // had started making role decisions it has no policies for.
        var api = await Client(ShopService.Customer, ShopUser.Customer, TargetMode.Gateway);

        var response = await api.GetAsync("/api/customers");

        response.ShouldBeForbidden(
            "A valid non-Admin token must reach the service and be refused there, not bounced at the gateway.");
    }

    [Test]
    public async Task ProductsAndOrders_RequireNoToken()
    {
        // Only Customer.Api authenticates. If a blanket auth filter is ever added, these turn
        // 401 and this test says so immediately.
        var products = await Client(ShopService.Product);
        var orders = await Client(ShopService.Order);

        (await products.GetAsync("/api/products", new { pageSize = 1 })).ShouldBeOk();
        (await orders.GetAsync("/api/orders", new { pageSize = 1 })).ShouldBeOk();
    }

    // ---------------------------------------------------------------- known issue

    [Test]
    [Category("known-issue")]
    public async Task DevTokenEndpoint_IsReachableAnonymouslyAndHandsOutAnAdminToken()
    {
        // DevAuthController is [AllowAnonymous] unconditionally — it is NOT gated on
        // app.Environment.IsDevelopment(). Anyone who can reach the service, or the gateway, can
        // mint a working Admin token using credentials published in the application's README.
        //
        // This asserts the CURRENT behaviour so that gating the controller shows up here as an
        // intentional change rather than a mystery failure somewhere else. Excluded from the
        // default run:  dotnet test --filter "TestCategory!=known-issue"
        var api = await Client(ShopService.Customer);
        var admin = Settings.Keycloak.Users["Admin"];

        var response = await api.PostAsync("/api/customers/dev/token", new
        {
            username = admin.Username,
            password = admin.Password,
        });

        response.ShouldBeOk(
            "If this is now 401/403/404, the dev token endpoints have been gated - delete this test.");

        var accessToken = response.As<DevTokenResponse>().AccessToken;
        Jwt.RealmRoles(accessToken).ShouldContain("Admin", "An unauthenticated caller just obtained Admin.");
    }

    private sealed class DevTokenResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }
}
