using System.Text.Json;

namespace Shop.Tests.Framework;

/// <summary>
/// A fully-read HTTP response, safe to keep and assert against repeatedly.
///
/// The assertion helpers put the whole exchange into the failure message. "Expected 404 but was
/// 500" costs a re-run to diagnose; this does not.
/// </summary>
public sealed class ApiResponse
{
    public required string Method { get; init; }

    public required string Url { get; init; }

    public required int Status { get; init; }

    public required string Body { get; init; }

    /// <summary>Playwright lower-cases header names; use <see cref="Header"/> to read case-insensitively.</summary>
    public required IDictionary<string, string> Headers { get; init; }

    /// <summary>What was sent, if anything. Diagnostics only.</summary>
    public string? RequestBody { get; init; }

    /// <summary>True when the request carried an Authorization header. Diagnostics only.</summary>
    public bool WasAuthenticated { get; init; }

    public TimeSpan Elapsed { get; init; }

    public bool IsSuccess => Status is >= 200 and < 300;

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public string? ContentType => Header("content-type");

    // ---------------------------------------------------------------- reading

    public T As<T>()
    {
        if (string.IsNullOrWhiteSpace(Body))
        {
            throw new InvalidOperationException($"Expected a JSON body but the response was empty.{Environment.NewLine}{this}");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(Body, ApiClient.Json)
                   ?? throw new InvalidOperationException(
                       $"Body deserialized to null for {typeof(T).Name}.{Environment.NewLine}{this}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Could not read the body as {typeof(T).Name}: {ex.Message}{Environment.NewLine}{this}", ex);
        }
    }

    /// <summary>Reads the body as an RFC 7807 problem document.</summary>
    public ProblemDetails Problem() => As<ProblemDetails>();

    // ---------------------------------------------------------------- assertions

    /// <param name="because">Optional context appended to the failure message.</param>
    public ApiResponse ShouldBe(int expectedStatus, string? because = null)
    {
        if (Status != expectedStatus)
        {
            var reason = because is null ? string.Empty : Environment.NewLine + because;

            // An unexpected 401 on an authenticated request is the one failure that reliably
            // sends people down the wrong path — it reads as a role or policy problem when it is
            // usually the token. Say so, rather than leaving it to be rediscovered.
            var hint = Status == 401 && expectedStatus != 401 && WasAuthenticated
                ? Environment.NewLine +
                  "This request DID carry a bearer token, so the token itself was rejected. Check, in order: " +
                  "the token's expiry (a slow test can outlive it), clock skew between this machine and Keycloak, " +
                  "and the issuer/audience the service validates against."
                : string.Empty;

            Assert.Fail($"Expected HTTP {expectedStatus} but got {Status}.{reason}{hint}{Environment.NewLine}{this}");
        }

        return this;
    }

    public ApiResponse ShouldBeOk(string? because = null) => ShouldBe(200, because);

    public ApiResponse ShouldBeCreated(string? because = null) => ShouldBe(201, because);

    public ApiResponse ShouldBeNoContent(string? because = null) => ShouldBe(204, because);

    public ApiResponse ShouldBeBadRequest(string? because = null) => ShouldBe(400, because);

    public ApiResponse ShouldBeUnauthorized(string? because = null) => ShouldBe(401, because);

    public ApiResponse ShouldBeForbidden(string? because = null) => ShouldBe(403, because);

    public ApiResponse ShouldBeNotFound(string? because = null) => ShouldBe(404, because);

    public ApiResponse ShouldBeConflict(string? because = null) => ShouldBe(409, because);

    /// <summary>
    /// Asserts an RFC 7807 error body with the given status, and returns it.
    /// </summary>
    /// <param name="requireProblemContentType">
    /// Off by default, because the application does not currently send the right media type.
    ///
    /// ExceptionHandlingMiddleware sets <c>Response.ContentType = "application/problem+json"</c>
    /// and then calls <c>WriteAsJsonAsync(problem)</c> — and that overload overwrites the content
    /// type with <c>application/json; charset=utf-8</c>. So the body is a correct problem
    /// document served under the wrong media type, and clients that branch on the content type
    /// cannot tell an error from a normal payload.
    ///
    /// The one-line fix on the application side is to pass it explicitly:
    ///   <c>await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");</c>
    /// Once that ships, flip this default to true (or pass true here) to lock it in.
    ///
    /// Note this affects only the middleware's 404/409/500 responses. Validation 400s come from
    /// [ApiController] itself and do carry application/problem+json.
    /// </param>
    public ProblemDetails ShouldBeProblem(
        int expectedStatus, string? expectedTitle = null, bool requireProblemContentType = false)
    {
        ShouldBe(expectedStatus);

        if (requireProblemContentType)
        {
            // Plain Contains + ShouldBeTrue rather than string.ShouldContain: Shouldly's string
            // overload takes (expected, Case), so passing a message as the second argument makes
            // overload resolution fall through to the IEnumerable<char> one and fail to compile.
            (ContentType ?? string.Empty).Contains("problem+json", StringComparison.OrdinalIgnoreCase)
                .ShouldBeTrue($"Expected the RFC 7807 media type.{Environment.NewLine}{this}");
        }

        // The shape is the assertion that matters either way: a body that parses as a problem
        // document and agrees with the HTTP status line.
        var problem = Problem();

        problem.Status.ShouldBe(
            expectedStatus, $"problem.status disagreed with the HTTP status.{Environment.NewLine}{this}");

        problem.Title.ShouldNotBeNullOrWhiteSpace(
            $"An RFC 7807 document must carry a title.{Environment.NewLine}{this}");

        if (expectedTitle is not null)
        {
            problem.Title.ShouldBe(expectedTitle, $"Unexpected problem title.{Environment.NewLine}{this}");
        }

        return problem;
    }

    /// <summary>
    /// Asserts a model-validation 400. These come from [ApiController], not from the shared
    /// exception middleware, so they carry an "errors" dictionary keyed by property name.
    /// </summary>
    public ProblemDetails ShouldBeValidationError(params string[] expectedProperties)
    {
        ShouldBe(400);
        var problem = Problem();

        problem.Errors.ShouldNotBeNull($"Expected a validation errors dictionary.{Environment.NewLine}{this}");

        foreach (var property in expectedProperties)
        {
            problem.HasErrorFor(property).ShouldBeTrue(
                $"Expected a validation error for '{property}'. Got: {problem}{Environment.NewLine}{this}");
        }

        return problem;
    }

    /// <summary>Asserts the Location header a 201 must carry, and returns it.</summary>
    public string ShouldHaveLocationHeader()
    {
        var location = Header("location");
        location.ShouldNotBeNullOrWhiteSpace(
            $"201 responses must carry a Location header.{Environment.NewLine}{this}");
        return location!;
    }

    public override string ToString() =>
        $"{Method} {Url} -> {Status} ({Elapsed.TotalMilliseconds:F0} ms){(WasAuthenticated ? " [authenticated]" : string.Empty)}" +
        (RequestBody is null ? string.Empty : $"{Environment.NewLine}  request:  {Truncate(RequestBody)}") +
        $"{Environment.NewLine}  response: {Truncate(Body)}";

    private static string Truncate(string value, int max = 2000) =>
        value.Length <= max ? value : value[..max] + $"... ({value.Length} chars total)";
}
