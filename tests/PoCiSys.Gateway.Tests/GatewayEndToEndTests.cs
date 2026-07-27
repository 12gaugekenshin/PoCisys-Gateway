using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PoCiSys.Gateway;

namespace PoCI.Monitor.Tests;

public sealed class GatewayEndToEndTests
{
    [Fact]
    public async Task ProxiesDummyAiStreamAndPublishesMatchingReceipt()
    {
        var backendPort = FreePort();
        var gatewayPort = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{backendPort}/");
        listener.Start();
        var expectedResponse =
            "data: {\"model\":\"dummy-llama\",\"choices\":[{\"delta\":{\"content\":\"tested\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2}}\n\n" +
            "data: [DONE]\n\n";
        var backendTask = ServeOnce(listener, expectedResponse);

        using var gateway = StartGateway(gatewayPort, backendPort);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            await WaitUntilReady(client, gatewayPort);

            var chatPage = await client.GetStringAsync($"http://127.0.0.1:{gatewayPort}/");
            var adminPage = await client.GetStringAsync($"http://127.0.0.1:{gatewayPort}/admin");
            Assert.Contains("Private reference chat", chatPage);
            Assert.Contains("PoCiSys Gateway", adminPage);

            var requestBody = "{\"model\":\"dummy-llama\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"secret test\"}]}";
            using var response = await client.PostAsync(
                $"http://127.0.0.1:{gatewayPort}/v1/chat/completions",
                new StringContent(requestBody, Encoding.UTF8, "application/json"));
            var received = await response.Content.ReadAsStringAsync();
            await backendTask;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expectedResponse, received);
            Assert.True(response.Headers.TryGetValues("X-PoCiSys-Receipt-Id", out var receiptIds));
            var responseReceiptId = Assert.Single(receiptIds);
            var receiptsJson = await client.GetStringAsync($"http://127.0.0.1:{gatewayPort}/pocisys/api/receipts");
            Assert.DoesNotContain("secret test", receiptsJson);
            using var receipts = JsonDocument.Parse(receiptsJson);
            var receipt = receipts.RootElement[0];
            Assert.Equal(responseReceiptId, receipt.GetProperty("receipt_id").GetString());
            Assert.Equal("dummy-llama", receipt.GetProperty("model").GetString());
            Assert.Equal(5, receipt.GetProperty("input_tokens").GetInt64());
            Assert.Equal(2, receipt.GetProperty("output_tokens").GetInt64());
            Assert.Equal(requestBody.Length, receipt.GetProperty("request_bytes").GetInt64());
            Assert.Equal(expectedResponse.Length, receipt.GetProperty("response_bytes").GetInt64());
            Assert.Equal(64, receipt.GetProperty("request_sha256").GetString()!.Length);
            Assert.Equal(64, receipt.GetProperty("response_sha256").GetString()!.Length);
        }
        finally
        {
            if (!gateway.HasExited)
                gateway.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task ManagedConnectionCanBeInspectedAndTestedButNotRedirected()
    {
        var backendPort = FreePort();
        var gatewayPort = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{backendPort}/");
        listener.Start();
        var backendTask = ServeModelList(listener);
        using var gateway = StartGateway(gatewayPort, backendPort, connectionManaged: true);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            await WaitUntilReady(client, gatewayPort);

            using var connection = await client.GetAsync($"http://127.0.0.1:{gatewayPort}/pocisys/api/connection");
            using var connectionJson = JsonDocument.Parse(await connection.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, connection.StatusCode);
            Assert.True(connectionJson.RootElement.GetProperty("managed").GetBoolean());

            using var sameUpdate = await client.PutAsync(
                $"http://127.0.0.1:{gatewayPort}/pocisys/api/connection",
                new StringContent($"{{\"baseUrl\":\"http://127.0.0.1:{backendPort}\",\"provider\":\"auto\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, sameUpdate.StatusCode);

            using var update = await client.PutAsync(
                $"http://127.0.0.1:{gatewayPort}/pocisys/api/connection",
                new StringContent("{\"baseUrl\":\"http://attacker.invalid:11434\",\"provider\":\"ollama\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);

            using var probe = await client.PostAsync(
                $"http://127.0.0.1:{gatewayPort}/pocisys/api/connection/test", null);
            Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
            using var probeJson = JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
            Assert.True(probeJson.RootElement.GetProperty("connected").GetBoolean());
            await backendTask;
        }
        finally
        {
            if (!gateway.HasExited)
                gateway.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task DiscoversDummyModelThenRoutesReferenceChatTraffic()
    {
        var backendPort = FreePort();
        var gatewayPort = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{backendPort}/");
        listener.Start();
        var backendTask = ServeModelListAndChat(listener);
        using var gateway = StartGateway(gatewayPort, backendPort);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            await WaitUntilReady(client, gatewayPort);

            using var probeResponse = await client.PostAsync(
                $"http://127.0.0.1:{gatewayPort}/pocisys/api/connection/test", null);
            var probeJson = await probeResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);
            using var probe = JsonDocument.Parse(probeJson);
            Assert.True(probe.RootElement.GetProperty("connected").GetBoolean());
            Assert.Equal("ollama", probe.RootElement.GetProperty("provider").GetString());
            Assert.Equal("dummy-chat-model", probe.RootElement.GetProperty("models")[0].GetString());

            var chatBody = "{\"model\":\"dummy-chat-model\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"private hello\"}]}";
            using var chatRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"http://127.0.0.1:{gatewayPort}/api/chat")
            {
                Content = new StringContent(chatBody, Encoding.UTF8, "application/json"),
            };
            chatRequest.Headers.Add("X-PoCiSys-Session", "browser-session-secret");
            using var chatResponse = await client.SendAsync(chatRequest);
            var streamed = await chatResponse.Content.ReadAsStringAsync();
            await backendTask;

            Assert.Equal(HttpStatusCode.OK, chatResponse.StatusCode);
            Assert.Contains("hello through ", streamed);
            Assert.Contains("gateway", streamed);
            var receiptsJson = await client.GetStringAsync($"http://127.0.0.1:{gatewayPort}/pocisys/api/receipts");
            Assert.DoesNotContain("private hello", receiptsJson);
            Assert.DoesNotContain("browser-session-secret", receiptsJson);
            using var receipts = JsonDocument.Parse(receiptsJson);
            Assert.Equal("dummy-chat-model", receipts.RootElement[0].GetProperty("model").GetString());
            Assert.Equal(24, receipts.RootElement[0].GetProperty("session_hash").GetString()!.Length);
        }
        finally
        {
            if (!gateway.HasExited)
                gateway.Kill(entireProcessTree: true);
        }
    }

    private static async Task ServeOnce(HttpListener listener, string responseText)
    {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        _ = await reader.ReadToEndAsync();
        var bytes = Encoding.UTF8.GetBytes(responseText);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        for (var offset = 0; offset < bytes.Length; offset += 17)
        {
            var count = Math.Min(17, bytes.Length - offset);
            await context.Response.OutputStream.WriteAsync(bytes.AsMemory(offset, count));
            await context.Response.OutputStream.FlushAsync();
        }
        context.Response.Close();
    }

    private static async Task ServeModelListAndChat(HttpListener listener)
    {
        var list = await listener.GetContextAsync();
        Assert.Equal("/api/tags", list.Request.Url!.AbsolutePath);
        var models = Encoding.UTF8.GetBytes("{\"models\":[{\"name\":\"dummy-chat-model\"}]}");
        list.Response.StatusCode = 200;
        list.Response.ContentType = "application/json";
        await list.Response.OutputStream.WriteAsync(models);
        list.Response.Close();

        var chat = await listener.GetContextAsync();
        Assert.Equal("/api/chat", chat.Request.Url!.AbsolutePath);
        using (var reader = new StreamReader(chat.Request.InputStream, chat.Request.ContentEncoding))
            Assert.Contains("private hello", await reader.ReadToEndAsync());
        var chunks = new[]
        {
            "{\"model\":\"dummy-chat-model\",\"message\":{\"content\":\"hello through \"},\"done\":false}\n",
            "{\"model\":\"dummy-chat-model\",\"message\":{\"content\":\"gateway\"},\"done\":false}\n",
            "{\"model\":\"dummy-chat-model\",\"done\":true,\"prompt_eval_count\":7,\"eval_count\":3,\"eval_duration\":300000000}\n",
        };
        chat.Response.StatusCode = 200;
        chat.Response.ContentType = "application/x-ndjson";
        foreach (var chunk in chunks)
        {
            var bytes = Encoding.UTF8.GetBytes(chunk);
            await chat.Response.OutputStream.WriteAsync(bytes);
            await chat.Response.OutputStream.FlushAsync();
        }
        chat.Response.Close();
    }

    private static async Task ServeModelList(HttpListener listener)
    {
        var list = await listener.GetContextAsync();
        Assert.Equal("/api/tags", list.Request.Url!.AbsolutePath);
        var models = Encoding.UTF8.GetBytes("{\"models\":[{\"name\":\"managed-model\"}]}");
        list.Response.StatusCode = 200;
        list.Response.ContentType = "application/json";
        await list.Response.OutputStream.WriteAsync(models);
        list.Response.Close();
    }

    private static Process StartGateway(int gatewayPort, int backendPort, bool connectionManaged = false)
    {
        var assembly = typeof(GatewayOptions).Assembly.Location;
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            Arguments = $"\"{assembly}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["Gateway__ListenUrl"] = $"http://127.0.0.1:{gatewayPort}";
        start.Environment["Gateway__BackendBaseUrl"] = $"http://127.0.0.1:{backendPort}";
        start.Environment["Gateway__ConnectionManaged"] = connectionManaged.ToString();
        start.Environment["POCISYS_GATEWAY_DATA_DIR"] = Path.Combine(
            Path.GetTempPath(), "pocisys-gateway-e2e-" + Guid.NewGuid().ToString("N"));
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to launch gateway fixture.");
    }

    private static async Task WaitUntilReady(HttpClient client, int port)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/pocisys/api/health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Gateway did not become ready.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
