using System.Text.RegularExpressions;
using RiskManagementService.Domain;
using RiskManagementService.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-20, FR-13, SC-02, #423, IADR-0164 決定5:
// 設定ストア（単一行 JSON）における **Stage 1 の最小取引件数**の往復と、**旧行・値域外の読み方**の検証。
//
// 設定は 1 行の JSON として永続化される。最小取引件数は #423 で追加した項目であり、
// **それ以前に書かれた行には存在しない**。読めない項目に既定を与えること自体は避けられないが、
// **その既定が「少ない件数」であってはならない**——少ない件数は「合格に近い」＝統制が緩む側であり、
// しかも画面には正常な設定として現れる（発注先の「読めない行は実弾」と同型の危険である）。
public class RiskSettingsSerializationStage1TradeCountTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(100)]
    [InlineData(1000)]
    public void 最小取引件数は保存と読み出しで往復する(int value)
    {
        var settings = TradingDefaults.CreateSettings() with { Stage1MinimumTradeCount = value };

        var restored = RiskSettingsSerialization.Deserialize(RiskSettingsSerialization.Serialize(settings));

        restored.Stage1MinimumTradeCount.Should().Be(value);
    }

    // 否定形（フェイルクローズ）: 本項目を持たない旧行は**既定 100**として読む。
    [Fact]
    public void 最小取引件数を持たない旧行は既定100として読まれる()
    {
        // #423 より前に書かれた行を模す（stage1MinimumTradeCount キーが無い）。
        var legacy = RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings());
        legacy = Regex.Replace(legacy, ",\"stage1MinimumTradeCount\":\\d+", string.Empty);
        legacy.Should().NotContain(
            "stage1MinimumTradeCount", "旧行の再現に失敗している（キーが残っていると検証にならない）");

        var restored = RiskSettingsSerialization.Deserialize(legacy);

        restored.Stage1MinimumTradeCount.Should().Be(100);
    }

    // 否定形: **値域外の永続値は既定 100 へ落とす**（下限 1 へ丸めない）。
    // 手編集・不整合な移行で 0 や 5 のような値が入り込んでも、擬似的に合格へ近づけない。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    [InlineData(2147483647)]
    public void 値域外の永続値は既定100として読まれる(int persisted)
    {
        var json = RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings());
        json = Regex.Replace(
            json, "\"stage1MinimumTradeCount\":-?\\d+", $"\"stage1MinimumTradeCount\":{persisted}");
        json.Should().Contain($"\"stage1MinimumTradeCount\":{persisted}", "書き換えに失敗している");

        var restored = RiskSettingsSerialization.Deserialize(json);

        restored.Stage1MinimumTradeCount.Should().Be(
            100, "読めない値は厳しい側（合格に遠い既定）へ倒す。少ない件数へ倒れてはならない");
    }

    // 最小取引件数の変更が**他の設定を巻き込まない**（`with` が他プロパティを保つことの往復確認）。
    [Fact]
    public void 最小取引件数の保存は他の設定を巻き込まない()
    {
        var baseline = TradingDefaults.CreateSettings();
        var settings = baseline with { Stage1MinimumTradeCount = 42 };

        var restored = RiskSettingsSerialization.Deserialize(RiskSettingsSerialization.Serialize(settings));

        restored.Stage1MinimumTradeCount.Should().Be(42);
        restored.BrokerProvider.Should().Be(baseline.BrokerProvider);
        restored.Stage.Should().Be(baseline.Stage);
        restored.Limits.Should().Be(baseline.Limits);
        restored.ShortSell.Should().Be(baseline.ShortSell);
    }
}
