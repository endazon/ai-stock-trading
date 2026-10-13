using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RiskManagementService.Tests;

// FR-11, UC-06, #1291, IADR-0532, IADR-0134 決定2: 設定の変更履歴の種別の応答上の整数値と、設定の保存と履歴の記録を
// 1 つのトランザクションにする配線（ロールバックそのものは実 DB の統合試験 T-10-2615 が固定する）。
public class SettingsChangeHistoryWiringTests(RiskWorkerWebApplicationFactory factory)
    : IClassFixture<RiskWorkerWebApplicationFactory>
{
    // T-10-2612, FR-11, IADR-0134 決定2: 種別は HTTP 応答で整数として往来し、画面（contracts.ts の CHANGE_TYPE_LABELS）と
    // 運用者の集計（#1291 の PoC）がその整数で読む。**全メンバの値を固定する**（間へ挿入すると過去の行の種別が黙って変わる。追加は末尾へ）。
    // HighVolatilityChanged は 11 番目のメンバで、値は 0 始まりの **10** である（PoC は 11 で数えて 0 件と読んだ）。
    [Fact]
    public void T_10_2612_履歴の種別の整数値は全メンバで固定されている()
    {
        var expected = new Dictionary<SettingsChangeType, int>
        {
            [SettingsChangeType.Guard] = 0,
            [SettingsChangeType.Limits] = 1,
            [SettingsChangeType.Stage] = 2,
            [SettingsChangeType.KillSwitchEngaged] = 3,
            [SettingsChangeType.KillSwitchDisengaged] = 4,
            [SettingsChangeType.TradingPaused] = 5,
            [SettingsChangeType.TradingResumed] = 6,
            [SettingsChangeType.BrokerProviderChanged] = 7,
            [SettingsChangeType.Stage1MinimumTradeCountChanged] = 8,
            [SettingsChangeType.StopLossMethodChanged] = 9,
            [SettingsChangeType.HighVolatilityChanged] = 10,
        };

        Enum.GetValues<SettingsChangeType>().Should().BeEquivalentTo(expected.Keys, "メンバを足したらここへ値を足す（末尾へ）");
        foreach (var (type, value) in expected)
            ((int)type).Should().Be(value, $"{type} の値");
    }

    // 🔴 T-10-2613, FR-11, #1291, IADR-0532 決定1: 本番の配線で、設定の保存と履歴の記録のトランザクションは EF 実装に解決される。
    // RiskSettingsService は省略時に本体をそのまま走らせる（単体試験の直接組み立て用）ため、登録が抜けると黙って原子性を失う。
    [Fact]
    public void T_10_2613_設定の書き込みのトランザクションはEF実装に解決される()
    {
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ISettingsWriteTransaction>().Should().BeOfType<EfSettingsWriteTransaction>();
        scope.ServiceProvider.GetRequiredService<RiskSettingsService>().Should().NotBeNull();
    }

    // T-10-2613 の補い: トランザクションを持たないプロバイダ（InMemory）では本体をそのまま走らせ、例外は呼び出し側へ透過する。
    [Fact]
    public void T_10_2613_関係DBでないプロバイダでは本体をそのまま走らせ例外を透過する()
    {
        using var scope = factory.Services.CreateScope();
        var transaction = scope.ServiceProvider.GetRequiredService<ISettingsWriteTransaction>();

        var ran = false;
        transaction.Execute(() => ran = true);
        ran.Should().BeTrue();

        var act = () => transaction.Execute(() => throw new InvalidOperationException("本体の失敗"));
        act.Should().Throw<InvalidOperationException>().WithMessage("本体の失敗");
    }
}
