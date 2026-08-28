using AiStockTrading.Report.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Report.Domain.Tests;

// FR-04, FR-06, FR-07, ADR-0017 決定4-(1), #335, IADR-0217: 報告書の「散文生成に使用した LLM」節。
//
// 🔴 **ここで守っているのは「沈黙のフォールバックを作らない」ことである。** 通知は流れて消えるが、
// 報告書は**期間の記録**として残る。ADR-0017 決定4 の明文——「月報が第 1 候補で書かれたのか
// 第 2 候補で書かれたのかは、その月報を次の 1 か月の方針書として採用する際の判断材料である」——は、
// 描画されて初めて満たされる。写像（LlmModelUsage）が正しくても、描画で落ちれば読み手には届かない。
public class ReportRendererLlmModelUsageTests
{
    private static ReportView View(LlmModelUsage? usage, ReportKind kind = ReportKind.Monthly) => new()
    {
        Kind = kind,
        PeriodKey = "monthly-2026-08",
        PeriodLabel = "2026-08",
        Markets = ["JP", "US"],
        AssumptionsVersion = 2,
        Pnl = new PnlSummary(
            RealizedPnlGross: 0m, TotalCost: 0m, TaxWithheld: 0m, RealizedPnlNet: 0m,
            UnrealizedPnl: 0m, TradeCount: 0, RealizingTradeCount: 0, WinningTradeCount: 0),
        PolicySummary = "方針テスト",
        Narrative = "散文テスト",
        LlmModelUsage = usage,
    };

    // 決定4-(1): 第 1 候補どおりなら「発火なし」と明記する。
    // **黙って何も書かない**のでは「第 1 候補で書かれた」と「そもそも記録していない」が区別できない。
    [Fact]
    public void 第1候補で生成された月報は発火なしと明記する()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(new LlmModelUsage("report-monthly", "claude-opus-5", "claude-opus-5", "Primary")));

        md.Should().Contain("### 散文生成に使用した LLM");
        md.Should().Contain("- 用途: report-monthly");
        md.Should().Contain("- 割当（第 1 候補）: claude-opus-5");
        md.Should().Contain("- 実際に使用したモデル: claude-opus-5");
        md.Should().Contain("フォールバック: 発火なし（第 1 候補で生成）");
    }

    // 🔴 決定4-(1) の要点。第 2 候補で書かれた月報は、**そう読めるように**残さねばならない。
    // 品質が第 1 候補と同一である保証が無いことまで書くのは、次月の方針として採る側の判断材料だからである。
    [Fact]
    public void 第2候補で生成された月報は発火ありと原因つきで明記する()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(new LlmModelUsage("report-monthly", "claude-opus-5", "claude-sonnet-5", "FallbackFired")));

        md.Should().Contain("- 実際に使用したモデル: claude-sonnet-5");
        md.Should().Contain("フォールバック: 発火あり（FallbackFired）");
        md.Should().Contain("品質が第 1 候補と同一である保証はありません");
        md.Should().NotContain("発火なし", "第 2 候補で書かれた事実を打ち消す文言を同時に出さない");
    }

    // 割当表に無いモデルへ落ちた場合（Unassigned）も原因が読める。
    [Fact]
    public void 割当外のモデルへ落ちた場合も原因を明記する()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(new LlmModelUsage("report-daily", null, "claude-opus-4-8", "Unassigned"), ReportKind.Daily));

        md.Should().Contain("- 割当（第 1 候補）: （割当なし）");
        md.Should().Contain("フォールバック: 発火あり（Unassigned）");
    }

    // 🔴 **否定形。** 未供給（null）を「第 1 候補で書かれた」と読ませない。
    // 縮退でプレースホルダ散文へ倒れたときはモデルを知り得ないため、節ごと出さないのが正である
    //（AppendFxSourceStatus と同じ規律。既存の描画結果を変えないことも兼ねる）。
    [Fact]
    public void モデル情報が未供給なら節ごと出さない()
    {
        var md = ReportRenderer.RenderMarkdown(View(usage: null));

        md.Should().NotContain("### 散文生成に使用した LLM");
        // #338: 「フォールバック」の語そのものは、月報 §7（当月の LLM 利用実績）が**別の事実**として使う。
        // ここで見たいのは「**この報告書の散文がフォールバックで書かれたか**」という主張が出ないことなので、
        // 本節が出す 2 つの主張の文言で照合する（語での照合は他節の追加で偽陽性になる）。
        md.Should().NotContain("フォールバック: 発火あり");
        md.Should().NotContain("フォールバック: 発火なし");
    }

    // 実効モデルが不明（応答がモデルを名乗らない）でも「不明」と書き、空欄で流さない。
    [Fact]
    public void 実効モデルが不明なら不明と明記する()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(new LlmModelUsage("report-weekly", "claude-opus-5", null, "FallbackFired"), ReportKind.Weekly));

        md.Should().Contain("- 実際に使用したモデル: （不明）");
    }
}
