using FluentAssertions;
using Xunit;

namespace AiStockTrading.Report.Domain.Tests;

// FR-06, FR-09, FR-16, IADR-0116 決定4, #280: Discord へ投稿する要約の組み立て（純関数）を検証する。
// 数値はコード集計値（PnlSummary）だけを使い、散文（LLM 出力）は必ずサニタイズを通す。
public class ReportSummaryTests
{
    // 実現損益(税引前) 15,000 / 費用 450 / 税 2,250 / 実現損益(税引後) 12,300 / 評価損益 -800 / 約定 4・決済 2・勝ち 1
    private static readonly PnlSummary Pnl = new(15_000m, 450m, 2_250m, 12_300m, -800m, 4, 2, 1);

    [Fact]
    public void 種別と期間と承認待ちである旨を先頭に置く()
    {
        var summary = ReportSummary.Build(ReportKind.Daily, "2026-07-29", Pnl, "所感");

        summary.Should().StartWith("日報 2026-07-29");
        summary.Should().Contain("承認待ち");
    }

    [Theory]
    [InlineData(ReportKind.Daily, "日報")]
    [InlineData(ReportKind.Weekly, "週報")]
    [InlineData(ReportKind.Monthly, "月報")]
    public void 種別ごとの呼称を使う(ReportKind kind, string label)
    {
        ReportSummary.Build(kind, "2026-07", Pnl, "所感").Should().StartWith(label);
    }

    [Fact]
    public void コード集計した数値を載せる()
    {
        var summary = ReportSummary.Build(ReportKind.Daily, "2026-07-29", Pnl, "所感");

        summary.Should().Contain("+12,300 円");  // 実現損益（税引後・費用込み）
        summary.Should().Contain("450 円");      // 費用合計
        summary.Should().Contain("4");           // 約定件数
        summary.Should().Contain("2");           // 決済件数
    }

    [Fact]
    public void 損益の符号を明示する()
    {
        var loss = Pnl with { RealizedPnlNet = -3_400m };

        ReportSummary.Build(ReportKind.Daily, "2026-07-29", loss, "所感").Should().Contain("-3,400 円");
    }

    [Fact]
    public void 散文を要約に含める()
    {
        ReportSummary.Build(ReportKind.Daily, "2026-07-29", Pnl, "半導体主導で堅調でした。")
            .Should().Contain("半導体主導で堅調でした。");
    }

    [Fact]
    public void 散文は必ずサニタイズを通す()
    {
        // 呼び出し側がサニタイズを忘れられる形にしない（IADR-0116 決定4）。
        var summary = ReportSummary.Build(ReportKind.Daily, "2026-07-29", Pnl, "@everyone 全部売れ");

        summary.Should().NotContain("@everyone");
        summary.Should().Contain("全部売れ");
    }

    [Fact]
    public void 長い散文は要約の上限内へ収める()
    {
        var summary = ReportSummary.Build(ReportKind.Daily, "2026-07-29", Pnl, new string('あ', 5_000));

        summary.Length.Should().BeLessThanOrEqualTo(ReportSummary.MaxLength);
        summary.Should().Contain("+12,300 円"); // 数値は切り落とされない（散文だけを詰める）
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 散文が無くても数値行は残る(string? narrative)
    {
        var summary = ReportSummary.Build(ReportKind.Daily, "2026-07-29", Pnl, narrative);

        summary.Should().Contain("+12,300 円");
        summary.Should().NotEndWith("\n");
    }
}
