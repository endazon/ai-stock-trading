using System.Net;
using System.Text;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AiStockTrading.Shared.Infrastructure.Grpc.LlmGateway.V1;
using AwesomeAssertions;
using Grpc.Core;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// FR-04, FR-11, #1269, IADR-0517: Sent=false の原因（原因の種類・上流の状態コード）を、REST と gRPC の
// どちらの輸送で受けても**同じ記録**にする。
//
// #1267 は REST（JSON の `failureKind` / `upstreamStatusCode`）だけを読んだ。gRPC は proto の写しに
// フィールドが無く、常に「種別不明」になっていた。基盤の MSP#1824 が proto に `failure_kind` /
// `upstream_status_code` を足したので、写しを追随して両輸送を揃える。
//
// 🔴 突き合わせるのは輸送の出口（`LlmCompletionPayload`）と、記録の元になる原因（`LlmGatewayUnsentCause`
// とその 1 行の説明）である。ログ・Hold の判断理由・台帳・通知はすべて `LlmGatewayUnsent.From(payload)` から
// 作られる（#1267）ため、ここが一致すれば記録も一致する。
public class LlmTransportUnsentParityTests
{
    private static readonly LlmCompletionCall Call = new("prompt", 4096, null, "internal", "trade-decision");

    private const string UpstreamText = "呼び出し先 anthropic-managed が現在利用できません。";
    private const string RoutingAllowed = "internal は anthropic-managed へ送信可";
    private const string EgressReason = "restricted は外部 LLM へ送信不可";
    private const string ProviderText = "呼び出し先プロバイダ anthropic-managed が未登録です。";

    // 1 つの論理的な入力を、REST の JSON 本文と gRPC の応答の両方で表す。
    // proto3 に null は無いので、REST の「欠落・null」は gRPC の "" / 0 に対応する。
    public sealed record Case(
        string Name, bool Sent, string Text, string? RoutingReason,
        string? FailureKind, int? UpstreamStatusCode,
        LlmGatewayUnsentKind? ExpectedKind, int? ExpectedStatus)
    {
        public override string ToString() => Name;
    }

    private static readonly Case[] Cases =
    [
        new("egress_denied", false, EgressReason, EgressReason, "egress_denied", null,
            LlmGatewayUnsentKind.EgressDenied, null),
        new("upstream_error_429", false, UpstreamText, RoutingAllowed, "upstream_error", 429,
            LlmGatewayUnsentKind.UpstreamError, 429),
        new("upstream_error_状態なし", false, UpstreamText, RoutingAllowed, "upstream_error", null,
            LlmGatewayUnsentKind.UpstreamError, null),
        new("provider_missing", false, ProviderText, RoutingAllowed, "provider_missing", null,
            LlmGatewayUnsentKind.ProviderMissing, null),
        // 未知の種類は「種別不明」（例外にも別の種類にもしない）。状態コードは読める値なら運ぶ。
        new("未知の種類", false, UpstreamText, RoutingAllowed, "quota_exceeded", 503,
            null, 503),
        // 範囲外の状態コード（100〜599 の外）は「無い」と同じ。
        new("範囲外の状態コード", false, UpstreamText, RoutingAllowed, "upstream_error", 42,
            LlmGatewayUnsentKind.UpstreamError, null),
        // 後方互換: 旧い基盤（原因のフィールドを返さない）の Sent=false。
        new("旧い基盤の_Sent_false", false, UpstreamText, RoutingAllowed, null, null,
            null, null),
        // 送信できた応答（原因は無い）。
        new("none_Sent_true", true, "買い", RoutingAllowed, null, null,
            null, null),
    ];

    public static TheoryData<Case> AllCases()
    {
        var data = new TheoryData<Case>();
        foreach (var c in Cases)
            data.Add(c);
        return data;
    }

    // T-04-023: 同じ論理的な入力は、REST と gRPC で同じ応答・同じ原因・同じ 1 行の説明になる。
    [Theory]
    [MemberData(nameof(AllCases))]
    public async Task REST_と_gRPC_は同じ入力から同じ記録を作る(Case c)
    {
        var rest = await RestPayload(RestBody(c));
        var grpc = await GrpcPayload(GrpcResponse(c));

        // 期待値そのもの（両輸送が揃って間違える退行を捕まえる）。
        rest.FailureKind.Should().Be(c.ExpectedKind);
        rest.UpstreamStatusCode.Should().Be(c.ExpectedStatus);

        // 輸送の出口が丸ごと一致する（Text / Sent / RoutingReason を含む）。
        grpc.Should().Be(rest);
        LlmGatewayUnsent.From(grpc).Should().Be(LlmGatewayUnsent.From(rest));
        LlmGatewayUnsent.From(grpc).Describe().Should().Be(LlmGatewayUnsent.From(rest).Describe());
    }

