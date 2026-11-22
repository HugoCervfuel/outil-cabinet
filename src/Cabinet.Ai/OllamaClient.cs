using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

    /// <summary>Format imposé à la réponse (sorties structurées d'Ollama) : les cinq clés, l'urgence parmi trois valeurs.</summary>
    private static readonly JsonElement AnalysisSchema = JsonDocument.Parse(
        """
        {"type":"object","properties":{
          "resume":{"type":"string"},
          "action":{"type":"string"},
          "urgence":{"type":"string","enum":["basse","normale","haute"]},
          "echeance":{"type":["string","null"]},
          "brouillon":{"type":"string"}},
         "required":["resume","action","urgence","echeance","brouillon"]}
        """).RootElement.Clone();

    private readonly HttpClient _http;
    private string _model = "";

    public OllamaClient(string model, Uri? endpoint = null, HttpMessageHandler? handler = null)
    {
        Endpoint = endpoint ?? DefaultEndpoint;
        if (!Endpoint.IsLoopback)
        {
            throw new ArgumentException("L'IA doit tourner sur cet ordinateur : seule l'adresse 127.0.0.1 est acceptée.", nameof(endpoint));
        }

        Model = model;
        _http = new HttpClient(handler ?? CreateHandler())
        {
            BaseAddress = Endpoint,
            // Un modèle local peut mettre plusieurs minutes à rédiger sur un ordinateur modeste.
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    public Uri Endpoint { get; }

    /// <summary>Le modèle utilisé. Les modèles « cloud » d'Ollama, exécutés chez Ollama, sont refusés.</summary>
    public string Model
    {
        get => _model;
        set
        {
            if (IsCloudModel(value))
            {
                throw new ArgumentException("Ce modèle s'exécute sur les serveurs d'Ollama, pas sur cet ordinateur : il est refusé.", nameof(value));
            }

            _model = value.Trim();
        }
    }

    public static bool IsAppleSilicon => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public static bool IsCloudModel(string model)
    {
        var name = model.Trim().ToLowerInvariant();
        return name.EndsWith("-cloud", StringComparison.Ordinal) || name.EndsWith(":cloud", StringComparison.Ordinal);
    }

    /// <summary>
    /// Modèle conseillé selon la mémoire et la puce. Un Mac à puce Apple fait tourner l'IA bien plus vite qu'un PC
    /// sans carte graphique : sur PC, on conseille un modèle plus léger pour une réponse en temps raisonnable.
    /// </summary>
    public static string RecommendedModel(long memoryBytes, bool appleSilicon)
    {
        var gigabytes = memoryBytes / (1024.0 * 1024 * 1024);
        if (appleSilicon)
        {
            return gigabytes >= 31 ? "mistral-small3.2" : gigabytes >= 15 ? "gemma3:12b" : "gemma3:4b";
        }

        return gigabytes >= 31 ? "gemma3:12b" : "gemma3:4b";
    }

    /// <summary>
    /// Connexion directe (aucun proxy), sans suivre de redirection, et refusée vers toute adresse qui n'est pas
    /// cette machine, même si un programme local tentait de rediriger la requête.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, cancellationToken);
            var loopback = addresses.Where(IPAddress.IsLoopback).ToArray();
            if (loopback.Length == 0 || loopback.Length != addresses.Length)
            {
                throw new HttpRequestException("Connexion refusée : l'IA doit tourner sur cet ordinateur.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(loopback, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

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
            var local = tags?.Models?
                .Where(m => string.IsNullOrEmpty(m.RemoteHost) && m.Name is { Length: > 0 } name && !IsCloudModel(name))
                .Select(m => m.Name!)
                .ToList();
            return new OllamaStatus(true, local ?? new List<string>());
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
            Format: AnalysisSchema,
            new ChatOptions(Temperature: 0.2, ContextLength: 8192, MaxTokens: 1200),
            KeepAlive: "30m");

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
        [property: JsonPropertyName("format")] JsonElement Format,
        [property: JsonPropertyName("options")] ChatOptions Options,
        [property: JsonPropertyName("keep_alive")] string KeepAlive);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatOptions(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("num_ctx")] int ContextLength,
        [property: JsonPropertyName("num_predict")] int MaxTokens);

    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record TagsResponse([property: JsonPropertyName("models")] IReadOnlyList<TagModel>? Models);

    private sealed record TagModel(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("remote_host")] string? RemoteHost);

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
