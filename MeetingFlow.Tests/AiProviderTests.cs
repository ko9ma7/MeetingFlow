using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MeetingFlow.App.Models;
using MeetingFlow.App.Services;

namespace MeetingFlow.Tests;

public sealed class AiProviderTests
{
    [Fact]
    public async Task OpenAiCompatible_TestConnection_UsesChatCompletionsAndParsesText()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestBody = string.Empty;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var (headers, body) = await ReadHttpRequestAsync(stream);
            Assert.Contains("POST /v1/chat/completions", headers, StringComparison.Ordinal);
            requestBody = body;
            var responseBody = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "연결 성공" } } } });
            var responseBytes = Encoding.UTF8.GetBytes(responseBody);
            var responseHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders);
            await stream.WriteAsync(responseBytes);
        });

        try
        {
            var settings = new AppSettings
            {
                AiProvider = "compatible",
                CompatibleApiEndpoint = $"http://127.0.0.1:{port}/v1",
                Model = "local-test",
                GeminiConnectionTimeoutSeconds = 10
            };
            await new GeminiService().TestAsync(settings, string.Empty);
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("local-test", requestBody, StringComparison.Ordinal);
            Assert.DoesNotContain("response_format", requestBody, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<(string Headers, string Body)> ReadHttpRequestAsync(NetworkStream stream)
    {
        var received = new List<byte>();
        var buffer = new byte[1024];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var count = await stream.ReadAsync(buffer);
            if (count == 0) break;
            received.AddRange(buffer.AsSpan(0, count).ToArray());
            headerEnd = FindHeaderEnd(received);
        }
        var headers = Encoding.ASCII.GetString(received.Take(headerEnd).ToArray());
        var contentLengthLine = headers.Split("\r\n").First(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var contentLength = int.Parse(contentLengthLine.Split(':', 2)[1].Trim());
        var bodyStart = headerEnd + 4;
        while (received.Count - bodyStart < contentLength)
        {
            var count = await stream.ReadAsync(buffer);
            if (count == 0) break;
            received.AddRange(buffer.AsSpan(0, count).ToArray());
        }
        return (headers, Encoding.UTF8.GetString(received.Skip(bodyStart).Take(contentLength).ToArray()));
    }

    private static int FindHeaderEnd(IReadOnlyList<byte> bytes)
    {
        for (var i = 0; i <= bytes.Count - 4; i++)
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i;
        return -1;
    }
}
