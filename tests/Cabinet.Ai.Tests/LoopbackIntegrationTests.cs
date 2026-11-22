using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cabinet.Ai.Tests;

/// <summary>Le vrai gestionnaire de connexion, contre un petit serveur HTTP sur 127.0.0.1.</summary>
public class LoopbackIntegrationTests
{
    private static (TcpListener Listener, Uri Endpoint, Func<int> Connections) Serve(Func<string, string> respond)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connections = 0;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref connections);
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[16384];
                    var read = await stream.ReadAsync(buffer);
                    var request = Encoding.UTF8.GetString(buffer, 0, read);
                    var response = respond(request.Split(' ').ElementAtOrDefault(1) ?? "/");
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
                }
            }
        });
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return (listener, new Uri($"http://127.0.0.1:{port}/"), () => connections);
    }

    private static string Http(int status, string reason, string body, string extraHeaders = "")
        => $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n{extraHeaders}\r\n{body}";

    [Fact]
    public async Task RealHandler_TalksToThisMachine()
    {
        var (listener, endpoint, _) = Serve(_ => Http(200, "OK", "{\"models\":[{\"name\":\"gemma3:12b\"}]}"));
        try
        {
            using var client = new OllamaClient("gemma3:12b", endpoint);
            var status = await client.GetStatusAsync(CancellationToken.None);
            Assert.True(status.Running);
            Assert.True(status.HasModel("gemma3:12b"));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RealHandler_DoesNotFollowARedirectElsewhere()
    {
        var (listener, endpoint, connections) = Serve(_ => Http(307, "Temporary Redirect", "", "Location: http://192.0.2.2/api/chat\r\n"));
        try
        {
            using var client = new OllamaClient("gemma3:12b", endpoint);
            await Assert.ThrowsAsync<OllamaException>(() => client.CompleteAsync("s", "contenu confidentiel", CancellationToken.None));
            Assert.Equal(1, connections());
        }
        finally
        {
            listener.Stop();
        }
    }
}
