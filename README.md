# Shop test automation

Playwright + NUnit API automation for the **Shop** microservices. Standalone: no application
code here, and no reference to it. The system under test is identified only by its base URLs, so
the same suite runs against localhost, CI, or a deployed environment with a config change.

Right now it contains the framework, full authorization coverage, and one template fixture to
copy. Everything else is yours to add.

---

## Layout

```
shop-automation/
├─ Shop.Automation.slnx
├─ Directory.Build.props        net10.0, nullable, central package management
├─ Directory.Packages.props     every package version, in one place
├─ scripts/                     wait-for-stack, run-tests
└─ Shop.Tests/
   ├─ Shop.Tests.csproj
   ├─ appsettings.json          base URLs + Keycloak users
   ├─ TestSetup.cs              runs once: Playwright driver + token provider
   ├─ Models.cs                 DTOs the tests use
   ├─ Framework/
   │  ├─ TestConfig.cs          settings, loading, base-URL resolution
   │  ├─ ApiClient.cs           HTTP over Playwright: GET/POST/PUT/PATCH/DELETE
   │  ├─ ApiResponse.cs         a read response + the Should* assertions
   │  ├─ Auth.cs                ShopUser, TokenProvider, Jwt helpers
   │  ├─ Contracts.cs           PagedResult, ProblemDetails
   │  └─ ApiTest.cs             base fixture: clients, cleanup, unique data, logging
   └─ Tests/
      ├─ AuthorizationTests.cs  the authorization suite
      └─ ExampleTests.cs        TEMPLATE — copy this to start a new area
```

One project, on purpose. A `Core` / `Api` / `Tests` split only pays off once a second consumer
exists — a UI suite — and until then it is three csproj files and a reference graph for nothing.
When UI tests arrive, move `Framework/` into its own project then; the namespaces do not change.

---

## Quick start

Bring the application stack up however you normally do:

```bash
cd ../shop-demo && docker compose up --build -d
```

Then, from here:

```bash
./scripts/wait-for-stack.sh                 # or: pwsh scripts/wait-for-stack.ps1
dotnet test --filter "TestCategory=auth"
dotnet test --filter "TestCategory!=known-issue"
```

Or in one step:

```powershell
pwsh scripts/run-tests.ps1 -ComposePath ..\shop-demo
pwsh scripts/run-tests.ps1 -Filter "TestCategory=auth"
pwsh scripts/run-tests.ps1 -GatewayUrl https://shop-staging.example.com
```

---

## Writing a test

Copy `Tests/ExampleTests.cs`. The shape is always the same:

```csharp
[TestFixture]
[Category("orders")]
public class OrderTests : ApiTest
{
    [Test]
    public async Task Create_ReturnsCreated()
    {
        var api = await Client(ShopService.Order);                 // anonymous
        // var api = await Client(ShopService.Customer, ShopUser.Admin);   // authenticated

        var response = await api.PostAsync("/api/orders", new
        {
            name = Unique("AutoOrder"),
            price = 25.50m,
            quantity = 2,
            status = "InProgress",
        });

        response.ShouldBeCreated();
        var created = response.As<OrderResponse>();

        Cleanup($"order {created.Id}", () => api.DeleteAsync($"/api/orders/{created.Id}"));

        created.Name.ShouldNotBeNullOrWhiteSpace();
    }
}
```

What the base class gives you:

| Member | What it does |
|---|---|
| `Client(service, user?, mode?)` | A client bound to the right URL and principal. Cached per test. |
| `ClientWithToken(service, token)` | For malformed or tampered tokens. |
| `Cleanup(what, action)` | Runs in reverse order at teardown; failures are logged, never thrown. |
| `Unique(prefix)`, `UniqueEmail()`, `UniqueBarCode()` | Values namespaced by a per-run id. |
| `Tokens` | The shared `TokenProvider`. |
| `Settings` | The loaded configuration. |

What a response gives you:

| Member | What it does |
|---|---|
| `ShouldBeOk()` `ShouldBeCreated()` `ShouldBeNoContent()` `ShouldBeBadRequest()` `ShouldBeUnauthorized()` `ShouldBeForbidden()` `ShouldBeNotFound()` `ShouldBeConflict()` `ShouldBe(int)` | Status assertions; the failure message carries the whole exchange. |
| `As<T>()` | Deserialize the body. |
| `ShouldBeProblem(status, title?)` | Status + `application/problem+json` + title, returns the document. |
| `ShouldBeValidationError("Prop")` | A 400 with an `errors` entry for each named property. |
| `ShouldHaveLocationHeader()` | For 201s. |

Query strings are anonymous objects; nulls are omitted:

```csharp
await api.GetAsync("/api/products", new { page = 1, pageSize = 5, search = "coffee" });
```

Two habits the framework assumes:

- **Never assert an absolute row count.** The application's seeders only run against an empty
  table, so after the first run the database holds both the seed data and earlier runs' output.
  Assert on data your test created.
- **Give every test its own data.** `BarCode`, `QrCode` and `Email` are uniqueness-checked
  server-side and answer 409.

---

## The ports, and why one is shared

The application publishes each service on its own port **and** puts an Ocelot gateway in front of
all three on **port 5000**, routing by path prefix:

| URL | What answers |
|---|---|
| `localhost:5001/api/products` · `5002/api/customers` · `5003/api/orders` | each service directly |
| `localhost:5000/api/products` · `/api/customers` · `/api/orders` | the gateway, forwarding to the same three |

The suite runs through the **gateway** by default (`"Target": "Gateway"`), because that is the
supported entry point — the way real clients call the system.

The per-service URLs still belong in the config, for two reasons: a few tests force one mode
explicitly to prove the gateway and the service behave differently (the gateway rejects an
invalid token before the service is reached; the service validates the token again on its own),
and Direct is the fastest way to tell whether a failure is the gateway's or the service's.

