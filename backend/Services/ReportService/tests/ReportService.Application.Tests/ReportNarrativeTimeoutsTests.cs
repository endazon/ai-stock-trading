using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Domain;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Report.Application.Tests;

// T-1〜T-4, FR-06/16, IADR-0123, #308: 報告書散文 LLM のタイムアウトを報告書種別ごとに解決する純関数。
//
// 週報（opus-5）・月報（fable-5）は日報（sonnet-5）より重いモデルが割り当てられており（IADR-0120 / MSP#422）、
// サービス共通の 30 秒では構造的に間に合わない（2026-07-31 の経路B live 検証で週報がちょうど 30 秒で縮退）。
// 解決順は「種別別設定 → 全種別設定 → 組込既定」で、いずれの段でも空/不正/非正値は未設定として次段へ倒す。
public class ReportNarrativeTimeoutsTests
{
    // T-1: 未設定なら組込既定。日報 30 秒は据置（現に成功しており、延ばすと遅延検知が鈍る）。
    [Theory]
    [InlineData(ReportKind.Daily, 30)]
    [InlineData(ReportKind.Weekly, 120)]
    [InlineData(ReportKind.Monthly, 120)]
    public void 未設定なら種別ごとの組込既定へ解決する(ReportKind kind, int expectedSeconds)
    {
        var timeouts = new ReportNarrativeTimeouts(allKinds: null, daily: null, weekly: null, monthly: null);

        timeouts.For(kind).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    // T-2: 全種別設定（既存の LlmGateway:TimeoutSeconds）は種別別設定の無い種別すべてへ適用する（既存デプロイの非破壊）。
    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 全種別設定は種別別設定の無い全種別へ適用される(ReportKind kind)
    {
        var timeouts = new ReportNarrativeTimeouts(allKinds: "45", daily: null, weekly: null, monthly: null);

        timeouts.For(kind).Should().Be(TimeSpan.FromSeconds(45));
    }

    // T-3: 種別別設定は全種別設定より優先する。
    [Fact]
    public void 種別別設定は全種別設定より優先する()
    {
        var timeouts = new ReportNarrativeTimeouts(allKinds: "45", daily: "10", weekly: "300", monthly: null);

        timeouts.For(ReportKind.Daily).Should().Be(TimeSpan.FromSeconds(10));
        timeouts.For(ReportKind.Weekly).Should().Be(TimeSpan.FromSeconds(300));
        timeouts.For(ReportKind.Monthly).Should().Be(TimeSpan.FromSeconds(45)); // 種別別が無い＝全種別設定
    }

    // T-4: 空/非数値/0/負値は「未設定」として扱い、より外側の既定へ倒す（既存 ParseTimeout の fail-safe を踏襲）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12.5")]
    public void 種別別設定が不正なら全種別設定へ倒す(string invalid)
    {
        var timeouts = new ReportNarrativeTimeouts(allKinds: "45", daily: null, weekly: invalid, monthly: null);

        timeouts.For(ReportKind.Weekly).Should().Be(TimeSpan.FromSeconds(45));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public void 全種別設定が不正なら組込既定へ倒す(string invalid)
    {
        var timeouts = new ReportNarrativeTimeouts(allKinds: invalid, daily: null, weekly: null, monthly: null);

        timeouts.For(ReportKind.Daily).Should().Be(TimeSpan.FromSeconds(30));
        timeouts.For(ReportKind.Weekly).Should().Be(TimeSpan.FromSeconds(120));
    }

    // HttpClient の Timeout（上限としての多層防御）に使う。解決値の最大＝どの種別の要求も打ち切らない上限。
    [Fact]
    public void 最大値は全種別の解決値の最大である()
    {
        new ReportNarrativeTimeouts(null, null, null, null).Max.Should().Be(TimeSpan.FromSeconds(120));
        new ReportNarrativeTimeouts(allKinds: "45", daily: null, weekly: null, monthly: "600").Max
            .Should().Be(TimeSpan.FromSeconds(600));
        new ReportNarrativeTimeouts(allKinds: "20", daily: null, weekly: null, monthly: null).Max
            .Should().Be(TimeSpan.FromSeconds(20));
    }
}
