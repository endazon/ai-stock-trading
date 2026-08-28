using System.Text.RegularExpressions;
using RiskManagementService.Domain;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Infrastructure.Tests;

// FR-19, UC-06, #375, ADR-0021 決定1/決定3, IADR-0153:
// 設定ストア（単一行 JSON）における**利用者が設定した口座種別**の往復と、**旧行の読み方**の検証。
//
// 本値は統制の切り替えには使われない（統制は照会結果で切り替わる）。役割は**照会結果との食い違いの検知**
// だけである。したがって「旧行を既定＝信用口座として読む」ことは統制を緩めない——
// 照会結果が無ければ新規建ては `BrokerAccountTypeUnverified` で止まる。
public class RiskSettingsSerializationAccountTypeTests
{
    [Theory]
    [InlineData(AccountType.Margin)]
    [InlineData(AccountType.Cash)]
    public void 設定した口座種別は保存と読み出しで往復する(AccountType accountType)
    {
        var settings = TradingDefaults.CreateSettings();
        settings = settings with { Guard = settings.Guard with { ConfiguredAccountType = accountType } };

        var restored = RiskSettingsSerialization.Deserialize(RiskSettingsSerialization.Serialize(settings));

        restored.Guard.ConfiguredAccountType.Should().Be(accountType);
    }

    [Fact]
    public void すべての口座種別が往復する()
    {
        foreach (var accountType in Enum.GetValues<AccountType>())
        {
            var settings = TradingDefaults.CreateSettings();
            settings = settings with { Guard = settings.Guard with { ConfiguredAccountType = accountType } };

            RiskSettingsSerialization
                .Deserialize(RiskSettingsSerialization.Serialize(settings))
                .Guard.ConfiguredAccountType.Should().Be(accountType);
        }
    }

    // FR-19, ADR-0021 決定1: 口座種別を持たない旧行（#375 より前に書かれた行）は**信用口座**として読む。
    //
    // これは「照会できなければ信用口座として扱う」ことではない。旧行が復元されるのは**設定値**であり、
    // 統制は照会結果で切り替わる。現金口座を実際に使っている利用者の旧行なら、照会結果（Cash）と
    // 復元値（Margin）が食い違い、**新規建てが止まる**（決定3 が意図した挙動そのものである）。
    [Fact]
    public void 口座種別を持たない旧行は信用口座として読まれる()
    {
        var legacy = RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings());
        legacy = Regex.Replace(legacy, ",\"configuredAccountType\":\\d+", string.Empty);
        legacy.Should().NotContain(
            "configuredAccountType", "旧行の再現に失敗している（キーが残っていると検証にならない）");

        var restored = RiskSettingsSerialization.Deserialize(legacy);

        restored.Guard.ConfiguredAccountType.Should().Be(AccountType.Margin);
    }

    // 序数は永続化と結びついている。**値の並べ替え・挿入を行わないこと**（ProductType と同じ規律）。
    [Theory]
    [InlineData(AccountType.Margin, 0)]
    [InlineData(AccountType.Cash, 1)]
    public void 口座種別の序数は不変である(AccountType accountType, int expectedOrdinal) =>
        ((int)accountType).Should().Be(
            expectedOrdinal,
            "序数は永続化（リスク管理設定 JSON）と結びついており、並べ替えると既存行の意味が変わる");
}