```bash
SHOP_TEST_Target=Direct dotnet test    # bypass the gateway for a run
```

A single test can force one entry point regardless of the run's default:

```csharp
var api = await Client(ShopService.Customer, ShopUser.Anonymous, TargetMode.Gateway);
```

---

## Configuration

`Shop.Tests/appsettings.json`, overlaid by `appsettings.{SHOP_TEST_ENV}.json`, overlaid by
environment variables prefixed `SHOP_TEST_` (double underscore for nesting):

```bash
SHOP_TEST_Target=Gateway
SHOP_TEST_Services__Gateway=https://shop-staging.example.com
SHOP_TEST_Keycloak__BaseUrl=https://id-staging.example.com
SHOP_TEST_Keycloak__Users__Admin__Password=...
```

The committed credentials are the realm's throwaway local test users, whose passwords are
already published in the application's own README. For any environment where that is not true,
supply them as environment variables from your secret store.

---

## Authentication

`TokenProvider` gets tokens straight from Keycloak's direct-grant endpoint and caches them per
user for the run, refreshing 30 seconds before expiry. It deliberately does **not** use
`/api/customers/dev/*` — those are endpoints under test, and a regression there should fail one
test, not the whole suite.

**Tokens are resolved per request, not per client.** A Playwright request context's headers are
fixed at construction, so a token baked in there can expire while a long test is still running —
and the test then fails with a 401 that reads like an authorization bug. Instead `ApiClient`
holds a *token source* and calls it immediately before every request, so the cache decides
freshness at the last possible moment. It costs a dictionary lookup; Keycloak is contacted only
when a refresh is genuinely due.

There is deliberately **no retry-on-401**. It is the reflex fix and it is wrong here: a large
part of this suite asserts 401 on purpose, so retrying would double those tests' runtime and
quietly mask a real regression. If a 401 does slip through, `ShouldBe` notices that the request
carried a token and says so in the failure message, pointing at expiry, clock skew and
issuer/audience — the three actual causes.

`ClientWithToken` never refreshes, by design: it exists to send tokens that are *supposed* to
be invalid.

```csharp
await Client(ShopService.Customer, ShopUser.Admin);      // admin1
await Client(ShopService.Customer, ShopUser.Customer);   // alice
await Client(ShopService.Customer);                      // no Authorization header
```

`Jwt.Audiences()`, `Jwt.RealmRoles()`, `Jwt.Claim()` read a token's payload for assertions;
`Jwt.Tamper()` breaks a signature while leaving the rest well-formed.

`AuthorizationTests` covers: token shape (issuer, audience, roles), 401 for anonymous, garbage
and tampered tokens, 403 for a valid non-Admin token, 200 for the allowed cases, `/me` for both
roles, and that the gateway rejects invalid tokens before the service is reached while leaving
role decisions to it.

---

## Parallelism

Assembly-level `[Parallelizable(ParallelScope.Fixtures)]` with `LevelOfParallelism(4)`: fixtures
run concurrently, tests inside a fixture run one at a time. That keeps `ApiTest`'s per-test
instance state safe. A fixture that wants its own tests parallel must add **both**
`[Parallelizable(ParallelScope.All)]` and `[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]`.

---

## Diagnostics

Every request is recorded. On failure the base fixture prints the full exchange — method, URL,
request body, status, response body, elapsed time — into the test output.

---

## Known issue pinned here

`AuthorizationTests.DevTokenEndpoint_IsReachableAnonymouslyAndHandsOutAnAdminToken`, tagged
`[Category("known-issue")]` and excluded from the default filter.

`DevAuthController` is `[AllowAnonymous]` unconditionally — not gated on `IsDevelopment()` — so
any caller who can reach the service, or the gateway, can mint a working Admin token using the
credentials in the application's README. The test asserts the current behaviour so that gating
the controller fails here deliberately.

Four more defects were found and are not yet covered by a test of their own:

- **Error responses are served as `application/json`, not `application/problem+json`.**
  `ExceptionHandlingMiddleware` sets the right content type and then calls
  `WriteAsJsonAsync(problem)`, which overwrites it. The body is a correct RFC 7807 document under
  the wrong media type, so a client branching on the content type cannot tell an error from a
  normal payload. Fix: `WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json")`.
  Until then `ShouldBeProblem(..., requireProblemContentType: true)` is opt-in; flip the default
  in `ApiResponse.cs` once it ships. Affects the middleware's 404/409/500 only — validation 400s
  come from `[ApiController]` and are correct.

- The application README documents `GET /api/customers/dev/admin-token`; the controller exposes
  `POST /api/customers/dev/token`.
- `PUT /api/orders/{id}` silently writes `DateTime.MinValue` when `orderDate` is omitted —
  `UpdateOrderRequest.OrderDate` is a non-nullable `DateTime` with no `[Required]`.
- `PagedRequest.Page` is unvalidated (`PageSize` is clamped, `Page` is not), so `page=0` reaches
  the data layer as a negative `Skip`.

---

## Adding UI tests later

1. Move `Framework/` into its own class library and reference it from both test projects; the
   namespaces stay as they are.
2. Add `Microsoft.Playwright.NUnit` (already commented into `Directory.Packages.props`) and
   install browsers: `pwsh bin/Debug/net10.0/playwright.ps1 install`.
3. Get a token from `TokenProvider` and seed it into storage state, so only the login tests
   drive the login form.
4. Use the API to arrange state and to verify results out-of-band.
5. Locator policy: `Page.GetByTestId` against `data-testid`, role-based locators otherwise. No
   CSS paths, no `Thread.Sleep` — web-first assertions only.
