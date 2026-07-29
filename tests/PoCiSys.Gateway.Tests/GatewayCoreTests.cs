using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PoCiSys.Gateway;

namespace PoCI.Monitor.Tests;

public sealed class GatewayCoreTests
{
    [Fact]
    public void ReadsFragmentedOpenAiStreamWithoutRetainingContent()
    {
        var inspector = new ProviderStreamInspector();
        var payload = Encoding.UTF8.GetBytes(
            "data: {\"model\":\"llama-test\",\"choices\":[{\"delta\":{\"content\":\"hello 🌎\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":4}}\n\n" +
            "data: [DONE]\n\n");

        for (var offset = 0; offset < payload.Length; offset += 3)
            inspector.Feed(payload.AsSpan(offset, Math.Min(3, payload.Length - offset)));
        inspector.Complete();

        Assert.True(inspector.SawOutput);
        Assert.Equal("llama-test", inspector.Model);
        Assert.Equal(12, inspector.InputTokens);
        Assert.Equal(4, inspector.OutputTokens);
        Assert.DoesNotContain("hello", JsonSerializer.Serialize(inspector));
    }

    [Fact]
    public void ReadsOllamaUsageAndProviderThroughput()
    {
        var inspector = new ProviderStreamInspector();
        inspector.Feed(Encoding.UTF8.GetBytes("{\"model\":\"qwen\",\"response\":\"hi\",\"done\":false}\n"));
        inspector.Feed(Encoding.UTF8.GetBytes(
            "{\"model\":\"qwen\",\"done\":true,\"prompt_eval_count\":8,\"eval_count\":20,\"eval_duration\":2000000000}\n"));

        Assert.True(inspector.SawOutput);
        Assert.Equal(8, inspector.InputTokens);
        Assert.Equal(20, inspector.OutputTokens);
        Assert.Equal(10, inspector.ProviderTokensPerSecond);
    }

    [Fact]
    public async Task HashingContentIsTransparentAndEnforcesLimit()
    {
        var sourceBytes = Encoding.UTF8.GetBytes("private prompt body");
        var headers = new HeaderDictionary { ["Content-Type"] = "application/json" };
        var content = new HashingHttpContent(new MemoryStream(sourceBytes), 1024, headers);
        await using var destination = new MemoryStream();

        await content.CopyToAsync(destination);

        Assert.Equal(sourceBytes, destination.ToArray());
        Assert.Equal(sourceBytes.Length, content.BytesRead);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(), content.Hash);

        var tooLarge = new HashingHttpContent(new MemoryStream(sourceBytes), 3, headers);
        await Assert.ThrowsAsync<InvalidDataException>(() => tooLarge.CopyToAsync(Stream.Null));
    }

    [Fact]
    public void ReceiptBufferIsBoundedAndNewestFirst()
    {
        var store = new ReceiptStore(new GatewayOptions { LiveReceiptLimit = 10 }, new ModelBaselineStore());
        for (var index = 0; index < 25; index++)
            store.Add(Receipt(index.ToString()));

        var receipts = store.Read(100);
        Assert.Equal(10, receipts.Count);
        Assert.Equal("24", receipts[0].ReceiptId);
        Assert.Equal("15", receipts[^1].ReceiptId);
    }

    [Fact]
    public void GatewayEvidencePersistsVerifiesAndSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "pocisys-gateway-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new GatewayOptions { PersistentEvidenceEnabled = true, PersistentReceiptLimit = 20 };
            string head;
            using (var ledger = new GatewayEvidenceLedger(options, root))
            {
                ledger.Append(Receipt("persist-1"));
                ledger.Append(Receipt("persist-2"));
                var status = ledger.Status();
                Assert.True(status.Valid);
                Assert.Equal("gateway_self_attested", status.Assurance);
                Assert.Equal(2, status.RetainedReceipts);
                head = status.ChainHead;
            }

            using var reopened = new GatewayEvidenceLedger(options, root);
            Assert.True(reopened.Status().Valid);
            Assert.Equal(head, reopened.Status().ChainHead);
            Assert.Equal(2, reopened.Status().RetainedReceipts);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GatewayEvidenceDetectsTamperingAndKeepsBoundedSignedWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "pocisys-gateway-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new GatewayOptions { PersistentEvidenceEnabled = true, PersistentReceiptLimit = 10 };
            using var ledger = new GatewayEvidenceLedger(options, root);
            for (var index = 0; index < 15; index++)
                ledger.Append(Receipt("bounded-" + index));

            var window = ledger.ReadWindow();
            Assert.Equal(10, window.Body.Entries.Count);
            Assert.NotEqual(GatewayEvidenceLedger.Genesis, window.Body.AnchorPreviousHash);
            Assert.True(GatewayEvidenceLedger.Verify(window).Valid);

            var entries = window.Body.Entries.ToList();
            var first = entries[0];
            entries[0] = first with { Body = first.Body with { Receipt = first.Body.Receipt with { Model = "tampered" } } };
            var tampered = window with { Body = window.Body with { Entries = entries } };
            Assert.False(GatewayEvidenceLedger.Verify(tampered).Valid);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("file:///private")]
    [InlineData("not-a-url")]
    [InlineData("http://localhost:11434?override=true")]
    public void RejectsUnsafeBackendAddresses(string address) =>
        Assert.Throws<InvalidOperationException>(() =>
            new GatewayOptions { BackendBaseUrl = address }.GetValidatedBackendUri());

    [Fact]
    public void BaselineLearnsNormalThenExplainsLargeSlowdown()
    {
        var baselines = new ModelBaselineStore();
        for (var index = 0; index < 5; index++)
            Assert.NotEqual("needs_attention", baselines.AssessAndLearn(Receipt($"normal-{index}" )).Status);

        var slow = Receipt("slow") with { TokensPerSecond = 1, FirstOutputMs = 1_000, DurationMs = 4_000 };
        var result = baselines.AssessAndLearn(slow);

        Assert.Equal("needs_attention", result.Status);
        Assert.Contains(result.Findings, finding => finding.Contains("slower"));
        Assert.Contains(result.Findings, finding => finding.Contains("first output"));
    }

    [Fact]
    public void KaspaSimulationBuildsDeterministicRootWithoutBroadcasting()
    {
        var planner = new KaspaAnchorPlanner();
        var hashes = new[] { new string('a', 64), new string('b', 64), new string('c', 64) };

        var first = planner.CreateMockPlan(hashes);
        var second = planner.CreateMockPlan(hashes);

        Assert.Equal(first.MerkleRoot, second.MerkleRoot);
        Assert.False(first.Broadcast);
        Assert.Equal("simulation", first.Mode);
        Assert.DoesNotContain("prompt", Encoding.UTF8.GetString(Convert.FromHexString(first.TransactionPayloadHex)));
    }

    [Fact]
    public void ContextTrackingExplainsPossibleCompactionWithoutClaimingCertainty()
    {
        var baselines = new ModelBaselineStore();
        var session = "0123456789abcdef01234567";
        for (var index = 0; index < 5; index++)
            baselines.AssessAndLearn(Receipt($"context-{index}") with { InputTokens = 1_000, SessionHash = session });

        var compacted = baselines.AssessAndLearn(
            Receipt("context-drop") with { InputTokens = 400, SessionHash = session });

        Assert.Equal("needs_attention", compacted.Status);
        Assert.Contains(compacted.Findings, finding => finding.Contains("may have compacted"));
    }

    [Fact]
    public void UserSettingsPersistAndClampUnsafeValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "pocisys-gateway-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new GatewayUserSettingsStore(root);
            store.Save(new GatewayUserSettings
            {
                SetupComplete = true,
                MonitoringSensitivity = "unknown",
                BaselineLearningRequests = 1,
                AnchorEveryEvents = int.MaxValue,
                AnchorEveryMinutes = 0,
            });

            var reloaded = new GatewayUserSettingsStore(root).Read();
            Assert.True(reloaded.SetupComplete);
            Assert.Equal("balanced", reloaded.MonitoringSensitivity);
            Assert.Equal(5, reloaded.BaselineLearningRequests);
            Assert.Equal(100_000, reloaded.AnchorEveryEvents);
            Assert.Equal(1, reloaded.AnchorEveryMinutes);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DashboardExplainsSetupBaselinesContextAndKaspaInPlainLanguage()
    {
        Assert.Contains("Choose your modules", Dashboard.Html);
        Assert.Contains("How the gauges work", Dashboard.Html);
        Assert.Contains("context", Dashboard.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Anchor after this many events", Dashboard.Html);
        Assert.Contains("do not by themselves prove", Dashboard.Html);
        Assert.Equal(1, Dashboard.Html.Split("id=\"requests\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("id=\"requestCount\"", Dashboard.Html);
        Assert.Contains("<th>Why</th>", Dashboard.Html);
        Assert.Contains("reason(x.findings)", Dashboard.Html);
        Assert.Contains("reason(x.lastFindings)", Dashboard.Html);
        Assert.Contains("Save and test connection", Dashboard.Html);
        Assert.Contains("Built-in reference chat", Dashboard.Html);
        Assert.Contains("Private reference chat", ChatPage.Html);
        Assert.Contains("Nothing is saved when this tab closes", ChatPage.Html);
        Assert.Contains("Thinking (Ollama)", ChatPage.Html);
        Assert.Contains("Maximum output tokens", ChatPage.Html);
        Assert.Contains("think:thinking.value==='normal'", ChatPage.Html);
        Assert.Contains("options:{num_predict:limit}", ChatPage.Html);
        Assert.Contains("max_tokens:limit", ChatPage.Html);
    }

    [Fact]
    public void BackendTargetPersistsOnlyValidatedAddressAndProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "pocisys-backend-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new GatewayOptions { BackendBaseUrl = "http://127.0.0.1:11434" };
            var first = new BackendTargetStore(options, root);
            first.Save(new BackendTargetUpdate("https://ai.internal.example:8443/base", "OPENAI"));
            var reloaded = new BackendTargetStore(options, root).Read();

            Assert.Equal("https://ai.internal.example:8443/base", reloaded.BaseUrl);
            Assert.Equal("openai", reloaded.Provider);
            Assert.Throws<InvalidOperationException>(() =>
                first.Save(new BackendTargetUpdate("http://user:secret@localhost:11434", "ollama")));
            Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(root, "backend-target.json")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeDiscoversOllamaModelsWithoutSendingPrompt()
    {
        HttpRequestMessage? observed = null;
        var handler = new DelegateHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"models\":[{\"name\":\"qwen-test\"},{\"model\":\"llama-test\"}]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var options = new GatewayOptions
        {
            BackendBaseUrl = "http://ai.test:11434",
            BackendApiKey = "fixture-key",
        };
        var target = new BackendTargetStore(options, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        target.Save(new BackendTargetUpdate(options.BackendBaseUrl, "ollama"));
        var probe = new BackendProbe(new HttpClient(handler), target, options);

        var result = await probe.Test();

        Assert.True(result.Connected);
        Assert.Equal("ollama", result.Provider);
        Assert.Equal(["llama-test", "qwen-test"], result.Models);
        Assert.Equal("/api/tags", observed!.RequestUri!.AbsolutePath);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "fixture-key"), observed.Headers.Authorization);
        Assert.Null(observed.Content);
    }

    private static GatewayReceipt Receipt(string id) => new(
        "pocisys.gateway-exchange.v1", id, DateTimeOffset.UtcNow, "POST", "/v1/chat/completions",
        "http://localhost:11434", 200, 10, 2, "provider_content", 1, 1,
        new string('0', 64), new string('1', 64), "test", 1, 1, "provider_reported", 10, true, null);

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
