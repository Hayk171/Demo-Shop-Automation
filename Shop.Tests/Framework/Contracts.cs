namespace Shop.Tests.Framework;

/// <summary>
/// Mirrors Shop.Shared.Paging.PagedResult&lt;T&gt; — the envelope every list endpoint returns.
/// </summary>
public sealed class PagedResult<T>
{
    public List<T> Items { get; set; } = [];

    public long TotalCount { get; set; }

    /// <summary>1-based.</summary>
    public int Page { get; set; }

    /// <summary>Echoed back AFTER the server clamps it — never assume it equals what you asked for.</summary>
    public int PageSize { get; set; }

    /// <summary>Server-computed, at least 1.</summary>
    public int TotalPages { get; set; }
}

/// <summary>
/// RFC 7807 error body. Two producers in this system emit it:
///   - Shop.Shared.Middleware.ExceptionHandlingMiddleware: 404 "Resource not found",
///     409 "Conflict", 500 "An unexpected error occurred".
///   - ASP.NET Core [ApiController] model validation: 400 with a populated <see cref="Errors"/>.
/// </summary>
public sealed class ProblemDetails
{
    public string? Type { get; set; }
    public string? Title { get; set; }
    public int? Status { get; set; }
    public string? Detail { get; set; }
    public string? Instance { get; set; }

    /// <summary>Populated only by model-validation 400s.</summary>
    public Dictionary<string, string[]>? Errors { get; set; }

    public bool HasErrorFor(string propertyName) =>
        Errors is not null && Errors.Keys.Any(k => string.Equals(k, propertyName, StringComparison.OrdinalIgnoreCase));

    public override string ToString() =>
        $"{Status} {Title}{(Detail is null ? "" : $" - {Detail}")}" +
        (Errors is null ? "" : " | " + string.Join("; ", Errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}")));
}
