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
    [InlineData(64L, true, "mistral-small3.2")]
    [InlineData(32L, true, "mistral-small3.2")]
    [InlineData(32L, false, "gemma3:12b")]
    [InlineData(16L, true, "gemma3:12b")]
    [InlineData(16L, false, "gemma3:4b")]
    [InlineData(8L, true, "gemma3:4b")]
    public void RecommendedModel_DependsOnMemoryAndChip(long gigabytes, bool appleSilicon, string expected)
        => Assert.Equal(expected, OllamaClient.RecommendedModel(gigabytes * 1024 * 1024 * 1024, appleSilicon));

    [Fact]
    public void DefaultHandler_NeverFollowsRedirectsNorUsesAProxy()
    {
        using var handler = OllamaClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task Redirect_IsRefused()
    {
        var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("http://192.0.2.2/api/chat");
            return response;
        });
        using var client = new OllamaClient("gemma3:12b", handler: handler);
        await Assert.ThrowsAsync<OllamaException>(() => client.CompleteAsync("s", "u", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("gpt-oss:120b-cloud")]
    [InlineData("deepseek-v3.1:671b-cloud")]
    [InlineData("qwen3-coder:cloud")]
    public void CloudModels_AreRefused(string model)
        => Assert.Throws<ArgumentException>(() => new OllamaClient(model));

    [Fact]
    public void CloudModels_AreRefusedWhenChangedLater()
    {
        using var client = new OllamaClient("gemma3:12b");
        Assert.Throws<ArgumentException>(() => client.Model = "gpt-oss:20b-cloud");
    }

    [Fact]
    public async Task Status_RemoteModels_AreNotCountedAsInstalled()
    {
        var handler = new FakeHandler((_, _) => Json("{\"models\":[{\"name\":\"gemma3:12b\"},{\"name\":\"gpt-oss:120b-cloud\",\"remote_host\":\"https://ollama.com:443\"}]}"));
        using var client = new OllamaClient("gemma3:12b", handler: handler);
        var status = await client.GetStatusAsync(CancellationToken.None);
        Assert.Equal(new[] { "gemma3:12b" }, status.Models);
    }

    [Fact]
    public async Task Complete_AsksForStructuredOutputAndCapsLength()
    {
        var handler = new FakeHandler((_, _) => Json("{\"message\":{\"role\":\"assistant\",\"content\":\"{}\"},\"done\":true}"));
        using var client = new OllamaClient("gemma3:12b", handler: handler);
        await client.CompleteAsync("s", "u", CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Requests.Single().Body!);
        var format = json.RootElement.GetProperty("format");
        Assert.Equal(JsonValueKind.Object, format.ValueKind);
        Assert.Equal("object", format.GetProperty("type").GetString());
        Assert.True(json.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32() > 0);
        Assert.Equal("30m", json.RootElement.GetProperty("keep_alive").GetString());
    }

    [Fact]
    public void AiAssembly_UsesHttpOnly()
    {
        var referenced = typeof(OllamaClient).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        foreach (var forbidden in new[] { "System.Net.Requests", "System.Net.WebClient", "System.Net.WebSockets", "System.Net.Mail" })
        {
            Assert.DoesNotContain(forbidden, referenced);
        }
    }
}

public class OllamaTextGenerationTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":{\"role\":\"assistant\",\"content\":\"Synthèse\"},\"done\":true}"),
            };
        }
    }

    [Fact]
    public async Task Generate_FreeText_HasNoFormat()
    {
        var handler = new Capture();
        using var client = new OllamaClient("gemma3:12b", handler: handler);
        Assert.Equal("Synthèse", await client.GenerateAsync("s", "u", json: false, CancellationToken.None));
        using var json = System.Text.Json.JsonDocument.Parse(handler.Body!);
        Assert.False(json.RootElement.TryGetProperty("format", out _));
        Assert.True(json.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32() >= 2000);
    }

    [Fact]
    public async Task Generate_Json_AsksForJson()
    {
        var handler = new Capture();
        using var client = new OllamaClient("gemma3:12b", handler: handler);
        await client.GenerateAsync("s", "u", json: true, CancellationToken.None);
        using var json = System.Text.Json.JsonDocument.Parse(handler.Body!);
        Assert.Equal("json", json.RootElement.GetProperty("format").GetString());
    }
}
