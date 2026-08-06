using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-19, UC-06, #375, ADR-0021 決定4-4, IADR-0153: **現金口座では信用買い・空売りを「選べなくする」。**
//
// 計画は「設定不能化」と述べており、「設定はできるが発注はできない」状態を作らない。発注側の遮断
//（RiskEvaluator の ProductTypeDisabled）とは**別の層**であり、両方が要る（多層防御）——
// 設定側だけでは API を直に叩く経路が残り、発注側だけでは画面上「有効」に見える設定が残る。
public class CashAccountSettingsGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 6, 0, 0, TimeSpan.Zero);

    private static (RiskSettingsService Service, InMemoryRiskSettingsStore Store, InMemorySettingsChangeLog Log)
        Create(FakeBrokerAccountObservations observations)
    {
        var store = new InMemoryRiskSettingsStore();
        var log = new InMemorySettingsChangeLog();
        var clock = new FakeClock(Now, new DateOnly(2026, 8, 6));
        return (new RiskSettingsService(store, log, clock, observations), store, log);
    }

    private static TradingGuardSettings GuardWith(params ProductType[] productTypes) =>
        TradingDefaults.CreateGuardSettings() with
        {
            EnabledProductTypes = new HashSet<ProductType>(productTypes),
        };

    // **否定形（中核）**: 現金口座を観測している状態で信用買い・空売りを有効化しようとしたら 400 で弾く。
    [Theory]
    [InlineData(ProductType.MarginLong)]
    [InlineData(ProductType.ShortSell)]
    public void 現金口座では信用買いと空売りを有効化できない(ProductType unsupported)
    {
        var (service, _, _) = Create(FakeBrokerAccountObservations.Cash());

        var act = () => service.UpdateGuard(
            GuardWith(ProductType.Cash, unsupported), "user", "検証のため信用系を有効化する");

        act.Should().Throw<ArgumentException>();
    }

    // **否定形**: 拒否した要求は設定も履歴も一切変えない（IADR-0141 / IADR-0151 決定2 と同じ規律）。
    // 拒否された要求を履歴に積むと、実際には起きていない変更が監査上の事実になる。
    [Fact]
    public void 拒否された設定変更は現行値も変更履歴も変えない()
    {
        var (service, store, log) = Create(FakeBrokerAccountObservations.Cash());
        var before = store.GetCurrent().Guard;

        var act = () => service.UpdateGuard(
            GuardWith(ProductType.Cash, ProductType.ShortSell), "user", "空売りを有効化する");

        act.Should().Throw<ArgumentException>();
        store.GetCurrent().Guard.Should().Be(before);
        log.GetHistory().Should().BeEmpty();
    }

    // 現物だけなら現金口座でも通る（上の否定形が「常に throw する」ことの裏返しでないことを固定する）。
    [Fact]
    public void 現金口座でも現物のみの設定は保存できる()
    {
        var (service, store, log) = Create(FakeBrokerAccountObservations.Cash());

        service.UpdateGuard(GuardWith(ProductType.Cash), "user", "現物のみへ絞る");

        store.GetCurrent().Guard.EnabledProductTypes.Should().BeEquivalentTo([ProductType.Cash]);
        log.GetHistory().Should().ContainSingle();
    }

    // 信用口座では 3 種すべて設定できる（#375 が信用口座の運用を狭めていないこと）。
    [Fact]
    public void 信用口座では信用買いと空売りを有効化できる()
    {
        var (service, store, _) = Create(FakeBrokerAccountObservations.Margin());

        service.UpdateGuard(
            GuardWith(ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell),
            "user",
            "Stage 3 の検証のため信用系を有効化する");

        store.GetCurrent().Guard.EnabledProductTypes.Should().HaveCount(3);
    }

    // FR-19, ADR-0021 決定3, IADR-0153: 口座種別を照会できていないときは**設定側では制限しない**。
    //
    // 設定の保存は発注そのものではない。観測が無い状態で設定変更まで止めると「統制を厳しくする方向の変更」
    // （空売りを無効化する等）も道連れに止まる。**安全性は発注側が担保している**——同じ状態で
    // 新規建ては `BrokerAccountTypeUnverified` で止まっており、設定を保存できても発注はできない。
    [Fact]
    public void 口座種別を照会できていなければ設定側では制限しない()
    {
        var (service, store, _) = Create(FakeBrokerAccountObservations.NotObserved());

        service.UpdateGuard(
            GuardWith(ProductType.Cash, ProductType.ShortSell), "user", "空売りを有効化する");

        store.GetCurrent().Guard.EnabledProductTypes.Should().Contain(ProductType.ShortSell);
    }

    // 口座種別の設定値そのものは保存・往復できる（決定3 の食い違い検知の入力になる値）。
    [Fact]
    public void 設定した口座種別は保存され現行値に反映される()
    {
        var (service, store, _) = Create(FakeBrokerAccountObservations.NotObserved());

        service.UpdateGuard(
            GuardWith(ProductType.Cash) with { ConfiguredAccountType = AccountType.Cash },
            "user",
            "米国口座を現金口座へ切り替えた");

        store.GetCurrent().Guard.ConfiguredAccountType.Should().Be(AccountType.Cash);
    }
}
