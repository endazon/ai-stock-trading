using System.Text.RegularExpressions;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-20 (3), UC-06, #431, IADR-0161 / IADR-0163 決定1:
// **段階の既定発注先（StageSettings.Mode）も allow-list を通して読む。**
//
// #422 が是正した allow-list は `SettingsDto.BrokerProvider` にだけ効いており、**同じ設定行の隣の項目**
// `Stage.Mode`（同じ `BrokerProvider` 型）には効いていなかった。到達経路は同一（手編集された行・
// 外部ツールが書いた行）であり、**同じ経路の片方だけを塞いだ状態**だった。
//
// 残っていた欠陥は 2 つの性質に分かれる。
//   - 未知の**序数**: 素通りするが `RiskEvaluator` の `Stage.Mode != MoomooReal` により実弾は止まる（安全側）。
//   - 未知の**文字列**・真偽値・オブジェクト・配列: **`JsonException` で設定行全体が読めなくなる**
//     ——統制値・ガード・段階もろとも失われ、`GetCurrent` が 500 を返しリスク判定そのものが動かない。
//     **こちらが本 issue の主眼**であり、IADR-0161 が「採らない」と明記した挙動そのものである。
public class RiskSettingsSerializationStageModeTests
{
    // **最重要**: 段階の既定発注先が解決できない行でも、**例外を投げず**設定行の他の値がすべて読める。
    // 例外へ倒すと「フェイルクローズ」に見えて、実際には統制値・ガード・段階もろとも失われる。
    [Theory]
    [InlineData("7")]                 // 未知の序数
    [InlineData("3")]                 // 直近の未使用序数（4 値目が足された日に意味が変わる値）
    [InlineData("-1")]
    [InlineData("2147483648")]        // int に収まらない数値
    [InlineData("null")]              // 明示的な null
    [InlineData("\"MOOMOO REAL\"")]   // 未知の文字列
    [InlineData("\"Live\"")]
    [InlineData("\"moomooReal\"")]    // 大小文字違い（Ordinal で弾く）
    [InlineData("\"REAL\"")]
    [InlineData("\"\"")]              // 空文字
    [InlineData("true")]              // 別の型（真偽値）
    [InlineData("{}")]                // 別の型（オブジェクト）
    [InlineData("[1]")]               // 別の型（配列）
    public void 解決できない段階の既定発注先は内蔵paperとして読まれ設定行は失われない(string rawJsonValue)
    {
        var json = WithStageModeValue(rawJsonValue);

        // 例外を投げないこと自体が要件である。
        var restored = RiskSettingsSerialization.Deserialize(json);

        restored.Stage.Mode.Should().Be(
            BrokerProvider.InternalPaper,
            "stage.mode={0} は allow-list に無い（FR-20 (3)・IADR-0161）",
            rawJsonValue);
        restored.Stage.Mode.Should().NotBe(BrokerProvider.MoomooReal);

        // 巻き添えで設定行の他の値が失われていないこと（例外へ倒していない証拠）。
        var defaults = TradingDefaults.CreateSettings();
        restored.Limits.MaxOpenPositions.Should().Be(defaults.Limits.MaxOpenPositions);
        restored.Guard.EnabledProductTypes.Should().BeEquivalentTo(defaults.Guard.EnabledProductTypes);
        restored.Stage.Stage.Should().Be(defaults.Stage.Stage);
        restored.Stage.CapitalCapRatio.Should().Be(defaults.Stage.CapitalCapRatio);
        restored.BrokerProvider.Should().Be(defaults.BrokerProvider);
        restored.ShortSell.Should().NotBeNull();
    }

    // 肯定形の裏面: 正準名・序数の 10 進表記は明示一致で読む（allow-list が「読めるものは読む」こと）。
    [Theory]
    [InlineData("\"MoomooReal\"", BrokerProvider.MoomooReal)]
    [InlineData("\"MoomooSimulate\"", BrokerProvider.MoomooSimulate)]
    [InlineData("\"InternalPaper\"", BrokerProvider.InternalPaper)]
    [InlineData("\"1\"", BrokerProvider.MoomooReal)]
    [InlineData("\"2\"", BrokerProvider.MoomooSimulate)]
    [InlineData("\"0\"", BrokerProvider.InternalPaper)]
    public void 正準名や序数の文字列で書かれた段階の既定発注先は明示一致で読まれる(
        string rawJsonValue, BrokerProvider expected)
    {
        RiskSettingsSerialization
            .Deserialize(WithStageModeValue(rawJsonValue))
            .Stage.Mode.Should().Be(expected);
    }

