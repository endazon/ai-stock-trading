using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-20, FR-12, INDEX 決定 46, #334, IADR-0140: 発注先（Broker Provider）の 3 値と序数の不変性。
//
// 本 enum は旧 TradeMode（Paper=0 / Live=1）を置き換えたものであり、HTTP 応答・イベント本文・
// 台帳列（approved_orders.Mode）が**整数として**往来させている。序数を動かすと、過去に記録された
// 「どこへ発注したか」の意味が変わる（IADR-0134 決定2 と同じ規律）。
public class BrokerProviderTests
{
    // FR-20:「発注先の値は moomoo `REAL`（実弾）／ moomoo `SIMULATE`（… OpenD 経由で実際に発注する）／
    // 内蔵 `paper`（… 外部へ発注しない。FR-12）の 3 値」。**増やしても減らしてもならない。**
    [Fact]
    public void 発注先は計画の3値である()
    {
        Enum.GetNames<BrokerProvider>().Should().BeEquivalentTo(
            ["InternalPaper", "MoomooReal", "MoomooSimulate"],
            "計画（FR-20・INDEX 決定 46）が定める発注先は 3 値であり、実装が値を発明してはならない");
    }

    // 境界値: 序数の固定。0 / 1 は旧 TradeMode.Paper / TradeMode.Live と同義であり、
    // **永続済みの行・在庫中のイベントの意味を保つために動かせない**。新値は末尾（2）へ足した。
    [Theory]
    [InlineData(BrokerProvider.InternalPaper, 0)]
    [InlineData(BrokerProvider.MoomooReal, 1)]
    [InlineData(BrokerProvider.MoomooSimulate, 2)]
    public void 序数は旧TradeModeの意味を保存する(BrokerProvider provider, int expectedOrdinal)
    {
        ((int)provider).Should().Be(
            expectedOrdinal,
            "0＝旧 Paper・1＝旧 Live。既存メンバの序数を動かすと過去の記録の意味が変わる（IADR-0140 決定2）");
    }

    // 否定形: 既定値（default(BrokerProvider)＝未指定・逆直列化の抜け）が**実弾を指してはならない**。
    // 「指定を忘れたら実弾」は取り返しのつかない失敗モードである。
    [Fact]
    public void 既定値は実弾ではなく外部へ発注しない値である()
    {
        default(BrokerProvider).Should().Be(BrokerProvider.InternalPaper);
        default(BrokerProvider).Should().NotBe(BrokerProvider.MoomooReal);
    }

    // プロパティベース: すべての値について、整数へ落として戻す往復が同一値を保つ
    // （HTTP・イベント・台帳が整数で往復させるため）。
    [Fact]
    public void すべての発注先は整数往復で同一値を保つ()
    {
        foreach (var provider in Enum.GetValues<BrokerProvider>())
        {
            var roundTripped = (BrokerProvider)(int)provider;
            roundTripped.Should().Be(provider);
        }
    }

    // FR-20, #334: OrderIntent は既定 JSON（数値 enum）で往復する。旧 TradeMode の値 0 / 1 を積んだ
    // 在庫中のイベント本文が、新 enum で**同じ意味**に読めることを固定する（否定形の裏返し）。
    [Theory]
    [InlineData(0, BrokerProvider.InternalPaper)]
    [InlineData(1, BrokerProvider.MoomooReal)]
    public void 旧TradeModeの数値を積んだ注文意図は同じ意味で読める(int legacyOrdinal, BrokerProvider expected)
    {
        var json = $$"""
            {"Symbol":"AAPL","Market":1,"Side":0,"ProductType":0,"Mode":{{legacyOrdinal}},"Quantity":1,"Price":100}
            """;

        var intent = JsonSerializer.Deserialize<OrderIntent>(json);

        intent.Should().NotBeNull();
        intent!.Mode.Should().Be(expected);
    }
}
