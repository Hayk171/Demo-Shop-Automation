using Shop.Tests.Framework;

namespace Shop.Tests.Tests;

/// <summary>
/// ===========================================================================================
///  TEMPLATE — copy this file to start a new area of coverage.
/// ===========================================================================================
///
/// It shows the five things almost every test in this suite does:
///
///   1. get a client            await Client(ShopService.X)  — add a ShopUser for authenticated calls
///   2. build unique data       Unique(...) / UniqueEmail() / UniqueBarCode()
///   3. call and assert         response.ShouldBeCreated(), .As&lt;T&gt;(), .ShouldBeProblem(...)
///   4. register cleanup        Cleanup("what", () => ...)   — runs in reverse at teardown
///   5. read back to verify     never trust the write response alone for anything that persisted
///
/// Products are the simplest surface to demonstrate on: no authentication, plain int ids, and
/// the same paths work direct (:5001) and through the gateway (:5000).
///
/// Two habits worth keeping:
///
///   * Never assert an absolute row count. The application's seeders only run against an empty
///     table, so after the first execution the database holds both the seed data and earlier
///     runs' leftovers. Assert on data this test created.
///
///   * Give every test its own data. BarCode, QrCode and Email are uniqueness-checked
///     server-side and answer 409, so re-running a suite that reuses fixed values fails.
/// </summary>
[TestFixture]
[Category("products")]
public class ExampleTests : ApiTest
{
    [Test]
    public async Task Create_ThenReadBack_RoundTripsEveryField()
    {
        // 1. a client — anonymous, because Product.Api has no authentication
        var api = await Client(ShopService.Product);

        // 2. unique data, so this passes on the hundredth run as well as the first
        var request = new CreateProductRequest
        {
            ProductName = Unique("AutoProduct"),
            BarCode = UniqueBarCode(),
            Description = "Created by the automation suite.",
            Quantity = 42,
            Unit = "box",
            Price = 12.34m,
            Discount = 7m,
        };

        // 3. call and assert
        var response = await api.PostAsync("/api/products", request);

        response.ShouldBeCreated();
        response.ShouldHaveLocationHeader();

        var created = response.As<ProductResponse>();

        // 4. register cleanup immediately after creating — before any assertion that could throw
        Cleanup($"product {created.ProductId}", () => api.DeleteAsync($"/api/products/{created.ProductId}"));

        created.ProductId.ShouldBeGreaterThan(0);
        created.ProductName.ShouldBe(request.ProductName);
        created.Quantity.ShouldBe(42);
        created.Price.ShouldBe(12.34m);
        created.UpdatedDate.ShouldBeNull("A freshly created product has never been updated.");

        // 5. read back — the POST response and what actually persisted are two different claims
        var reloaded = (await api.GetAsync($"/api/products/{created.ProductId}")).ShouldBeOk().As<ProductResponse>();

        reloaded.ProductName.ShouldBe(request.ProductName);
        reloaded.BarCode.ShouldBe(request.BarCode);
        reloaded.Unit.ShouldBe("box");
    }

    [Test]
    public async Task GetById_UnknownId_Returns404WithAProblemDocument()
    {
        // How to assert an error: ShouldBeProblem checks the status, that the body parses as an
        // RFC 7807 document agreeing with that status, and the title — then hands you the parsed
        // document for anything more specific.
        //
        // It does NOT check the media type by default: the application currently serves these as
        // application/json. See the note on ShouldBeProblem for why and how to fix it.
        var api = await Client(ShopService.Product);

        var response = await api.GetAsync("/api/products/2147483647");

        var problem = response.ShouldBeProblem(404, "Resource not found");
        problem.Instance.ShouldBe("/api/products/2147483647");
    }

    [Test]
    public async Task Create_WithoutAName_IsAValidationError()
    {
        // Validation 400s are a different shape from the 404 above: they come from
        // [ApiController] model validation and carry an "errors" dictionary.
        var api = await Client(ShopService.Product);

        var response = await api.PostAsync("/api/products", new CreateProductRequest
        {
            ProductName = string.Empty,
            Quantity = 1,
            Price = 1m,
        });

        response.ShouldBeValidationError("ProductName");
    }

    [Test]
    public async Task List_UsesQueryParametersAndReturnsThePagingEnvelope()
    {
        // Query strings are written as an anonymous object; null properties are simply omitted.
        var api = await Client(ShopService.Product);

        var response = await api.GetAsync("/api/products", new { page = 1, pageSize = 5, search = (string?)null });

        response.ShouldBeOk();
        var page = response.As<PagedResult<ProductResponse>>();

        page.Page.ShouldBe(1);
        page.PageSize.ShouldBe(5);
        page.Items.Count.ShouldBeLessThanOrEqualTo(5);
        page.TotalPages.ShouldBeGreaterThanOrEqualTo(1);
    }
}
