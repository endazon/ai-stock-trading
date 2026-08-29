using System.Net;
using System.Text;
using System.Text.Json;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// FR-16, FR-11, #563, IADR-0268: 監査台帳（GET /audit/events/by-type）から**記録済みの判断根拠**を引く
// s2s 照会と、その fail-safe を fake HttpMessageHandler で検証する（実ネットワーク不使用）。
//
// 🔴 **供給不達はすべて null（未供給）へ倒す。空の辞書（引けたが記録が無い）と混ぜない。**
public class HttpTradeRationaleSourceTests
{
    private static readonly DateOnly From = new(2026, 8, 28);
    private static readonly DateOnly To = new(2026, 8, 28);
    private static readonly Guid DecisionId = new("11111111-1111-1111-1111-111111111111");

    private static HttpTradeRationaleSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://audit") },
            NullLogger<HttpTradeRationaleSource>.Instance);

    /// <summary>
    /// 🔴 <b>台帳の応答を「実物の書式」で組み立てる。</b>
    /// <para>
    /// <see cref="AuditDetailJson.Options"/> は<b>監査サービスが実際に書くときに使う設定そのもの</b>である
    /// （IADR-0199 決定6）。ここでリテラル JSON を手書きすると、<b>書き手が書式を変えたときに
    /// 本テストだけが古い形のまま緑になり、本番だけが静かに壊れる。</b>
    /// </para>
    /// </summary>
    private static string Ledger(params object[] events)
    {
        var entries = events.Select(e => new
        {
            id = Guid.NewGuid(),
            eventType = e.GetType().Name,
            detail = JsonSerializer.Serialize(e, e.GetType(), AuditDetailJson.Options),
        });

        return JsonSerializer.Serialize(entries);
    }

    private static TradeDecisionMade Decision(Guid decisionId, string rationale) => new(
        decisionId,
        new OrderIntent("7203", Market.Japan, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 100, 2_500m),
        rationale,
        new DateTimeOffset(2026, 8, 28, 0, 5, 0, TimeSpan.Zero));

    [Fact]
    public async Task 期間を半開区間で要求のクエリ文字列に載せる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger());

        await Source(handler).GetRationalesAsync(From, To);

        handler.LastUri!.AbsolutePath.Should().Be("/audit/events/by-type");
        // 🔴 半開区間 [8/28 00:00 JST, 8/29 00:00 JST) = [8/27 15:00Z, 8/28 15:00Z)。
        // 終端を 23:59:59 で閉じるとその日の最後の 1 秒が落ちる。
        Uri.UnescapeDataString(handler.LastUri.Query).Should().Contain("from=2026-08-27T15:00:00.0000000+00:00");
        Uri.UnescapeDataString(handler.LastUri.Query).Should().Contain("to=2026-08-28T15:00:00.0000000+00:00");
        handler.LastUri.Query.Should().Contain("types=TradeDecisionMade");
    }

    // 🔴 **肯定形**: 記録された根拠を DecisionId 引きでそのまま返す。
    [Fact]
    public async Task 記録された判断根拠をDecisionId引きで返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(Decision(DecisionId, "始値が支持線で反発。")));

        var rationales = await Source(handler).GetRationalesAsync(From, To);

        rationales.Should().NotBeNull();
        rationales!.Should().ContainKey(DecisionId);
        rationales[DecisionId].Should().Be("始値が支持線で反発。");
    }

    // 🔴 **否定形（上の肯定形と対）**: 引けなかったことを「根拠が無かった」と書かない（空の辞書へ倒さない）。
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 非2xxは未供給へ倒す(HttpStatusCode status)
    {
        var rationales = await Source(new StubHandler(status, Ledger())).GetRationalesAsync(From, To);

        rationales.Should().BeNull();
    }

    [Fact]
    public async Task 例外は未供給へ倒す()
    {
        (await Source(new ThrowingHandler()).GetRationalesAsync(From, To)).Should().BeNull();
    }

    [Fact]
    public async Task 応答本文がnullなら未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.OK, "null")).GetRationalesAsync(From, To)).Should().BeNull();
    }

    // 引けたが記録が 1 件も無い期間は**空の辞書**（未供給ではない）。上の否定形と区別できることを固定する。
    [Fact]
    public async Task 記録が0件の期間は空の辞書であり未供給ではない()
    {
        var rationales = await Source(new StubHandler(HttpStatusCode.OK, Ledger())).GetRationalesAsync(From, To);

        rationales.Should().NotBeNull();
        rationales!.Should().BeEmpty();
    }

    [Fact]
    public async Task 壊れた記録は当該1件だけを落とし期間全体を落とさない()
    {
        var good = Ledger(Decision(DecisionId, "始値が支持線で反発。"));
        // 1 件目の本文を壊す（読めなかった記録は捨ててログへ残し、当該約定の根拠だけが未供給になる）。
        var broken = good.Replace("\"detail\":\"{", "\"detail\":\"{broken", StringComparison.Ordinal);
        var body = "[" + broken[1..^1] + "," + good[1..^1] + "]";

        var rationales = await Source(new StubHandler(HttpStatusCode.OK, body)).GetRationalesAsync(From, To);

        rationales.Should().NotBeNull();
        rationales!.Should().ContainKey(DecisionId);
    }

    [Fact]
    public async Task 要求していない種別は混ぜない()
    {
        // 台帳側の絞り込みが効かず別種別が返ってきた場合。**混ぜずに落とす。**
        var body = Ledger(Decision(DecisionId, "他種別"))
            .Replace("\"eventType\":\"TradeDecisionMade\"", "\"eventType\":\"OrderApproved\"", StringComparison.Ordinal);

        var rationales = await Source(new StubHandler(HttpStatusCode.OK, body)).GetRationalesAsync(From, To);

        rationales.Should().NotBeNull();
        rationales!.Should().BeEmpty();
    }

    [Fact]
    public async Task 空の根拠は載せない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(Decision(DecisionId, "   ")));

        var rationales = await Source(handler).GetRationalesAsync(From, To);

        rationales.Should().NotBeNull();
        rationales!.Should().BeEmpty();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }
}
