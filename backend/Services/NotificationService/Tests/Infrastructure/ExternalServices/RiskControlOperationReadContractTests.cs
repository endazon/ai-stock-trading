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

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