    // T-04-023（陰性対照）: 種類が報告されていれば「種別不明」と書かない／報告が無ければ推測で埋めない。
    // 🔴 これが無いと、両輸送が揃って原因を落としても上の一致は緑のままになる。
    [Fact]
    public async Task gRPC_で原因の種類が報告されれば説明に載り_無ければ種別不明と書く()
    {
        var reported = await GrpcPayload(new CompleteResponse
        {
            Text = UpstreamText,
            Sent = false,
            RoutingReason = RoutingAllowed,
            FailureKind = "upstream_error",
            UpstreamStatusCode = 429,
        });
        var absent = await GrpcPayload(new CompleteResponse
        {
            Text = UpstreamText,
            Sent = false,
            RoutingReason = RoutingAllowed,
        });

        LlmGatewayUnsent.From(reported).Describe().Should().StartWith("種別: 上流の不調／上流 429／");
        LlmGatewayUnsent.From(absent).Describe().Should().StartWith("種別: 種別不明／理由: ");
        LlmGatewayUnsent.From(absent).Describe().Should().NotContain("上流 ");
    }

    // T-04-024: 写しのフィールド番号が正本（MSP develop 51633872）と一致する。
    // 🔴 番号がずれると、実往復で別のフィールドとして読まれ**例外にならずに**値が消える（CI の実往復は無い）。
    [Fact]
    public void 写しの原因フィールドの番号は正本と一致する()
    {
        CompleteResponse.FailureKindFieldNumber.Should().Be(9);
        CompleteResponse.UpstreamStatusCodeFieldNumber.Should().Be(10);
        CompletionStreamEvent.FailureKindFieldNumber.Should().Be(10);
        CompletionStreamEvent.UpstreamStatusCodeFieldNumber.Should().Be(11);
    }

    // T-04-024（後方互換）: 原因のフィールドを持たない旧い wire（フィールド 1〜8 だけ）を読んでも
    // 既定値（"" / 0）になり、輸送の出口では null（種別不明）になる。
    [Fact]
    public async Task 原因のフィールドを持たない旧い_wire_は既定値として読める()
    {
        var parsed = CompleteResponse.Parser.ParseFrom(OldWire(UpstreamText, RoutingAllowed));

        parsed.FailureKind.Should().BeEmpty();
        parsed.UpstreamStatusCode.Should().Be(0);

        var payload = await GrpcPayload(parsed);
        payload.FailureKind.Should().BeNull();
        payload.UpstreamStatusCode.Should().BeNull();
        payload.RoutingReason.Should().Be(RoutingAllowed);
    }

    // ---- 入力の組み立て --------------------------------------------------------------------------

    private static string RestBody(Case c)
    {
        var sb = new StringBuilder();
        sb.Append("{\"text\":").Append(Json(c.Text))
          .Append(",\"sent\":").Append(c.Sent ? "true" : "false")
          .Append(",\"routingReason\":").Append(c.RoutingReason is null ? "null" : Json(c.RoutingReason))
          // 基盤の CompletionApiResponse はトークン数を int で常に返す（gRPC の 0 と同じ値）。
          // 🔴 トークン数の欠落の扱い（REST は null・gRPC は 0）は本件の範囲外のため、ここでは揃えて与える。
          .Append(",\"inputTokens\":0,\"outputTokens\":0");
        // 欠落（旧い基盤）は**フィールドごと出さない**。
        if (c.FailureKind is not null)
            sb.Append(",\"failureKind\":").Append(Json(c.FailureKind));
        if (c.UpstreamStatusCode is { } status)
            sb.Append(",\"upstreamStatusCode\":").Append(status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return sb.Append('}').ToString();
    }

    private static CompleteResponse GrpcResponse(Case c) => new()
    {
        Text = c.Text,
        Sent = c.Sent,
        RoutingReason = c.RoutingReason ?? string.Empty,
        // proto3 の「無い」は "" / 0。
        FailureKind = c.FailureKind ?? string.Empty,
        UpstreamStatusCode = c.UpstreamStatusCode ?? 0,
    };

    // 旧い写し（フィールド 1〜8）が出す wire を手で組む: 1=text、7=routing_reason（sent=false は proto3 では出力されない）。
    // 生成コードの直列化を使わないのは、新しい写しの型で組むと「旧い形」であることを試験が保証できないため。
    private static byte[] OldWire(string text, string routingReason)
    {
        using var ms = new MemoryStream();
        using (var output = new Google.Protobuf.CodedOutputStream(ms))
        {
            output.WriteTag(1, Google.Protobuf.WireFormat.WireType.LengthDelimited);
            output.WriteString(text);
            output.WriteTag(7, Google.Protobuf.WireFormat.WireType.LengthDelimited);
            output.WriteString(routingReason);
        }
        return ms.ToArray();
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static async Task<LlmCompletionPayload> RestPayload(string body)
    {
        using var http = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://llm-gateway") };
        var exchange = await new RestLlmCompletionTransport(http).CompleteAsync(Call);
        exchange.Outcome.Should().Be(LlmTransportOutcome.Completed);
        return exchange.Payload!;
    }

    private static async Task<LlmCompletionPayload> GrpcPayload(CompleteResponse response)
    {
        var exchange = await new GrpcLlmCompletionTransport(new FakeCompletionClient(response)).CompleteAsync(Call);
        exchange.Outcome.Should().Be(LlmTransportOutcome.Completed);
        return exchange.Payload!;
    }

    // ---- test double ---------------------------------------------------------------------------

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FakeCompletionClient(CompleteResponse response) : LlmCompletion.LlmCompletionClient
    {
        public override AsyncUnaryCall<CompleteResponse> CompleteAsync(CompleteRequest request, CallOptions options) =>
            new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
    }
}
