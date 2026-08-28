using System.Net;
using System.Text;
using System.Text.Json;
using ReportService.Infrastructure.Adapters;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Infrastructure.Tests;

// FR-06, FR-10, FR-11, UC-06, #381, ADR-0022 決定1・決定2, IADR-0196 決定2〜4, IADR-0199:
// 監査台帳（権威源）から為替の情報源の状態を期間で引く。fake HttpMessageHandler で検証する（実ネットワーク不使用）。
//
// 🔴 **本アダプタの要点は 3 つある。**
//   ① **失敗の向き**——供給不達は `null`（未供給）。同居の `HttpPeriodFillSource`（空列）とは逆である。
//   ② **期間の写像**——JST 取引日 → UTC の**半開区間**。閉じると最後の 1 秒が落ちる。
//   ③ **出典**——**台帳に証拠のある源だけ**を載せる（記録が無い期間に第一の源を騙らない）。
public class HttpFxSourceStatusSourceTests
{
    private static readonly DateOnly From = new(2026, 8, 1);
    private static readonly DateOnly To = new(2026, 8, 8);
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 3, 0, 0, TimeSpan.Zero);

    private static HttpFxSourceStatusSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://audit") },
            NullLogger<HttpFxSourceStatusSource>.Instance);

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

    [Fact]
    public async Task 期間と種別を要求のクエリ文字列に載せる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger());

        await Source(handler).GetStatusAsync(From, To);

        handler.LastUri!.AbsolutePath.Should().Be("/audit/events/by-type");
        var query = Uri.UnescapeDataString(handler.LastUri.Query);
        query.Should().Contain(nameof(FxRateSourceFellBack))
            .And.Contain(nameof(FxRateSourcePrimaryRestored))
            .And.Contain(nameof(FxRateStale))
            .And.Contain(nameof(PositionClosedWithStaleFxRate))
            // #513: 静かな期間の出典はこの種別からしか導けない。要求から落ちると平常時が再び空白になる。
            .And.Contain(nameof(FxRateSourceUsed));
    }

    // 🔴 **JST 取引日 → UTC 半開区間**（IADR-0199 決定3）。
    // 8/1〜8/8（JST）は UTC で 7/31 15:00 〜 8/8 15:00 である。
    // **ここを取り違えると、日付境界の事象が隣の日の報告書へ落ちる。**
    [Fact]
    public async Task 期間をJSTの半開区間としてUTCへ写す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger());

        await Source(handler).GetStatusAsync(From, To);

        var query = Uri.UnescapeDataString(handler.LastUri!.Query);
        query.Should().Contain("from=2026-07-31T15:00:00.0000000+00:00");
        // 終端は **to の翌日 0 時 JST**（＝8/8 15:00 UTC）。閉区間にすると 8/8 の最後の 1 秒が落ちる。
        query.Should().Contain("to=2026-08-08T15:00:00.0000000+00:00");
    }

    [Fact]
    public async Task 台帳の記録を種別ごとに復元する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(
            new FxRateSourceFellBack("USD", "fred", 2, 2, T0),
            new FxRateSourcePrimaryRestored("USD", "boj", T0.AddHours(-6), T0),
            new FxRateStale("USD", T0.AddDays(-7), 7, 5, 30, T0),
            new PositionClosedWithStaleFxRate("7203", Market.Japan, "JPY", 300, 0.0067m, T0.AddDays(-31), 31, T0)));

        var result = await Source(handler).GetStatusAsync(From, To);

        result.Should().NotBeNull();
        result!.FellBacks.Should().ContainSingle().Which.SourceName.Should().Be("fred");
        result.Restorations.Should().ContainSingle().Which.FallbackDuration.Should().Be(TimeSpan.FromHours(6));
        result.StaleWarnings.Should().ContainSingle().Which.MaxAgeDays.Should().Be(30);
        result.StaleCloses.Should().ContainSingle().Which.RateAsOf.Should().Be(T0.AddDays(-31));
    }

    // 🔴 **数値と列挙が本当に戻っていること。** 名前が食い違うと `JsonSerializer` は例外を投げず
    // **既定値（0 / null）の record を黙って作る**——**数量 0・金額 0 の行が報告書に載る。**
    [Fact]
    public async Task 復元した値が既定値へ潰れていない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(
            new PositionClosedWithStaleFxRate("7203", Market.Japan, "JPY", 300, 0.0067m, T0.AddDays(-31), 31, T0)));

        var close = (await Source(handler).GetStatusAsync(From, To))!.StaleCloses.Single();

        close.Symbol.Should().Be("7203");
        close.Quantity.Should().Be(300, "0 に潰れていない");
        close.FxRateToBase.Should().Be(0.0067m, "0 に潰れていない");
        close.Market.Should().Be(Market.Japan, "列挙は文字列で往復する");
        close.AgeDays.Should().Be(31);
    }

    // 決定5: 出典は**証拠のある源だけ**。日銀を使った記録（復帰）があるなら出す。
    [Fact]
    public async Task 日銀を使った記録があれば出典を返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(
            new FxRateSourcePrimaryRestored("USD", "boj", T0.AddHours(-6), T0)));

        var result = await Source(handler).GetStatusAsync(From, To);

        result!.PrimarySourceCredits.Should().ContainSingle().Which.Should().Be(FxSourceCredits.Boj);
    }

    // 🔴 **否定形（決定5 の核心）。** 記録が無い期間に第一の源のクレジットを騙らない。
    // **遷移でしか発行しない**ため、静かな期間はどの源を使ったのか台帳から証明できない。
    [Fact]
    public async Task 記録が無い期間は_出典を返さない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger());

        var result = await Source(handler).GetStatusAsync(From, To);

        result.Should().NotBeNull("照会は成功しており、事象が無かったという事実である");
        result!.PrimarySourceCredits.Should().BeEmpty("使ったと証明できない源のクレジットは出さない");
    }

    // --- #513（IADR-0225）: 静かな期間の出典 ------------------------------------------------------

    // 🔴 **本 issue の核心。** 切替も復帰も起きない期間でも、**使用記録から出典を導ける**。
    // これが無いと日報の為替欄は平常時こそ「記録からは特定できません」になる。
    [Fact]
    public async Task 静かな期間でも_使用記録から出典を導ける()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(
            new FxRateSourceUsed("USD", "boj", 1, 2, T0)));

        var result = await Source(handler).GetStatusAsync(From, To);

        result!.PrimarySourceCredits.Should().ContainSingle().Which.Should().Be(FxSourceCredits.Boj);
        result.Usages.Should().ContainSingle().Which.IsPrimary.Should().BeTrue();
        result.IsClean.Should().BeTrue("使用記録は劣化ではない");
    }

    // 🔴 **否定形（IADR-0196 決定4 を壊していないこと）。** 使用記録がフォールバック先だけなら、
    // **日銀のクレジットは出さない**——使っていない源のクレジットは事実に反する。
    [Fact]
    public async Task 使用記録がフォールバック先だけなら_日銀の出典を出さない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(
            new FxRateSourceUsed("USD", "fred", 2, 2, T0)));

        var result = await Source(handler).GetStatusAsync(From, To);

        result!.PrimarySourceCredits.Should().BeEmpty();
        result.UsedSourceNames.Should().ContainSingle().Which.Should().Be("fred", "使った源自体は特定できている");
        result.PrimarySourceNames.Should().BeEmpty("第一の情報源を使った証拠は無い");
    }

    // 🔴 **否定形。** FRED へ落ちていた記録しか無いなら、**日銀のクレジットは出さない**
    //（IADR-0196 決定4: 使っていない情報源のクレジットは事実に反する）。
    [Fact]
    public async Task フォールバック先しか使っていなければ_日銀の出典を出さない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(
            new FxRateSourceFellBack("USD", "fred", 2, 2, T0)));

        var result = await Source(handler).GetStatusAsync(From, To);

        result!.PrimarySourceCredits.Should().BeEmpty();
    }

    // 🔴 **否定形（失敗の向き）。** 照会失敗は `null`（未供給）であり、空（＝劣化なし）ではない。
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task 照会に失敗したら_null_を返す(HttpStatusCode status)
    {
        var handler = new StubHandler(status, "");

        var result = await Source(handler).GetStatusAsync(From, To);

        result.Should().BeNull("「照会できませんでした」を「劣化はありませんでした」と書かない");
    }

    [Fact]
    public async Task 接続できなければ_null_を返す()
    {
        var result = await Source(new ThrowingHandler()).GetStatusAsync(From, To);

        result.Should().BeNull();
    }

    // **壊れた 1 件で期間全体を落とさない**（残りは報告できる）。ただし黙って捨てない（ログへ残す）。
    [Fact]
    public async Task 復元できない記録が混ざっても_残りを返す()
    {
        var good = Ledger(new FxRateSourceFellBack("USD", "fred", 2, 2, T0));
        var broken = good.Replace("\"detail\":\"{", "\"detail\":\"{broken", StringComparison.Ordinal);
        var mixed = broken[..^1] + "," + good[1..];

        var result = await Source(new StubHandler(HttpStatusCode.OK, mixed)).GetStatusAsync(From, To);

        result.Should().NotBeNull();
        result!.FellBacks.Should().ContainSingle("読めた 1 件は報告する（壊れた 1 件で期間を落とさない）");
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }
}
