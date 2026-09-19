using System.Text.RegularExpressions;
using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-12, FR-11, UC-06, SC-02, ADR-0040 決定1・決定3, #819, IADR-0342 決定2・決定3:
// 損切りの実行機構の設定点（サービス・純関数・永続化・承認への搭載）の検証。
//
// 受け入れ基準:
//   1.  既定は S0 であり、旧い設定行も S0 として読まれる（現行挙動が変わらない）
//   2a. 実弾（moomoo REAL）で S0 以外への変更は拒否され、設定も履歴も変わらない
//       ＋ S0 以外が有効なまま実弾へは切り替えられない（逆方向）
//   7.  承認に現在の手法が載る
public class StopLossMethodSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 6, 0, 0, TimeSpan.Zero);

    private static (RiskSettingsService Service, InMemoryRiskSettingsStore Store, InMemorySettingsChangeLog Log)
        Create(RiskManagementSettings? initial = null)
    {
        var store = new InMemoryRiskSettingsStore(initial);
        var log = new InMemorySettingsChangeLog();
        var clock = new FakeClock(Now, new DateOnly(2026, 9, 17));
        return (new RiskSettingsService(store, log, clock, FakeBrokerAccountObservations.NotObserved()), store, log);
    }

    private static RiskManagementSettings WithProvider(BrokerProvider provider) =>
        TradingDefaults.CreateSettings() with { BrokerProvider = provider };

    // ---- 既定 ----

    [Fact]
    public void 損切りの実行機構の既定はS0である()
    {
        TradingDefaults.CreateSettings().StopLossMethod.Should().Be(StopLossExecutionMethod.BrokerStopOrder);
        new InMemoryRiskSettingsStore().GetCurrent().StopLossMethod.Should().Be(StopLossExecutionMethod.BrokerStopOrder);
    }

    // ---- 変更（肯定形） ----

    [Theory]
    [InlineData(BrokerProvider.MoomooSimulate, StopLossExecutionMethod.NoProtectiveStop)]
    [InlineData(BrokerProvider.MoomooSimulate, StopLossExecutionMethod.SoftwareStop)]
    [InlineData(BrokerProvider.MoomooSimulate, StopLossExecutionMethod.AlternativeBrokerOrderType)]
    [InlineData(BrokerProvider.InternalPaper, StopLossExecutionMethod.NoProtectiveStop)]
    [InlineData(BrokerProvider.MoomooReal, StopLossExecutionMethod.BrokerStopOrder)]
    public void 実弾以外ではS0からS3を選べ前後値と理由つきで履歴に残る(
        BrokerProvider provider, StopLossExecutionMethod target)
    {
        var (service, store, log) = Create(WithProvider(provider));

        var rejections = service.UpdateStopLossMethod(target, "endazon", "PoC で建玉を残して観測する");

        rejections.Should().BeEmpty();
        store.GetCurrent().StopLossMethod.Should().Be(target);
        store.GetCurrent().BrokerProvider.Should().Be(provider, "発注先には触れない");
        var entry = log.GetHistory().Should().ContainSingle().Which;
        entry.ChangeType.Should().Be(SettingsChangeType.StopLossMethodChanged);
        entry.Actor.Should().Be("endazon");
        entry.Reason.Should().Be("PoC で建玉を残して観測する");
        entry.Before.Should().Be(nameof(StopLossExecutionMethod.BrokerStopOrder));
        entry.After.Should().Be(target.ToString());
        entry.ChangedAt.Should().Be(Now);
    }

    // ---- 否定形: 実弾では S0 以外を選べない ----

    [Theory]
    [InlineData(StopLossExecutionMethod.SoftwareStop)]
    [InlineData(StopLossExecutionMethod.NoProtectiveStop)]
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType)]
    public void 実弾ではS0以外への変更は拒否され設定も履歴も変わらない(StopLossExecutionMethod target)
    {
        var (service, store, log) = Create(WithProvider(BrokerProvider.MoomooReal));

        var rejections = service.UpdateStopLossMethod(target, "endazon", "実弾でも試したい");

        rejections.Should().Contain(StopLossMethodChangeRejection.NotPermittedOnLive);
        store.GetCurrent().StopLossMethod.Should().Be(StopLossExecutionMethod.BrokerStopOrder);
        log.GetHistory().Should().BeEmpty("拒否された要求を履歴に積むと、起きていない変更が監査上の事実になる");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 理由が空の変更は拒否され設定も履歴も変わらない(string? reason)
    {
        var (service, store, log) = Create(WithProvider(BrokerProvider.MoomooSimulate));

        service.UpdateStopLossMethod(StopLossExecutionMethod.NoProtectiveStop, "endazon", reason)
            .Should().Contain(StopLossMethodChangeRejection.ReasonRequired);

        store.GetCurrent().StopLossMethod.Should().Be(StopLossExecutionMethod.BrokerStopOrder);
        log.GetHistory().Should().BeEmpty();
    }

    [Fact]
    public void 未知の手法は拒否される()
    {
        var (service, store, _) = Create(WithProvider(BrokerProvider.MoomooSimulate));

        service.UpdateStopLossMethod((StopLossExecutionMethod)7, "endazon", "検証")
            .Should().Contain(StopLossMethodChangeRejection.UnknownMethod);
        store.GetCurrent().StopLossMethod.Should().Be(StopLossExecutionMethod.BrokerStopOrder);
    }

    // ---- 否定形: S0 以外が有効なまま実弾へ切り替えない（逆方向） ----

    [Theory]
    [InlineData(StopLossExecutionMethod.SoftwareStop)]
    [InlineData(StopLossExecutionMethod.NoProtectiveStop)]
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType)]
    public void S0以外が有効なまま実弾へは確認操作が揃っていても切り替えられない(StopLossExecutionMethod active)
    {
        var (service, store, log) = Create(
            WithProvider(BrokerProvider.MoomooSimulate) with { StopLossMethod = active });

        var assessment = service.UpdateBrokerProvider(
            new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "実弾へ移行する", true, "REAL"), "endazon");

        assessment.Accepted.Should().BeFalse();
        assessment.Rejections.Should().ContainSingle()
            .Which.Should().Be(BrokerProviderChangeRejection.StopLossMethodNotBrokerStop);
        store.GetCurrent().BrokerProvider.Should().Be(BrokerProvider.MoomooSimulate);
        log.GetHistory().Should().BeEmpty();
    }

    [Theory]
    [InlineData(BrokerProvider.InternalPaper)]
    [InlineData(BrokerProvider.MoomooSimulate)]
    public void S0以外が有効でも実弾以外への切替は妨げない(BrokerProvider target)
    {
        var (service, store, _) = Create(
            WithProvider(BrokerProvider.MoomooSimulate) with { StopLossMethod = StopLossExecutionMethod.NoProtectiveStop });

        service.UpdateBrokerProvider(new BrokerProviderChangeRequest(target, "切替"), "endazon")
            .Accepted.Should().BeTrue();
        store.GetCurrent().BrokerProvider.Should().Be(target);
    }

    [Fact]
    public void S0なら確認操作つきで実弾へ切り替えられる_従来どおり()
    {
        var (service, store, _) = Create(WithProvider(BrokerProvider.MoomooSimulate));

        service.UpdateBrokerProvider(
                new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "実弾へ移行する", true, "REAL"), "endazon")
            .Accepted.Should().BeTrue();
        store.GetCurrent().BrokerProvider.Should().Be(BrokerProvider.MoomooReal);
    }

    [Fact]
    public void 手法を渡さない発注先の判定は従来と同一である()
    {
        var stage = new StageSettings(
            TradingStage.Stage2MinimalLive, BrokerProvider.MoomooReal, TradingDefaults.Stage2MinimalLiveCapitalCapRatio);
        var request = new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "実弾へ移行する", true, "REAL");

        BrokerProviderChange.Evaluate(request, stage).Accepted.Should().BeTrue();
    }

    // ---- 永続化（単一行 JSON） ----

    [Theory]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder)]
    [InlineData(StopLossExecutionMethod.SoftwareStop)]
    [InlineData(StopLossExecutionMethod.NoProtectiveStop)]
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType)]
    public void 手法は保存と読み出しで往復する(StopLossExecutionMethod method)
    {
        var settings = TradingDefaults.CreateSettings() with { StopLossMethod = method };

        RiskSettingsSerialization.Deserialize(RiskSettingsSerialization.Serialize(settings))
            .StopLossMethod.Should().Be(method);
    }

    [Fact]
    public void 手法を持たない旧行はS0として読まれる()
    {
        var legacy = RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings());
        legacy = Regex.Replace(legacy, ",\"stopLossMethod\":\\d+", string.Empty);
        legacy.Should().NotContain("stopLossMethod", "旧行の再現に失敗している（キーが残っていると検証にならない）");

        RiskSettingsSerialization.Deserialize(legacy).StopLossMethod.Should().Be(StopLossExecutionMethod.BrokerStopOrder);
    }

    // 否定形: 読めない値は「免除（S2）」ではなく S0 へ倒れる。
    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(99)]
    public void 未知の序数の永続値はS0として読まれる(int persisted)
    {
        var json = RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings());
        json = Regex.Replace(json, "\"stopLossMethod\":-?\\d+", $"\"stopLossMethod\":{persisted}");
        json.Should().Contain($"\"stopLossMethod\":{persisted}", "書き換えに失敗している");

        RiskSettingsSerialization.Deserialize(json).StopLossMethod.Should().Be(StopLossExecutionMethod.BrokerStopOrder);
    }

    // ---- 承認への搭載 ----

    [Theory]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder)]
    [InlineData(StopLossExecutionMethod.NoProtectiveStop)]
    public void 承認は審査時点で有効な損切りの実行機構を運ぶ(StopLossExecutionMethod method)
    {
        var clock = new FakeClock(Now, new DateOnly(2026, 9, 17));
        var portfolio = new FakePortfolioStateProvider(new PortfolioState
        {
            LedgerEquity = 100_000m,
            OpenPositionCount = 0,
            InvestedCapital = 0m,
            DailyOrderedAmount = 0m,
            DailyRealizedPnl = 0m,
            UnrealizedPnl = 0m,
        });
        var builder = new PortfolioSnapshotBuilder(
            portfolio, new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            FakeBrokerAccountObservations.NotObserved(), FakeInformationDegradation.Affirmed(), capitalBaseline: FakeCapitalBaseline.Of(100_000m));
        var settings = new InMemoryRiskSettingsStore(TradingDefaults.CreateSettings() with { StopLossMethod = method });
        var service = new OrderScreeningService(
            settings, builder, new InMemoryLockoutStore(), clock, new WeekendBusinessCalendar(),
            new InMemoryBuyInInferenceStore());
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m,
            PositionEffect.Open);

        var outcome = service.Screen(new TradeDecisionMade(Guid.NewGuid(), intent, "テスト判断", Now));

        outcome.IsApproved.Should().BeTrue();
        outcome.Approved!.StopLossMethod.Should().Be(method);
    }
}
