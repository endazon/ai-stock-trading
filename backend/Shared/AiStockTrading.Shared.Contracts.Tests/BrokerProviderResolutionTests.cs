using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-20 (3), #422, IADR-0161: 発注先の **allow-list** による解決。
//
// 計画（FR-20 の 2026-08-07 追記 (3)）:
//   「発注先の初期値は内蔵 `paper` とする——外部へ一度も発注しない唯一の値であり、初期段階が Stage 0 で
//     あることとも整合する。**設定ストアの旧行（本項目を持たない場合）も同じ既定へ落とす**
//     （「読めない行は実弾」に倒れないようにするため）」
//
// 本テスト群の主眼は**否定形**である——解決できない入力が**実弾側へ落ちない**ことを固定する。
// 肯定形（3 値が往復する）だけでは、`_ => MoomooReal` という 1 行の退行を検知できない。
public class BrokerProviderResolutionTests
{
    // 既定は内蔵 paper（外部へ一度も発注しない唯一の値）。他の値へ変えたら赤くなる。
    [Fact]
    public void 既定は内蔵paperである()
    {
        BrokerProviderResolution.Default.Should().Be(BrokerProvider.InternalPaper);
    }

    // 肯定形: 計画が定める 3 値は明示一致で通る。
    [Theory]
    [InlineData(BrokerProvider.InternalPaper)]
    [InlineData(BrokerProvider.MoomooReal)]
    [InlineData(BrokerProvider.MoomooSimulate)]
    public void 計画の3値はそのまま解決される(BrokerProvider provider)
    {
        BrokerProviderResolution.IsKnown(provider).Should().BeTrue();
        BrokerProviderResolution.Resolve(provider).Should().Be(provider);
    }

    // **否定形の中核**: 未知の序数・null は内蔵 paper へ落ちる。実弾へは落ちない。
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void 未知の序数は内蔵paperへ落ちる(int ordinal)
    {
        var unknown = (BrokerProvider)ordinal;

        BrokerProviderResolution.IsKnown(unknown).Should().BeFalse();
        BrokerProviderResolution.Resolve(unknown).Should().Be(
            BrokerProvider.InternalPaper,
            "allow-list は 3 値の明示一致だけを受理する（FR-20 (3)）");
        BrokerProviderResolution.Resolve(unknown).Should().NotBe(
            BrokerProvider.MoomooReal,
            "「読めない値は実弾」に倒れる解決は取り返しがつかない");
    }

    [Fact]
    public void nullは内蔵paperへ落ちる()
    {
        // 本項目を持たない旧行（JSON にキーが無い）は null として現れる。
        BrokerProviderResolution.Resolve((BrokerProvider?)null).Should().Be(BrokerProvider.InternalPaper);
    }

    // 文字列表現: 序数の 10 進表記と正準名の**完全一致**のみ受理する。
    [Theory]
    [InlineData("0", BrokerProvider.InternalPaper)]
    [InlineData("1", BrokerProvider.MoomooReal)]
    [InlineData("2", BrokerProvider.MoomooSimulate)]
    [InlineData("InternalPaper", BrokerProvider.InternalPaper)]
    [InlineData("MoomooReal", BrokerProvider.MoomooReal)]
    [InlineData("MoomooSimulate", BrokerProvider.MoomooSimulate)]
    [InlineData("  MoomooReal  ", BrokerProvider.MoomooReal)] // 前後空白のみは除く
    [InlineData("1 ", BrokerProvider.MoomooReal)]             // 同上（前後空白のみを除く規則の裏面）
    public void 正準名と序数の文字列は解決される(string text, BrokerProvider expected)
    {
        BrokerProviderResolution.Resolve(text).Should().Be(expected);
    }

    // **否定形の中核**: 未知の文字列・大小文字違い・空文字・空白は内蔵 paper へ落ちる。
    // 計画（(2)）が「REAL」照合で大小文字を区別すると定めたのと同じ規律であり、ここを緩めると
    // `"MOOMOO REAL"` のような綴りが実弾として通り始める。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MOOMOO REAL")]
    [InlineData("moomoo real")]
    [InlineData("moomooreal")]
    [InlineData("MOOMOOREAL")]
    [InlineData("moomooReal")]
    [InlineData("REAL")]
    [InlineData("real")]
    [InlineData("live")]
    [InlineData("Moomoo Real")]
    [InlineData("Moomoo_Real")]
    [InlineData("01")]
    [InlineData("1.0")]
    [InlineData("+1")]
    [InlineData("ＲＥＡＬ")]
    [InlineData("１")] // 全角数字
    public void 未知の文字列と大小文字違いは内蔵paperへ落ちる(string? text)
    {
        var resolved = BrokerProviderResolution.Resolve(text);

        resolved.Should().Be(
            BrokerProvider.InternalPaper,
            "未知の文字列 '{0}' は allow-list に無い（FR-20 (3)）",
            text ?? "<null>");
    }

    // プロパティ: **allow-list が受理する値はちょうど 3 つ**。将来 4 値目を末尾へ足しても、
    // 本規則が明示的に承認するまで黙って通らない（Enum.IsDefined へ退行させないための固定）。
    [Fact]
    public void 受理される発注先はちょうど3値である()
    {
        var known = Enumerable.Range(-1_000, 2_000)
            .Select(i => (BrokerProvider)i)
            .Where(BrokerProviderResolution.IsKnown)
            .ToList();

        known.Should().BeEquivalentTo(
        [
            BrokerProvider.InternalPaper,
            BrokerProvider.MoomooReal,
            BrokerProvider.MoomooSimulate,
        ]);
    }
}
