using System.Net;
using System.Text;
using System.Text.Json;

namespace Cabinet.Ai.Tests;

public class OllamaClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("http://192.168.1.10:11434/")]
    [InlineData("https://api.exemple.com/")]
    [InlineData("http://ollama.cabinet.local:11434/")]
    public void Constructor_RefusesAnyMachineButThisOne(string endpoint)
        => Assert.Throws<ArgumentException>(() => new OllamaClient("gemma3:12b", new Uri(endpoint)));

    [Theory]
    [InlineData("http://127.0.0.1:11434/")]
    [InlineData("http://localhost:11434/")]
    [InlineData("http://[::1]:11434/")]
    public void Constructor_AcceptsThisMachine(string endpoint)
    {
        using var client = new OllamaClient("gemma3:12b", new Uri(endpoint));
        Assert.True(client.Endpoint.IsLoopback);
    }

    [Fact]
    public async Task Complete_SendsChatRequestAndReturnsContent()
    {
        var handler = new FakeHandler((_, _) => Json("""{"message":{"role":"assistant","content":"{\"resume\":\"R\"}"},"done":true}"""));
        using var client = new OllamaClient("gemma3:12b", handler: handler);
        var answer = await client.CompleteAsync("consignes", "e-mail", CancellationToken.None);

        Assert.Equal("{\"resume\":\"R\"}", answer);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:11434/api/chat", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("gemma3:12b", json.RootElement.GetProperty("model").GetString());
        Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("json", json.RootElement.GetProperty("format").GetString());
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("consignes", messages[0].GetProperty("content").GetString());
        Assert.Equal("e-mail", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Complete_ServerError_Throws()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"model not found"}""") });
        using var client = new OllamaClient("absent", handler: handler);
        await Assert.ThrowsAsync<OllamaException>(() => client.CompleteAsync("s", "u", CancellationToken.None));
    }

    [Fact]
    public async Task Status_ListsInstalledModels()
    {
        var handler = new FakeHandler((_, _) => Json("""{"models":[{"name":"gemma3:12b"},{"name":"mistral-small3.2:latest"}]}"""));
        using var client = new OllamaClient("mistral-small3.2", handler: handler);
        var status = await client.GetStatusAsync(CancellationToken.None);
        Assert.True(status.Running);
        Assert.True(status.HasModel("mistral-small3.2"));
        Assert.True(status.HasModel("gemma3:12b"));
        Assert.False(status.HasModel("gemma3:4b"));
    }

    [Fact]
    public async Task Status_OllamaNotRunning_IsNotRunning()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("Connexion refusée"));
        using var client = new OllamaClient("gemma3:12b", handler: handler);
        Assert.False((await client.GetStatusAsync(CancellationToken.None)).Running);
    }

    [Fact]
    public async Task Pull_ReportsProgress()
    {
        var stream = "{\"status\":\"pulling\",\"total\":100,\"completed\":25}\n{\"status\":\"pulling\",\"total\":100,\"completed\":100}\n{\"status\":\"success\"}\n";
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream) });
        using var client = new OllamaClient("gemma3:12b", handler: handler);
        var values = new List<double>();
        await client.PullAsync(new SynchronousProgress(values.Add), CancellationToken.None);
        Assert.Equal(new[] { 0.25, 1.0 }, values);
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Theory]
    [InlineData(64L, "mistral-small3.2")]
    [InlineData(32L, "mistral-small3.2")]
    [InlineData(24L, "gemma3:12b")]
    [InlineData(16L, "gemma3:12b")]
    [InlineData(8L, "gemma3:4b")]
    public void RecommendedModel_DependsOnMemory(long gigabytes, string expected)
        => Assert.Equal(expected, OllamaClient.RecommendedModel(gigabytes * 1024 * 1024 * 1024));

    [Fact]
    public void AiAssembly_UsesHttpOnly()
    {
        var referenced = typeof(OllamaClient).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        foreach (var forbidden in new[] { "System.Net.Sockets", "System.Net.Requests", "System.Net.WebClient", "System.Net.WebSockets", "System.Net.Mail" })
        {
            Assert.DoesNotContain(forbidden, referenced);
        }
    }
}
