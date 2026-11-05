using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AiStockTrading.Shared.Infrastructure.Grpc.LlmGateway.V1;
using AwesomeAssertions;
using Grpc.Core;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// FR-04, NFR, #1523, IADR-0607: ゲートウェイのプロンプトキャッシュの契約（MSP の IADR-0671・MSP develop b05b19b9）を、
// REST と gRPC の両輸送で同じに運ぶ。要求は接頭辞（REST `cacheablePrefix`・proto `cacheable_prefix = 6`）、
// 応答はキャッシュの書き・読み（REST `cacheCreationInputTokens` / `cacheReadInputTokens`・proto 11 / 12）。
public class LlmTransportPromptCacheTests
{
    private static readonly LlmCompletionCall WithPrefix =
        new("残り", 4096, "claude-sonnet-5-5", "internal", "trade-decision", CacheablePrefix: "接頭辞|");

    private static readonly LlmCompletionCall WithoutPrefix =
        new("全文", 4096, null, "internal", "trade-decision");

    [Fact]
    public async Task REST_の要求に接頭辞が載り_応答の書きと読みが出口へ出る()
    {
        var handler = new CapturingHandler(
            """{"text":"買い","sent":true,"model":"claude-sonnet-5-5","inputTokens":3000,"outputTokens":50,"cacheCreationInputTokens":0,"cacheReadInputTokens":2048}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") };

        var exchange = await new RestLlmCompletionTransport(http).CompleteAsync(WithPrefix);

        using var sent = JsonDocument.Parse(handler.Body!);
        sent.RootElement.GetProperty("cacheablePrefix").GetString().Should().Be("接頭辞|");
        sent.RootElement.GetProperty("prompt").GetString().Should().Be("残り");
        sent.RootElement.GetProperty("maxTokens").GetInt32().Should().Be(4096);
        sent.RootElement.GetProperty("model").GetString().Should().Be("claude-sonnet-5-5");
        exchange.Payload!.InputTokens.Should().Be(3000);
        exchange.Payload.CacheCreationInputTokens.Should().Be(0);
        exchange.Payload.CacheReadInputTokens.Should().Be(2048);
    }

    [Fact]
    public async Task REST_で接頭辞が無ければ_null_を送り_キャッシュの欄の欠落は_0_と読む()
    {
        var handler = new CapturingHandler("""{"text":"買い","sent":true,"inputTokens":10,"outputTokens":5}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") };

        var exchange = await new RestLlmCompletionTransport(http).CompleteAsync(WithoutPrefix);

        using var sent = JsonDocument.Parse(handler.Body!);
        sent.RootElement.GetProperty("cacheablePrefix").ValueKind.Should().Be(JsonValueKind.Null);
        exchange.Payload!.CacheCreationInputTokens.Should().Be(0);
        exchange.Payload.CacheReadInputTokens.Should().Be(0);
    }

    [Fact]
    public async Task gRPC_の要求に接頭辞が載り_応答の書きと読みが出口へ出る()
    {
        var client = new CapturingCompletionClient(new CompleteResponse
        {
            Text = "買い",
            Sent = true,
            Model = "claude-sonnet-5-5",
            InputTokens = 3000,
            OutputTokens = 50,
            CacheCreationInputTokens = 2048,
            CacheReadInputTokens = 0,
        });

        var exchange = await new GrpcLlmCompletionTransport(client).CompleteAsync(WithPrefix);

        client.Request!.CacheablePrefix.Should().Be("接頭辞|");
        client.Request.Prompt.Should().Be("残り");
        exchange.Payload!.CacheCreationInputTokens.Should().Be(2048);
        exchange.Payload.CacheReadInputTokens.Should().Be(0);
    }

    [Fact]
    public async Task gRPC_で接頭辞が無ければ空文字を送る()
    {
        var client = new CapturingCompletionClient(new CompleteResponse { Text = "買い", Sent = true });

        await new GrpcLlmCompletionTransport(client).CompleteAsync(WithoutPrefix);

        client.Request!.CacheablePrefix.Should().BeEmpty();
    }

    // 同じ論理的な応答は、REST と gRPC で同じ出口になる。
    [Fact]
    public async Task REST_と_gRPC_は同じ応答から同じ出口を作る()
    {
        var handler = new CapturingHandler(
            """{"text":"買い","sent":true,"model":"claude-haiku-5-5","inputTokens":5000,"outputTokens":7,"cacheCreationInputTokens":100,"cacheReadInputTokens":4000}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") };
        var rest = (await new RestLlmCompletionTransport(http).CompleteAsync(WithPrefix)).Payload;
        var grpc = (await new GrpcLlmCompletionTransport(new CapturingCompletionClient(new CompleteResponse
        {
            Text = "買い",
            Sent = true,
            Model = "claude-haiku-5-5",
            InputTokens = 5000,
            OutputTokens = 7,
            CacheCreationInputTokens = 100,
            CacheReadInputTokens = 4000,
        })).CompleteAsync(WithPrefix)).Payload;

        grpc.Should().Be(rest);
    }

    // proto の写しのフィールド番号が正本（MSP develop b05b19b9 の completion.proto）と一致する。
    // 🔴 番号がずれると、実往復で別のフィールドとして読まれ**例外にならずに**値が消える。
    [Fact]
    public void 写しのキャッシュのフィールドの番号は正本と一致する()
    {
        CompleteRequest.CacheablePrefixFieldNumber.Should().Be(6);
        CompleteResponse.CacheCreationInputTokensFieldNumber.Should().Be(11);
        CompleteResponse.CacheReadInputTokensFieldNumber.Should().Be(12);
        CompletionStreamEvent.CacheCreationInputTokensFieldNumber.Should().Be(12);
        CompletionStreamEvent.CacheReadInputTokensFieldNumber.Should().Be(13);
    }

    // 往復: 写しで直列化した要求・応答を読み戻すと同じ値になる（wire の上で落ちない）。
    [Fact]
    public void 写しの要求と応答は直列化して読み戻すと同じ値になる()
    {
        var request = new CompleteRequest { Prompt = "残り", CacheablePrefix = "接頭辞|", MaxTokens = 4096, Purpose = "trade-decision" };
        var response = new CompleteResponse { Text = "買い", Sent = true, InputTokens = 3000, CacheCreationInputTokens = 1, CacheReadInputTokens = 2048 };
        var ev = new CompletionStreamEvent { Done = true, Sent = true, CacheCreationInputTokens = 3, CacheReadInputTokens = 4 };

        CompleteRequest.Parser.ParseFrom(Google.Protobuf.MessageExtensions.ToByteArray(request)).Should().Be(request);
        CompleteResponse.Parser.ParseFrom(Google.Protobuf.MessageExtensions.ToByteArray(response)).Should().Be(response);
        CompletionStreamEvent.Parser.ParseFrom(Google.Protobuf.MessageExtensions.ToByteArray(ev)).Should().Be(ev);
    }

    // 旧い wire（キャッシュの欄が無い）を読んでも 0 になる（後方互換）。
    [Fact]
    public async Task キャッシュの欄を持たない旧い_wire_は_0_として読める()
    {
        var parsed = CompleteResponse.Parser.ParseFrom(Google.Protobuf.MessageExtensions.ToByteArray(
            new CompleteResponse { Text = "買い", Sent = true, InputTokens = 10 }));
        var payload = (await new GrpcLlmCompletionTransport(new CapturingCompletionClient(parsed)).CompleteAsync(WithoutPrefix)).Payload!;

        payload.CacheCreationInputTokens.Should().Be(0);
        payload.CacheReadInputTokens.Should().Be(0);
    }

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CapturingCompletionClient(CompleteResponse response) : LlmCompletion.LlmCompletionClient
    {
        public CompleteRequest? Request { get; private set; }

        public override AsyncUnaryCall<CompleteResponse> CompleteAsync(CompleteRequest request, CallOptions options)
        {
            Request = request;
            return new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }
}
