using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cabinet.Core;

namespace Cabinet.Ai;

/// <summary>Erreur renvoyée par Ollama (modèle absent, mémoire insuffisante…).</summary>
public sealed class OllamaException(string message) : Exception(message);

/// <summary>État de l'IA locale : Ollama ouvert ou non, modèles déjà téléchargés.</summary>
public sealed record OllamaStatus(bool Running, IReadOnlyList<string> Models)
{
    public bool HasModel(string model)
    {
        static string Full(string name) => name.Contains(':') ? name : name + ":latest";
        return Models.Any(m => string.Equals(Full(m), Full(model), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Client de l'IA locale Ollama. Il ne parle qu'à cet ordinateur (127.0.0.1) : toute autre adresse est refusée,
/// et aucun proxy n'est utilisé. Les e-mails analysés ne quittent donc jamais le poste.
/// </summary>
public sealed class OllamaClient : IMailAssistant, IDisposable
{
    public static readonly Uri DefaultEndpoint = new("http://127.0.0.1:11434/");

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;

    public OllamaClient(string model, Uri? endpoint = null, HttpMessageHandler? handler = null)
    {
        Endpoint = endpoint ?? DefaultEndpoint;
        if (!Endpoint.IsLoopback)
        {
            throw new ArgumentException("L'IA doit tourner sur cet ordinateur : seule l'adresse 127.0.0.1 est acceptée.", nameof(endpoint));
        }

        Model = model;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = Endpoint,
            // Un modèle local peut mettre plusieurs minutes à rédiger sur un ordinateur modeste.
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    public Uri Endpoint { get; }

    public string Model { get; set; }

    /// <summary>Modèle conseillé selon la mémoire de l'ordinateur.</summary>
    public static string RecommendedModel(long memoryBytes)
    {
        var gigabytes = memoryBytes / (1024.0 * 1024 * 1024);
        return gigabytes >= 31 ? "mistral-small3.2" : gigabytes >= 15 ? "gemma3:12b" : "gemma3:4b";
    }

    public async Task<OllamaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("api/tags", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new OllamaStatus(false, Array.Empty<string>());
            }

            var tags = await response.Content.ReadFromJsonAsync<TagsResponse>(Json, cancellationToken);
            return new OllamaStatus(true, tags?.Models?.Select(m => m.Name ?? "").Where(n => n.Length > 0).ToList() ?? new List<string>());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new OllamaStatus(false, Array.Empty<string>());
        }
    }

    public async Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
    {
        var request = new ChatRequest(
            Model,
            new[] { new ChatMessage("system", system), new ChatMessage("user", user) },
            Stream: false,
            Format: "json",
            new ChatOptions(Temperature: 0.2, ContextLength: 8192));

        using var response = await _http.PostAsJsonAsync("api/chat", request, Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new OllamaException(await ReadErrorAsync(response, cancellationToken));
        }

        var chat = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, cancellationToken);
        return chat?.Message?.Content ?? throw new OllamaException("Réponse vide de l'IA locale.");
    }

    /// <summary>Télécharge le modèle (une seule fois). Seul le modèle transite, aucune donnée du cabinet.</summary>
    public async Task PullAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/pull")
        {
            Content = JsonContent.Create(new PullRequest(Model, Stream: true), options: Json),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new OllamaException(await ReadErrorAsync(response, cancellationToken));
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var update = JsonSerializer.Deserialize<PullUpdate>(line, Json);
            if (update?.Error is { } error)
            {
                throw new OllamaException(error);
            }

            if (update is { Total: > 0, Completed: { } completed })
            {
                progress?.Report(Math.Clamp(completed / (double)update.Total.Value, 0, 1));
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, cancellationToken);
            return error?.Error ?? $"Erreur {(int)response.StatusCode} de l'IA locale.";
        }
        catch (JsonException)
        {
            return $"Erreur {(int)response.StatusCode} de l'IA locale.";
        }
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("options")] ChatOptions Options);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatOptions(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("num_ctx")] int ContextLength);

    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record TagsResponse([property: JsonPropertyName("models")] IReadOnlyList<TagModel>? Models);

    private sealed record TagModel([property: JsonPropertyName("name")] string? Name);

    private sealed record PullRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record PullUpdate(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("total")] long? Total,
        [property: JsonPropertyName("completed")] long? Completed,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record ErrorResponse([property: JsonPropertyName("error")] string? Error);
}
