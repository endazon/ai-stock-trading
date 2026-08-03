using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, FR-19, FR-20, UC-06, ADR-0007: 設定変更の検証。利用者のみ・変更履歴（前後値つき）の記録・現行値の更新。
public class RiskSettingsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 6, 0, 0, TimeSpan.Zero);

    private static (RiskSettingsService Service, InMemoryRiskSettingsStore Store, InMemorySettingsChangeLog Log)
        Create()
    {
        var store = new InMemoryRiskSettingsStore();
        var log = new InMemorySettingsChangeLog();
        var clock = new FakeClock(Now, new DateOnly(2026, 7, 9));
        return (new RiskSettingsService(store, log, clock), store, log);
    }

    [Fact]
    public void 上限変更は現行値を更新し前後値つきで履歴に残す()
    {
        var (service, store, log) = Create();
        var before = store.GetCurrent().Limits;
        var updated = before with { MaxOpenPositions = 5 };

        service.UpdateLimits(updated, "user", "検証結果に基づき保有数上限を緩和");

        store.GetCurrent().Limits.MaxOpenPositions.Should().Be(5);
        var entry = log.GetHistory().Should().ContainSingle().Subject;
        entry.ChangeType.Should().Be(SettingsChangeType.Limits);
        entry.Actor.Should().Be("user");
        entry.Before.Should().Be(before.ToString());
        entry.After.Should().Be(updated.ToString());
    }

    [Fact]
    public void 段階変更は現行値を更新し履歴に残す()
    {
        // FR-20: 段階ゲートの昇格は利用者承認で行う。
        var (service, store, log) = Create();
        var stage = new StageSettings(TradingStage.Stage1Paper, Shared.Contracts.Trading.TradeMode.Paper, 100_000m);

        service.UpdateStage(stage, "user", "Stage0 の合格基準を満たしたため昇格");

        store.GetCurrent().Stage.Stage.Should().Be(TradingStage.Stage1Paper);
        log.GetHistory().Should().ContainSingle(e => e.ChangeType == SettingsChangeType.Stage);
    }

    [Fact]
    public void ガード変更は現行値を更新し履歴に残す()
    {
        var (service, store, log) = Create();
        var guard = store.GetCurrent().Guard with { PreventSameDayReentry = false };

        service.UpdateGuard(guard, "user", "差金決済防止を一時的に無効化");

        store.GetCurrent().Guard.PreventSameDayReentry.Should().BeFalse();
        log.GetHistory().Should().ContainSingle(e => e.ChangeType == SettingsChangeType.Guard);
    }

    [Fact]
    public void アクターまたは理由が空なら変更を拒否する()
    {
        // ADR-0007: アクター・理由のない変更は受け付けない。
        var (service, store, _) = Create();
        var limits = store.GetCurrent().Limits;

        var act = () => service.UpdateLimits(limits, "", "理由");

        act.Should().Throw<ArgumentException>();
    }
}
