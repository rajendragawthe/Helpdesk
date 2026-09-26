using System.Net;
using System.Text;
using System.Text.Json;
using Helpdesk.Infrastructure.Ai;

namespace Helpdesk.Infrastructure.Tests.Ai;

public class OpenRouterAiServiceTests
{
    private static readonly OpenRouterOptions Options = new("sk-test-key", "test/model");

    private sealed class StubHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string CompletionWith(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    private static (OpenRouterAiService Service, StubHandler Handler) Create(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        return (new OpenRouterAiService(client, Options), handler);
    }

    [Fact]
    public async Task ClassifyAsync_ValidResponse_ReturnsParsedResult()
    {
        var (service, _) = Create(HttpStatusCode.OK,
            CompletionWith("""{"category":"Billing","summary":"Customer was charged twice.","confidence":0.92}"""));

        var result = await service.ClassifyAsync("Charged twice", "I was charged twice");

        Assert.Equal("Billing", result.Category);
        Assert.Equal("Customer was charged twice.", result.Summary);
        Assert.Equal(0.92, result.Confidence);
    }

    [Fact]
    public async Task ClassifyAsync_SendsBearerKeyModelJsonModeAndTicketContent()
    {
        var (service, handler) = Create(HttpStatusCode.OK,
            CompletionWith("""{"category":"Other","summary":"s","confidence":0.5}"""));

        await service.ClassifyAsync("Subject line", "Body text");

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test-key", handler.Request.Headers.Authorization.Parameter);

        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.Equal("test/model", root.GetProperty("model").GetString());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("Technical Issue", messages[0].GetProperty("content").GetString());
        var userContent = messages[1].GetProperty("content").GetString();
        Assert.Contains("Subject line", userContent);
        Assert.Contains("Body text", userContent);
    }

    [Fact]
    public async Task ClassifyAsync_RequestCapsMaxTokensAndTreatsSystemPromptContentAsUntrusted()
    {
        var (service, handler) = Create(HttpStatusCode.OK,
            CompletionWith("""{"category":"Other","summary":"s","confidence":0.5}"""));

        await service.ClassifyAsync("s", "b");

        using var doc = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(300, doc.RootElement.GetProperty("max_tokens").GetInt32());
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Contains("untrusted", messages[0].GetProperty("content").GetString());
        Assert.Contains("<email_body>", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ClassifyAsync_OverlongSummary_IsTruncatedTo1000Characters()
    {
        var longSummary = new string('x', 5000);
        var (service, _) = Create(HttpStatusCode.OK,
            CompletionWith($$"""{"category":"Other","summary":"{{longSummary}}","confidence":0.5}"""));

        var result = await service.ClassifyAsync("s", "b");

        Assert.Equal(1000, result.Summary.Length);
    }

    [Fact]
    public void OpenRouterOptions_ToString_DoesNotLeakApiKey()
    {
        Assert.DoesNotContain("sk-test-key", Options.ToString());
    }

    [Fact]
    public async Task ClassifyAsync_UnknownCategory_NormalizedToOther()
    {
        var (service, _) = Create(HttpStatusCode.OK,
            CompletionWith("""{"category":"Refunds","summary":"s","confidence":0.5}"""));

        var result = await service.ClassifyAsync("s", "b");

        Assert.Equal("Other", result.Category);
    }

    [Theory]
    [InlineData("1.7", 1.0)]
    [InlineData("-0.3", 0.0)]
    public async Task ClassifyAsync_ConfidenceOutOfRange_IsClamped(string confidenceJson, double expected)
    {
        var (service, _) = Create(HttpStatusCode.OK,
            CompletionWith($$"""{"category":"Billing","summary":"s","confidence":{{confidenceJson}}}"""));

        var result = await service.ClassifyAsync("s", "b");

        Assert.Equal(expected, result.Confidence);
    }

    [Fact]
    public async Task ClassifyAsync_NonSuccessStatus_Throws()
    {
        var (service, _) = Create(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => service.ClassifyAsync("s", "b"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"category":"Billing","confidence":0.5}""")]
    [InlineData("""{"category":"Billing","summary":"   ","confidence":0.5}""")]
    [InlineData("""{"category":"Billing","summary":"s"}""")]
    public async Task ClassifyAsync_UnusableModelContent_Throws(string content)
    {
        var (service, _) = Create(HttpStatusCode.OK, CompletionWith(content));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClassifyAsync("s", "b"));
    }

    [Fact]
    public async Task ClassifyAsync_NoChoices_Throws()
    {
        var (service, _) = Create(HttpStatusCode.OK, """{"choices":[]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClassifyAsync("s", "b"));
    }
}
