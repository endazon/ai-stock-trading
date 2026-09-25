using AwesomeAssertions;
using ReportService.Domain;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-09, #840, IADR-0352 決定 5: 「未供給だった入力」の語彙（種別ごとの適用・永続化形式）と、
// 提示通知の要約へ載せる警告行を固定する。
public class ReportInputsTests
{
    // ---- 種別ごとの適用（ReportRenderer / ReportDraftService の種別分岐と対で維持する） ----------------

    [Theory]
    [InlineData(ReportInput.Fills, true, true, true)]
    [InlineData(ReportInput.Narrative, true, true, true)]
    [InlineData(ReportInput.TradeRationales, true, true, false)]
    [InlineData(ReportInput.OpenPositions, true, false, false)]
    [InlineData(ReportInput.CurrentStage, false, false, true)]
    [InlineData(ReportInput.MarginReductions, true, false, true)]
    [InlineData(ReportInput.BuyInInferences, true, false, true)]
    [InlineData(ReportInput.FxSourceStatus, true, false, true)]
    [InlineData(ReportInput.LlmUsage, true, false, true)]
    [InlineData(ReportInput.BorrowFees, true, false, true)]
    [InlineData(ReportInput.OpenDUptime, true, false, true)]
    [InlineData(ReportInput.PeriodEndFxRate, true, false, true)]
    // T-10-997, #823: 日報 §4「損切りの実行機構（当日）」だけが使う。
    [InlineData(ReportInput.StopLossMethods, true, false, false)]
    public void 入力は_それを描く種別にだけ適用される(ReportInput input, bool daily, bool weekly, bool monthly)
    {
        ReportInputs.AppliesTo(input, ReportKind.Daily).Should().Be(daily);
        ReportInputs.AppliesTo(input, ReportKind.Weekly).Should().Be(weekly);
        ReportInputs.AppliesTo(input, ReportKind.Monthly).Should().Be(monthly);
    }

    [Fact]
    public void 語彙のすべてに_適用の定義と表示名がある()
    {
        // 語彙を足して AppliesTo / Label を足し忘れると「欠けているのに警告が出ない」側へ倒れる。
        foreach (var input in Enum.GetValues<ReportInput>())
        {
            Enum.GetValues<ReportKind>().Any(kind => ReportInputs.AppliesTo(input, kind))
                .Should().BeTrue($"{input} を使う種別が 1 つも無いなら語彙に要らない");
            ReportInputs.Label(input).Should().NotBe(input.ToString(), $"{input} の表示名が未定義");
        }
    }

    // ---- 永続化形式 ---------------------------------------------------------------------------

    [Fact]
    public void 永続化形式は宣言順で重複なく往復する()
    {
        var serialized = ReportInputs.Serialize(
            [ReportInput.OpenDUptime, ReportInput.Fills, ReportInput.OpenDUptime, ReportInput.OpenPositions]);

        serialized.Should().Be("Fills,OpenPositions,OpenDUptime");
        ReportInputs.Parse(serialized).Should().Equal(
            ReportInput.Fills, ReportInput.OpenPositions, ReportInput.OpenDUptime);
    }

    [Fact]
    public void 未供給が無ければ_NULL_のまま保存する()
    {
        ReportInputs.Serialize([]).Should().BeNull();
        ReportInputs.Serialize(null).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 既存行の_NULL_や空は_未供給なしとして読む(string? stored)
    {
        ReportInputs.Parse(stored).Should().BeEmpty();
    }

    [Fact]
    public void 解釈できない要素は捨て_残りは読む()
    {
        // 将来の語彙・数値の混入（"99" は Enum.TryParse が通してしまう）で一覧ごと落とさない。
        ReportInputs.Parse("OpenPositions,FutureInput,99, Narrative ,openpositions")
            .Should().Equal(ReportInput.OpenPositions, ReportInput.Narrative);
    }

    // ---- 提示通知の要約 -----------------------------------------------------------------------

    private static readonly PnlSummary ZeroPnl = PnlAggregator.Aggregate([], AiStockTrading.Shared.Kernel.Trading.TradingAssumptionsDefaults.Create(), null);

    [Fact]
    public void 未供給の入力は_要約に表示名で現れる()
    {
        var summary = ReportSummary.Build(
            ReportKind.Daily, "2026-09-18", ZeroPnl, "所感",
            [ReportInput.OpenPositions, ReportInput.OpenDUptime, ReportInput.Narrative]);

        summary.Should().Contain(ReportSummary.UnsuppliedWarningPrefix);
        summary.Should().Contain("建玉、OpenD 稼働率、散文（LLM）");
        summary.Should().Contain("確定の前に本文を確認してください");
    }

    [Fact]
    public void 未供給が無ければ_要約に警告は現れない()
    {
        ReportSummary.Build(ReportKind.Daily, "2026-09-18", ZeroPnl, "所感", [])
            .Should().NotContain(ReportSummary.UnsuppliedWarningPrefix);
        // 引数を渡さない既存の呼び出しは本変更前とバイト等価。
        ReportSummary.Build(ReportKind.Daily, "2026-09-18", ZeroPnl, "所感")
            .Should().Be(ReportSummary.Build(ReportKind.Daily, "2026-09-18", ZeroPnl, "所感", []));
    }

    [Fact]
    public void 散文が上限まで長くても_警告は切り落とされない()
    {
        var summary = ReportSummary.Build(
            ReportKind.Monthly, "2026-09", ZeroPnl, new string('あ', 5000), Enum.GetValues<ReportInput>());

        summary.Length.Should().BeLessThanOrEqualTo(ReportSummary.MaxLength);
        summary.Should().Contain(ReportSummary.UnsuppliedWarningPrefix);
        summary.Should().Contain(ReportInputs.Label(ReportInput.Narrative));
        // 警告は散文より前にある（詰められるのは散文側）。
        summary.IndexOf(ReportSummary.UnsuppliedWarningPrefix, StringComparison.Ordinal)
            .Should().BeLessThan(summary.IndexOf("あああ", StringComparison.Ordinal));
    }
}
