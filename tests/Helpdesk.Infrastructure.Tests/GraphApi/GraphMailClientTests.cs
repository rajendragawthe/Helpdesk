using System.Net;
using System.Text;
using System.Text.Json;
using Helpdesk.Infrastructure.GraphApi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;

namespace Helpdesk.Infrastructure.Tests.GraphApi;

public class GraphMailClientTests
{
    private const string Mailbox = "support@contoso.com";

    private sealed class CapturingHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (GraphMailClient Client, CapturingHandler Handler) Create(HttpStatusCode status, string body = "")
    {
        var handler = new CapturingHandler(status, body);
        var graph = new GraphServiceClient(new HttpClient(handler));
        var options = new GraphApiOptions("tenant", "client", "secret", Mailbox);
        return (new GraphMailClient(graph, options, NullLogger<GraphMailClient>.Instance), handler);
    }

    [Fact]
    public async Task SendReplyAsync_PostsAReplyWithTheTextToTheMailboxMessage()
    {
        var (client, handler) = Create(HttpStatusCode.Accepted);

        await client.SendReplyAsync("AAMkAD-123", "Hello,\nWe are on it.");

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        var path = Uri.UnescapeDataString(handler.Request.RequestUri!.AbsolutePath);
        Assert.EndsWith($"/users/{Mailbox}/messages/AAMkAD-123/reply", path);

        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var comment = doc.RootElement.EnumerateObject()
            .Single(p => string.Equals(p.Name, "comment", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Hello,\nWe are on it.", comment.Value.GetString());
    }

    [Fact]
    public async Task SendReplyAsync_GraphErrorResponse_Throws()
    {
        var (client, _) = Create(HttpStatusCode.TooManyRequests,
            """{"error":{"code":"ApplicationThrottled","message":"slow down"}}""");

        await Assert.ThrowsAnyAsync<Exception>(() => client.SendReplyAsync("AAMkAD-123", "Hi"));
    }
}