    // 回帰: 旧 `TradeMode` の序数（0 / 1）を積んだ行は同じ意味で読まれる（IADR-0140 決定2・3）。
    // allow-list を通しても既存行の意味が変わらないことを固定する。
    [Theory]
    [InlineData(0, BrokerProvider.InternalPaper)]
    [InlineData(1, BrokerProvider.MoomooReal)]
    [InlineData(2, BrokerProvider.MoomooSimulate)]
    public void 既知の序数を積んだ段階設定はそのまま読まれる(int ordinal, BrokerProvider expected)
    {
        RiskSettingsSerialization
            .Deserialize(WithStageModeValue(ordinal.ToString()))
            .Stage.Mode.Should().Be(expected);
    }

    // ワイヤ形式は**数値の序数のまま**である（文字列へ変えると旧版が読めなくなる）。
    [Fact]
    public void 段階の既定発注先は数値の序数として書き出される()
    {
        var settings = TradingDefaults.CreateSettings() with
        {
            Stage = new StageSettings(
                TradingStage.Stage1Simulate, BrokerProvider.MoomooSimulate, TradingDefaults.FullCapitalCapRatio),
        };

        RiskSettingsSerialization.Serialize(settings).Should().Contain("\"mode\":2");
    }

    // 段階設定は往復する（DTO を挟んだことで欠落する項目が無いこと）。
    [Fact]
    public void 段階設定は保存と読み出しで往復する()
    {
        foreach (var stage in Enum.GetValues<TradingStage>())
        {
            foreach (var mode in Enum.GetValues<BrokerProvider>())
            {
                var settings = TradingDefaults.CreateSettings() with
                {
                    Stage = new StageSettings(stage, mode, 0.30m),
                };

                RiskSettingsSerialization
                    .Deserialize(RiskSettingsSerialization.Serialize(settings))
                    .Stage.Should().Be(new StageSettings(stage, mode, 0.30m));
            }
        }
    }

    // FR-20 (1), #431: **安全側の挙動が保たれること。** 段階の既定発注先が解決できない行を読んだ設定では、
    // 実弾の注文が `StageProhibitsLiveTrading` で拒否される（内蔵 paper へ倒れる＝`!= MoomooReal`）。
    // 直列化の単体だけを見ていると「内蔵 paper へ倒した」ことが**統制として何を意味するか**を見落とす。
    [Theory]
    [InlineData("7")]
    [InlineData("\"MOOMOO REAL\"")]
    [InlineData("true")]
    public void 段階の既定発注先が解決できない行では実弾の発注が段階ゲートで拒否される(string rawJsonValue)
    {
        // 現在の発注先は実弾（1）に、段階の既定発注先は解決できない値にした行を読む。
        var json = WithStageModeValue(rawJsonValue);
        json = Regex.Replace(json, "\"brokerProvider\":\\d+", "\"brokerProvider\":1");

        var settings = RiskSettingsSerialization.Deserialize(json);
        settings.BrokerProvider.Should().Be(BrokerProvider.MoomooReal, "前提の再現に失敗している");

        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooReal, Quantity: 1, Price: 100m, PositionEffect.Open);
        var snapshot = new PortfolioSnapshot
        {
            Capital = 100_000m,
            SymbolsTradedToday = new HashSet<(string, Market)>(),
        };

        RiskEvaluator.Evaluate(intent, settings, snapshot)
            .Reasons.Should().Contain(
                RejectionReason.StageProhibitsLiveTrading,
                "解決できない段階の既定発注先は内蔵 paper へ倒れ、実弾の発注は止まる（FR-20 (1)(3)）");
    }

    // stage.mode の値だけを差し替えた行を作る（他の項目は既定のまま）。
    private static string WithStageModeValue(string rawJsonValue)
    {
        var json = RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings());
        Regex.IsMatch(json, "\"mode\":\\d+")
            .Should().BeTrue("差し替え対象のキーが見つかっていない（テストが検証にならない）");
        return Regex.Replace(json, "\"mode\":\\d+", $"\"mode\":{rawJsonValue}");
    }
}
