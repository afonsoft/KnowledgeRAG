using System.Net;
using System.Text;
using KnowledgeHub.Shared;

namespace KnowledgeHub.Tests.Unit.Shared;

// EnsureSuccessOrApiErrorAsync surfaces the server's error body so toasts show
// "agent requires a chat provider (Chat:Provider)" instead of the raw HTTP reason.
public class HttpResponseMessageExtensionsTests
{
    private static HttpResponseMessage Response(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    [Fact]
    public async Task SuccessResponse_DoesNotThrow()
    {
        using var response = Response(HttpStatusCode.OK, "{}");
        await response.EnsureSuccessOrApiErrorAsync();
    }

    [Fact]
    public async Task ErrorBody_SurfacesServerMessage()
    {
        using var response = Response(HttpStatusCode.BadRequest,
            """{"error":"agent requires a chat provider (Chat:Provider)"}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => response.EnsureSuccessOrApiErrorAsync());

        Assert.Equal("agent requires a chat provider (Chat:Provider)", ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task ProblemDetails_SurfacesDetail()
    {
        using var response = Response(HttpStatusCode.BadRequest,
            """{"type":"about:blank","title":"One or more validation errors occurred.","status":400,"detail":"content is required"}""",
            "application/problem+json");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => response.EnsureSuccessOrApiErrorAsync());

        Assert.Equal("content is required", ex.Message);
    }

    [Fact]
    public async Task ProblemDetailsWithoutDetail_SurfacesTitle()
    {
        using var response = Response(HttpStatusCode.NotFound,
            """{"type":"about:blank","title":"Resource was not found.","status":404}""",
            "application/problem+json");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => response.EnsureSuccessOrApiErrorAsync());

        Assert.Equal("Resource was not found.", ex.Message);
    }

    [Fact]
    public async Task NonJsonBody_FallsBackToStatusLine()
    {
        using var response = Response(HttpStatusCode.InternalServerError, "boom", "text/plain");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => response.EnsureSuccessOrApiErrorAsync());

        Assert.Contains("500", ex.Message);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }
}
