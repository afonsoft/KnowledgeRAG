using System.Net;
using System.Text;
using KnowledgeHub.Server.Ingestion.Connectors;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260919-notion-connector RF-002: pagination, throttle,
// Retry-After on 429, error mapping, auth headers.
[Collection("SearchTelemetry")]
public class NotionApiClientTests
{
    private sealed record SentRequest(
        HttpMethod Method, Uri Uri, string? Authorization, string? NotionVersion, string? Body);

    private static NotionApiClient Sut(Queue<HttpResponseMessage> responses, List<SentRequest> sent)
    {
        var handler = new QueueHandler(responses, sent);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.test") };
        // Throttle disabled — timing is a prod concern, behavior is under test.
        return new NotionApiClient(http, "ntn_test-token", "2022-06-28", throttle: TimeSpan.Zero);
    }

    [Fact]
    public async Task Probe_SendsAuthAndVersionHeaders()
    {
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(Json(HttpStatusCode.OK, """{"object":"user","type":"bot"}"""));
        var sent = new List<SentRequest>();

        await Sut(responses, sent).ProbeAsync(CancellationToken.None);

        var request = Assert.Single(sent);
        Assert.Equal("/v1/users/me", request.Uri.PathAndQuery);
        Assert.Equal("Bearer ntn_test-token", request.Authorization);
        Assert.Equal("2022-06-28", request.NotionVersion);
    }

    [Fact]
    public async Task Search_PaginatesUntilHasMoreFalse()
    {
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(Json(HttpStatusCode.OK,
            """{"results":[{"id":"p1","object":"page"}],"has_more":true,"next_cursor":"cur-1"}"""));
        responses.Enqueue(Json(HttpStatusCode.OK,
            """{"results":[{"id":"p2","object":"page"}],"has_more":false,"next_cursor":null}"""));
        var sent = new List<SentRequest>();

        var items = await Sut(responses, sent)
            .SearchAsync(CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, items.Count);
        Assert.Equal(2, sent.Count);
        Assert.Contains("\"start_cursor\":\"cur-1\"", sent[1].Body);
        Assert.Contains("\"page_size\":100", sent[1].Body);
    }

    [Fact]
    public async Task BlockChildren_PaginatesWithCursor()
    {
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(Json(HttpStatusCode.OK,
            """{"results":[{"id":"b1","type":"paragraph"}],"has_more":true,"next_cursor":"bc-1"}"""));
        responses.Enqueue(Json(HttpStatusCode.OK,
            """{"results":[{"id":"b2","type":"divider"}],"has_more":false,"next_cursor":null}"""));
        var sent = new List<SentRequest>();

        var items = await Sut(responses, sent)
            .GetBlockChildrenAsync("page-1", CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, items.Count);
        Assert.Equal("/v1/blocks/page-1/children", sent[0].Uri.AbsolutePath);
        Assert.Contains("start_cursor=bc-1", sent[1].Uri.Query);
        Assert.Contains("page_size=100", sent[0].Uri.Query);
    }

    [Fact]
    public async Task DatabaseQuery_PaginatesRows()
    {
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(Json(HttpStatusCode.OK,
            """{"results":[{"id":"r1","object":"page"}],"has_more":false,"next_cursor":null}"""));
        var sent = new List<SentRequest>();

        var items = await Sut(responses, sent)
            .QueryDatabaseAsync("db-1", CancellationToken.None)
            .ToListAsync();

        Assert.Single(items);
        Assert.Equal("/v1/databases/db-1/query", sent[0].Uri.AbsolutePath);
        Assert.Equal(HttpMethod.Post, sent[0].Method);
    }

    [Fact]
    public async Task RateLimited_HonorsRetryAfterThenSucceeds()
    {
        var responses = new Queue<HttpResponseMessage>();
        var limited = new HttpResponseMessage((HttpStatusCode)429);
        limited.Headers.Add("Retry-After", "0");
        responses.Enqueue(limited);
        responses.Enqueue(Json(HttpStatusCode.OK, """{"object":"user"}"""));

        await Sut(responses, new List<SentRequest>()).ProbeAsync(CancellationToken.None);

        Assert.Empty(responses);
    }

    [Fact]
    public async Task RateLimited_ExhaustsRetries_Throws()
    {
        var responses = new Queue<HttpResponseMessage>();
        for (var i = 0; i < 4; i++)
        {
            var limited = new HttpResponseMessage((HttpStatusCode)429);
            limited.Headers.Add("Retry-After", "0");
            responses.Enqueue(limited);
        }

        var ex = await Assert.ThrowsAsync<NotionApiException>(
            () => Sut(responses, new List<SentRequest>()).ProbeAsync(CancellationToken.None));

        Assert.Equal((HttpStatusCode)429, ex.StatusCode);
        Assert.Empty(responses);
    }

    [Fact]
    public async Task Unauthorized_ThrowsNotionApiException()
    {
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(Json(HttpStatusCode.Unauthorized,
            """{"object":"error","status":401,"code":"unauthorized","message":"API token is invalid."}"""));

        var ex = await Assert.ThrowsAsync<NotionApiException>(
            () => Sut(responses, new List<SentRequest>()).ProbeAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("unauthorized", ex.Message);
    }

    [Fact]
    public async Task ItemNotFound_ThrowsNotionApiException()
    {
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(Json(HttpStatusCode.NotFound,
            """{"object":"error","status":404,"code":"object_not_found","message":"not shared"}"""));

        var ex = await Assert.ThrowsAsync<NotionApiException>(async () =>
        {
            await foreach (var _ in Sut(responses, new List<SentRequest>())
                .GetBlockChildrenAsync("missing", CancellationToken.None))
            {
                throw new InvalidOperationException("expected NotFound before any item");
            }
        });

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task ServerError_ThrowsNotionApiException()
    {
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(Json(HttpStatusCode.InternalServerError, "{}"));

        await Assert.ThrowsAsync<NotionApiException>(
            () => Sut(responses, new List<SentRequest>()).ProbeAsync(CancellationToken.None));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class QueueHandler(
        Queue<HttpResponseMessage> responses,
        List<SentRequest> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Buffer the body before the request is disposed by the caller.
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            sent.Add(new SentRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Notion-Version", out var v) ? v.Single() : null,
                body));
            return responses.Count > 0
                ? responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }
}
