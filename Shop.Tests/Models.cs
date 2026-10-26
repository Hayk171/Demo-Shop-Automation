namespace Shop.Tests;

// Only the models the current tests actually use. Add more here (or split this file per service
// once it grows) as you add tests. Property names match the services' DTOs; serialization is
// camelCase, configured once in ApiClient.Json.

/// <summary>
/// CustomerApi.Dtos.MeResponse — the caller's own identity, read straight off the JWT.
/// </summary>
public sealed class MeResponse
{
    public string Id { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Email { get; set; }
    public List<string> Roles { get; set; } = [];
}

/// <summary>
/// ProductApi.Dtos.CreateProductRequest.
/// Server validation: ProductName required 1-200, QrCode &lt;=300, BarCode &lt;=100,
/// Description &lt;=1000, Quantity &gt;=0, Unit required &lt;=20, Price &gt;=0, Discount 0-100.
/// </summary>
public sealed class CreateProductRequest
{
    public string ProductName { get; set; } = string.Empty;
    public string? QrCode { get; set; }
    public string? BarCode { get; set; }
    public string? Description { get; set; }
    public int Quantity { get; set; }
    public string Unit { get; set; } = "pcs";
    public decimal Price { get; set; }
    public decimal Discount { get; set; }
}

/// <summary>ProductApi.Dtos.ProductResponse.</summary>
public sealed class ProductResponse
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? QrCode { get; set; }
    public string? BarCode { get; set; }
    public string? Description { get; set; }
    public int Quantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Discount { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? UpdatedDate { get; set; }
}
