extern alias RiskManagementWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using NotificationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RiskFeatures = RiskManagementWorker::RiskManagementService.Features.RiskManagement;
using RiskAdopt = RiskManagementWorker::RiskManagementService.Features.RiskManagement.AdoptPositionDrift;
using RiskGfv = RiskManagementWorker::RiskManagementService.Features.RiskManagement.ClearGoodFaithViolations;
using RiskStatus = RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetRiskStatus;

namespace NotificationService.Tests;

// 🔴 T-10-932〜935, FR-10, FR-14, FR-19, UC-06, UC-07, ADR-0009, ADR-0028, #957, IADR-0062, IADR-0075, IADR-0182,
// IADR-0408（2026-09-25 追記。T-10-800 の同型）: 通知（Discord の操作）が読むリスク管理の kill switch・一時停止・稼働状態・
// GFV 解除の応答に、**送り手の本物の型を送り手の実際の JSON 設定（web 既定＝camelCase・列挙は数値）で直列化した応答**を読ませる。
// 既存の各アダプタのテストは手書きの JSON であり、送り手で項目名を変えても緑のまま、実行時は既定値で読む（統制は送り手で成立
// するが、`Engaged`・`Paused` → 操作の結果を「OFF」と報告する／稼働状態の真偽値 → 「kill switch=OFF・新規建て=可能」と表示する／
// `RemainingCount` → 「停止は継続します」が出ない）。送り手が web 既定のまま出していることはリスク管理側の T-10-805 が、
// GFV 解除の外側（送り手は匿名型）の項目名は T-10-938 が、それぞれ本物の Program.cs で固定する。
//
// 🔴 T-10-941〜942, FR-14, FR-10, ADR-0041 決定2, #990, IADR-0354, IADR-0408（2026-09-25 追記）: 稼働状態の送り手は口座を照会できて
// いない間（新規建ては止まっている）資金・上限の実額を null で返す。受け手が上限を非 null で受けていたため、まさにその間 `/status` は
// JsonException で失敗していた。資金・上限が null の送り手の値も読めて、上限を「不明」と表示する（0 と表示しない）ことを固定する。
//
// 🔴 T-10-989, FR-10, FR-11, FR-14, UC-06, ADR-0041 決定 4, #871, IADR-0350, IADR-0423: 乖離の取り込み
// （POST /risk-controls/position-drift/adopt）の受理（200）・受理不能（422）の本文も、送り手の本物の型
// （`PositionDriftAdoptionResponse`・`PositionDriftAdoptionRejectionBody`）を web 既定で直列化したものを読ませる。
// 送り手がその設定で出していることはリスク管理側の T-10-985 が本物の Program.cs で固定する。逆向き（Bot が送る本文を
// 送り手の本物の要求型 `PositionDriftAdoptionRequest` で読めること＝操作者の onBehalfOf が届くこと）も同じ所で固定する。
public class RiskControlOperationReadContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 1, 0, 0, TimeSpan.Zero);

    private static HttpClient Client(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(status, JsonSerializer.Serialize(value, value.GetType(), Web)))
        {
            BaseAddress = new Uri("http://risk-management-service"),
        };

    // 🔴 T-10-932: kill switch の起動・解除（POST /risk-controls/kill-switch/engage・disengage）。
    [Fact]
    public async Task kill_switch_の操作結果は送り手の本物の型を直列化した応答から読める()
    {
        var engaged = new RiskFeatures.KillSwitchState(true, "owner", "Discord Bot 経由の操作（actor=owner）", T0);
        var disengaged = new RiskFeatures.KillSwitchState(false, "owner", "再開", T0.AddMinutes(5));

        var on = await new HttpKillSwitchController(Client(engaged), NullLogger<HttpKillSwitchController>.Instance)
            .EngageAsync("緊急停止");
        var off = await new HttpKillSwitchController(Client(disengaged), NullLogger<HttpKillSwitchController>.Instance)
            .DisengageAsync("再開");

        (on.Succeeded, on.Engaged).Should().Be((true, true));
        (off.Succeeded, off.Engaged).Should().Be((true, false));
    }

    // 🔴 T-10-933: 一時停止・再開（POST /risk-controls/pause・resume）。
    [Fact]
    public async Task 一時停止と再開の操作結果は送り手の本物の型を直列化した応答から読める()
    {
        var paused = new RiskFeatures.PauseState(true, "owner", "決算発表前", T0);
        var resumed = new RiskFeatures.PauseState(false, "owner", "再開", T0.AddHours(1));

        var p = await new HttpPauseController(Client(paused), NullLogger<HttpPauseController>.Instance).PauseAsync("決算発表前");
        var r = await new HttpPauseController(Client(resumed), NullLogger<HttpPauseController>.Instance).ResumeAsync("再開");

        (p.Succeeded, p.Paused).Should().Be((true, true));
        (r.Succeeded, r.Paused).Should().Be((true, false));
    }

    // 🔴 T-10-934: 稼働状態（GET /risk-controls/status）。3 統制のうち kill switch と一時停止が成立・段階 1・資金あり。
    [Fact]
    public async Task 稼働状態は送り手の本物の型を直列化した応答から統制と段階とポジションを読める()
    {
        var view = new RiskStatus.RiskStatusView(
            KillSwitchEngaged: true,
            DailyLossLockoutActive: false,
            LockoutReleaseOn: null,
            TradingPaused: true,
            ActiveControl: RiskStatus.ActiveTradingControl.KillSwitch,
            NewEntriesBlocked: true,
            Stage: TradingStage.Stage1Simulate,
            BrokerProvider: BrokerProvider.MoomooSimulate,
            DailyRealizedPnl: -500m,
            UnrealizedPnl: -1_200m,
            DailyPnl: -1_700m,
            Capital: 1_000_000m,
            DailyOrderedAmount: 40_000m,
            MaxOrderAmount: 50_000m,
            MaxDailyOrderAmount: 100_000m,
            DrawdownRatio: 0.05m,
            MaxDrawdownRatio: 0.10m,
            OpenPositionCount: 3,
            MaxOpenPositions: 10);
        var controller = new HttpPauseController(Client(view), NullLogger<HttpPauseController>.Instance);

        var result = await controller.GetStatusAsync();

        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Contain("新規建て=停止中（成立中の統制: kill switch（緊急停止））")
            .And.Contain("統制: kill switch=ON / 日次損失ロックアウト=OFF / 一時停止=ON")
            .And.Contain("段階: Stage 1")
            .And.Contain("ポジション: 3/10 件");
    }

    // 口座を照会できていないときの送り手の値の形（RiskStatusService と同じく、資金が null なら上限の実額も null）。
    private static RiskStatus.RiskStatusView StatusWithCapital(decimal? capital, decimal? maxOrder, decimal? maxDaily) => new(
        KillSwitchEngaged: false,
        DailyLossLockoutActive: false,
        LockoutReleaseOn: null,
        TradingPaused: false,
        ActiveControl: RiskStatus.ActiveTradingControl.None,
        NewEntriesBlocked: false,
        Stage: TradingStage.Stage1Simulate,
        BrokerProvider: BrokerProvider.MoomooSimulate,
        DailyRealizedPnl: -500m,
        UnrealizedPnl: -1_200m,
        DailyPnl: -1_700m,
        Capital: capital,
        DailyOrderedAmount: 40_000m,
        MaxOrderAmount: maxOrder,
        MaxDailyOrderAmount: maxDaily,
        DrawdownRatio: 0.05m,
        MaxDrawdownRatio: 0.10m,
        OpenPositionCount: 3,
        MaxOpenPositions: 10);

    // 🔴 T-10-941: 口座を照会できていない間（資金・上限が null）も稼働状態の照会は成功し、上限を「不明」と表示する（0 と表示しない）。
    [Fact]
    public async Task 資金が未供給の稼働状態は送り手の本物の型を直列化した応答から読めて上限を不明と表示する()
    {
        var controller = new HttpPauseController(
            Client(StatusWithCapital(capital: null, maxOrder: null, maxDaily: null)), NullLogger<HttpPauseController>.Instance);

        var result = await controller.GetStatusAsync();

        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Contain("統制: kill switch=OFF / 日次損失ロックアウト=OFF / 一時停止=OFF")
            .And.Contain("段階: Stage 1")
            .And.Contain($"当日損益: {-1_700m:N0} 円")
            .And.Contain($"日次発注 {40_000m:N0} 円/上限 {HttpPauseController.UnknownDailyOrderCap}")
            .And.Contain("ポジション: 3/10 件");
        // 否定形: 上限を 0 と表示しない（「上限 0」は「上限が分からない」と別の事実）。
        result.Message.Should().NotContain($"/{0m:N0} 円");
    }

    // 🔴 T-10-942: 上限が供給されていれば従来どおり「発注額/上限 円」を表示し、「不明」を出さない。
    [Fact]
    public async Task 上限が供給された稼働状態は送り手の本物の型を直列化した応答から発注額と上限を読める()
    {
        var controller = new HttpPauseController(
            Client(StatusWithCapital(capital: 1_000_000m, maxOrder: 50_000m, maxDaily: 100_000m)), NullLogger<HttpPauseController>.Instance);

        var result = await controller.GetStatusAsync();

        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Contain($"上限使用率: 日次発注 {40_000m:N0}/{100_000m:N0} 円 / DD")
            .And.NotContain("不明");
    }

    // 🔴 T-10-935: GFV 解除（POST /risk-controls/good-faith-violations/clear）。値は送り手の解除の結果の本物の型から作り、
    // 外側は送り手の匿名型と同じ項目名で組む（T-10-938 が送り手の本物の Program.cs で固定する）。受理不能（422）の本文も同様。
    [Fact]
    public async Task GFV_解除の結果は送り手の値と項目名で組んだ応答から対象件数と残件数を読める()
    {
        var outcome = RiskGfv.GoodFaithViolationClearingOutcome.Cleared(["o-1", "o-2"], T0, remainingCount: 1);
        var cleared = new
        {
            clearedOrderIds = outcome.ClearedOrderIds,
            clearedAt = outcome.ClearedAt,
            remainingCount = outcome.RemainingCount,
        };
        var nothing = new { error = "解除できる GFV 違反記録がありません（停止していません）。" };

        var ok = await new HttpGoodFaithViolationController(Client(cleared), NullLogger<HttpGoodFaithViolationController>.Instance)
            .ClearAsync("原因を是正した");
        var ng = await new HttpGoodFaithViolationController(
                Client(nothing, HttpStatusCode.UnprocessableEntity), NullLogger<HttpGoodFaithViolationController>.Instance)
            .ClearAsync("原因を是正した");

        (ok.Succeeded, ok.Cleared).Should().Be((true, true));
        ok.Message.Should().Contain("対象 2 件").And.Contain("1 件が残っており停止は継続します");
        (ng.Succeeded, ng.Cleared).Should().Be((true, false));
        ng.Message.Should().Be(nothing.error);
    }

    // 🔴 T-10-989: 受理（200）は前後の数量・観測・記録した操作者を、送り手の本物の型を直列化した応答から読める。
    // 「実現損益は記録していない」ことを必ず伝える。
    [Fact]
    public async Task 乖離の取り込みの受理は送り手の本物の型を直列化した応答から前後の数量と操作者を読める()
    {
        var adopted = new RiskAdopt.PositionDriftAdoptionResponse(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, LedgerQuantityBefore: 3_381, LedgerQuantityAfter: 0, BrokerQuantity: 0,
            ObservedAt: T0, RealizedPnlRecorded: false, ReferencePrice: null, EstimatedPnlInBase: null,
            AdoptedAt: T0.AddMinutes(1), Actor: "endazon");
        var controller = new HttpPositionDriftAdoptionController(
            Client(adopted), NullLogger<HttpPositionDriftAdoptionController>.Instance);

        var result = await controller.AdoptAsync("AAPL", Market.UnitedStates, "証券会社のアプリで売却した", "endazon");

        (result.Succeeded, result.Adopted).Should().Be((true, true));
        result.Message.Should().Contain("AAPL（米国市場）の建玉を 3381 → 0 へ合わせました")
            .And.Contain("ブローカーの観測 0・観測 2026-09-25 01:00:00Z")
            .And.Contain("操作者 endazon として記録しました")
            .And.Contain("実現損益は記録していません");
        result.Message.Should().NotContain(HttpPositionDriftAdoptionController.ActorUnconfirmed);
    }

    // 🔴 T-10-989: 受理不能（422）は、送り手の本物の型の本文から**拒否の理由の文言をそのまま**利用者へ返し、
    // 台帳が変わっていないことを伝える（観測が古い・乖離が無い・減らす乖離だけ、の 3 つで確かめる）。
    [Theory]
    [InlineData("ブローカ建玉の最新の観測が古すぎます（60 分超）。", "ObservationStale")]
    [InlineData("当該銘柄に取り込む乖離がありません（最新の観測と台帳は一致しています。取り込み済みを含む）。", "NoDrift")]
    [InlineData("取り込めるのは台帳の建玉を減らす乖離だけです。", "UnsupportedDirection")]
    public async Task 乖離の取り込みの拒否は送り手の本物の型から理由の文言を読める(string error, string code)
    {
        var body = new RiskAdopt.PositionDriftAdoptionRejectionBody(error, code);
        var controller = new HttpPositionDriftAdoptionController(
            Client(body, HttpStatusCode.UnprocessableEntity), NullLogger<HttpPositionDriftAdoptionController>.Instance);

        var result = await controller.AdoptAsync("AAPL", Market.UnitedStates, "理由", "endazon");

        (result.Succeeded, result.Adopted).Should().Be((true, false));
        result.Message.Should().Be($"取り込みは行いませんでした（台帳は変わっていません）: {error}");
    }

    // 🔴 T-10-989（逆向き）: Bot が送る本文は、送り手の本物の要求型で読むと銘柄・市場・理由・**代理される利用者**が
    // そのまま届く（`onBehalfOf` の改名で操作者が黙って `client:<azp>` へ落ちない）。数量は送らない。
    [Fact]
    public async Task 取り込みの要求本文は送り手の本物の要求型で銘柄と市場と理由と操作者を読める()
    {
        var capture = new CapturingHandler();
        var controller = new HttpPositionDriftAdoptionController(
            new HttpClient(capture) { BaseAddress = new Uri("http://risk-management-service") },
            NullLogger<HttpPositionDriftAdoptionController>.Instance);

        await controller.AdoptAsync("7203", Market.Japan, "証券会社のアプリで売却した", "endazon");

        capture.Path.Should().Be("/risk-controls/position-drift/adopt");
        var request = JsonSerializer.Deserialize<RiskAdopt.PositionDriftAdoptionRequest>(capture.Body, Web)!;
        request.Should().Be(new RiskAdopt.PositionDriftAdoptionRequest("7203", Market.Japan, "証券会社のアプリで売却した", "endazon"));
        using var doc = JsonDocument.Parse(capture.Body);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["symbol", "market", "reason", "onBehalfOf"]);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    // 要求本文を捕まえ、422（受理不能）で応える。
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent("""{"error":"x","code":"NoDrift"}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
