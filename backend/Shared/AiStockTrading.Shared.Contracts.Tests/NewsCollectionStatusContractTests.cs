using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// 🔴 FR-04, ADR-0020 決定2, #1081, IADR-0455: ニュースの状態の**数値を固定する**。
//
// InformationCollected.NewsStatus は通信路（JSON）では数値で送られる。値を振り直すと、新旧が混在する配備
// （収集と判断の片方だけが新しい）で**欠測と未構成が入れ替わって**判断のプロンプトへ届く。基準ファイル
// （event-schemas.baseline.json）は型名しか見ないため、ここで各メンバーの数値と名前を完全一致で固定する。
// 0 は有効値にしない（既定値を「取得済み」と読ませない）。
public class NewsCollectionStatusContractTests
{
    [Theory]
    [InlineData(NewsCollectionStatus.Fetched, 1)]
    [InlineData(NewsCollectionStatus.Outage, 2)]
    [InlineData(NewsCollectionStatus.NotConfigured, 3)]
    public void 各状態の数値は固定である(NewsCollectionStatus status, int expected)
    {
        ((int)status).Should().Be(expected);
    }

    [Fact]
    public void メンバーは3つだけで0を持たない()
    {
        Enum.GetValues<NewsCollectionStatus>().Select(v => ((int)v, v.ToString()))
            .Should().BeEquivalentTo(new[] { (1, "Fetched"), (2, "Outage"), (3, "NotConfigured") });
        Enum.IsDefined((NewsCollectionStatus)0).Should().BeFalse();
    }
}
