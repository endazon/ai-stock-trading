using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-20, FR-12, #334, IADR-0140: 設定ストア（単一行 JSON）における発注先の往復と、**旧行の読み方**の検証。
//
// 設定は 1 行の JSON として永続化される。発注先は #334 で追加した項目であり、
// **それ以前に書かれた行には存在しない**。読めない項目を enum の既定値へ落とすこと自体は避けられないが、
// その既定値が「実弾」であってはならない——「読めない行は実弾」に倒れる移行は取り返しがつかない。
public class RiskSettingsSerializationBrokerProviderTests
{
    [Fact]
    public void 発注先は保存と読み出しで往復する()
    {
        var settings = TradingDefaults.CreateSettings() with { BrokerProvider = BrokerProvider.MoomooSimulate };

        var restored = RiskSettingsSerialization.Deserialize(RiskSettingsSerialization.Serialize(settings));

        restored.BrokerProvider.Should().Be(BrokerProvider.MoomooSimulate);
    }

    [Fact]
    public void すべての発注先が往復する()
    {
        foreach (var provider in Enum.GetValues<BrokerProvider>())
        {
            var settings = TradingDefaults.CreateSettings() with { BrokerProvider = provider };

            RiskSettingsSerialization
                .Deserialize(RiskSettingsSerialization.Serialize(settings))
                .BrokerProvider.Should().Be(provider);
        }
    }

    // 否定形（フェイルクローズ）: 発注先を持たない旧行は**内蔵 paper** として読む。実弾に倒れない。
    [Fact]
    public void 発注先を持たない旧行は内蔵paperとして読まれる()
    {
        // #334 より前に書かれた行を模す（brokerProvider キーが無い）。
        var legacy = RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings());
        legacy = System.Text.RegularExpressions.Regex.Replace(
            legacy, ",\"brokerProvider\":\\d+", string.Empty);
        legacy.Should().NotContain("brokerProvider", "旧行の再現に失敗している（キーが残っていると検証にならない）");

        var restored = RiskSettingsSerialization.Deserialize(legacy);

        restored.BrokerProvider.Should().Be(BrokerProvider.InternalPaper);
        restored.BrokerProvider.Should().NotBe(
            BrokerProvider.MoomooReal,
            "読めない行が実弾に倒れる移行は取り返しがつかない（IADR-0140 決定4）");
    }

    // 段階の Mode（既定の発注先）は**旧 TradeMode の序数をそのまま読む**。名前も序数も据え置いたため、
    // #334 以前に書かれた行の "mode": 0 / 1 は内蔵 paper / 実弾として正しく復元される（IADR-0140 決定2・3）。
    [Theory]
    [InlineData(0, BrokerProvider.InternalPaper)]
    [InlineData(1, BrokerProvider.MoomooReal)]
    public void 旧TradeModeの序数を積んだ段階設定は同じ意味で読まれる(int legacyOrdinal, BrokerProvider expected)
    {
        var json = RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings());
        var legacy = System.Text.RegularExpressions.Regex.Replace(
            json, "\"mode\":\\d+", $"\"mode\":{legacyOrdinal}");

        RiskSettingsSerialization.Deserialize(legacy).Stage.Mode.Should().Be(expected);
    }
}
