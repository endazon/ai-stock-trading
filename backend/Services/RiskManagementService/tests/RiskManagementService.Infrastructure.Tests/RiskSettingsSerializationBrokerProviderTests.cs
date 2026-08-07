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

    // FR-20 (3), #422, IADR-0161: **allow-list**（3 値の明示一致のみ）で読む。
    // 「本項目を持たない旧行」だけを見る `?? 既定` は deny-list であり、**未知の値を素通しにする**
    // （未知の序数はそのまま流れ、文字列・真偽値は JsonException で設定行全体が読めなくなる）。
    // 計画（FR-20 の 2026-08-07 追記 (3)）が名指しする「読めない行は実弾」の否定形を、値の形ごとに固定する。
    [Theory]
    [InlineData("7")]                 // 未知の序数
    [InlineData("3")]                 // 直近の未使用序数（4 値目が足された日に意味が変わる値）
    [InlineData("-1")]
    [InlineData("2147483648")]        // int に収まらない数値
    [InlineData("null")]              // 明示的な null
    [InlineData("\"MOOMOO REAL\"")]   // 未知の文字列
    [InlineData("\"moomooReal\"")]    // 大小文字違い（Ordinal で弾く）
    [InlineData("\"REAL\"")]
    [InlineData("\"\"")]              // 空文字
    [InlineData("true")]              // 別の型（真偽値）
    [InlineData("{}")]                // 別の型（オブジェクト）
    [InlineData("[1]")]               // 別の型（配列）
    public void 解決できない発注先の値は内蔵paperとして読まれる(string rawJsonValue)
    {
        var json = WithBrokerProviderValue(rawJsonValue);

        // 例外を投げないこと自体が要件である——投げると設定行**全体**（統制値・ガード・段階）が失われ、
        // リスク判定そのものが動かなくなる。計画は「同じ既定へ落とす」と明言している。
        var restored = RiskSettingsSerialization.Deserialize(json);

        restored.BrokerProvider.Should().Be(
            BrokerProvider.InternalPaper,
            "brokerProvider={0} は allow-list に無い（FR-20 (3)・IADR-0161）",
            rawJsonValue);
        restored.BrokerProvider.Should().NotBe(BrokerProvider.MoomooReal);
        // 巻き添えで他の設定が失われていないことも確かめる（例外へ倒していない証拠）。
        restored.Limits.MaxOpenPositions.Should().Be(TradingDefaults.CreateRiskLimits().MaxOpenPositions);
    }

    // 肯定形の裏面: 正準名の文字列で書かれた行は正しく読む（allow-list が「読めるものは読む」ことの確認）。
    [Theory]
    [InlineData("\"MoomooReal\"", BrokerProvider.MoomooReal)]
    [InlineData("\"MoomooSimulate\"", BrokerProvider.MoomooSimulate)]
    [InlineData("\"InternalPaper\"", BrokerProvider.InternalPaper)]
    public void 正準名の文字列で書かれた行は明示一致で読まれる(string rawJsonValue, BrokerProvider expected)
    {
        RiskSettingsSerialization
            .Deserialize(WithBrokerProviderValue(rawJsonValue))
            .BrokerProvider.Should().Be(expected);
    }

    // 書き込み側のワイヤ形式は**数値のまま**である（旧版が読めなくなる変更をしない）。
    [Fact]
    public void 発注先は数値の序数として書き出される()
    {
        var json = RiskSettingsSerialization.Serialize(
            TradingDefaults.CreateSettings() with { BrokerProvider = BrokerProvider.MoomooSimulate });

        json.Should().Contain("\"brokerProvider\":2");
    }

    // 既定の設定（新規インストール相当）の直列化結果が内蔵 paper であること。
    [Fact]
    public void 新規インストールの初期値は内蔵paperである()
    {
        RiskSettingsSerialization
            .Deserialize(RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings()))
            .BrokerProvider.Should().Be(BrokerProvider.InternalPaper);
    }

    // brokerProvider の値だけを差し替えた行を作る（他の項目は既定のまま）。
    private static string WithBrokerProviderValue(string rawJsonValue)
    {
        var json = RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings());
        var replaced = System.Text.RegularExpressions.Regex.Replace(
            json, "\"brokerProvider\":\\d+", $"\"brokerProvider\":{rawJsonValue}");
        replaced.Should().NotBe(json, "差し替えに失敗している（キーが見つかっていない）");
        return replaced;
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
