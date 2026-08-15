using AiStockTrading.Report.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Report.Domain.Tests;

// FR-06, FR-10, FR-17, UC-06, #381, ADR-0022 決定1・決定2・決定5, IADR-0196: 日報の「為替レートの情報源」節。
//
// 🔴 **ここで守っているのは「劣化を黙って隠さない」ことである。** 通知は流れて消えるが、
// 報告書は**期間の記録**として残る。ここに出ていなければ、後から
// 「いつ劣化した情報源で判断していたか」を復元できない。
public class ReportRendererFxSourceStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 15, 3, 0, 0, TimeSpan.Zero);

    private static ReportView View(FxSourceStatus? fx, ReportKind kind = ReportKind.Daily) => new()
    {
        Kind = kind,
        PeriodKey = "daily-2026-08-15",
        PeriodLabel = "2026-08-15",
        Markets = ["JP", "US"],
        AssumptionsVersion = 2,
        Pnl = new PnlSummary(
            RealizedPnlGross: 0m, TotalCost: 0m, TaxWithheld: 0m, RealizedPnlNet: 0m,
            UnrealizedPnl: 0m, TradeCount: 0, RealizingTradeCount: 0, WinningTradeCount: 0),
        PolicySummary = "方針テスト",
        Narrative = "散文テスト",
        FxSourceStatus = fx,
    };

    private static FxSourceStatus Status(
        IReadOnlyList<FxRateSourceFellBack>? fellBacks = null,
        IReadOnlyList<FxRateSourcePrimaryRestored>? restorations = null,
        IReadOnlyList<FxRateStale>? stales = null,
        IReadOnlyList<string>? credits = null) =>
        new(fellBacks ?? [], restorations ?? [], stales ?? [], credits ?? []);

    // 🔴 **否定形（決定3）。** 照会不能を「切替なし」と書くと、劣化を隠したのと同じ結果になる。
    [Fact]
    public void 状態を照会できなかった場合は_切替なしとは書かず要確認と明記する()
    {
        var md = ReportRenderer.RenderMarkdown(View(fx: null));

        md.Should().Contain("状態を照会できませんでした（要確認）");
        md.Should().Contain("「切替なし」とは区別しています");
    }

    [Fact]
    public void 事象が無ければ_劣化しなかったことを明記する()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status()));

        md.Should().Contain("第一の情報源から取得できており、鮮度警告もありません");
        // 本節の文言だけを見る。「照会できませんでした」は他節（維持率割れの記録）も使うため、
        // 素で否定すると**別の節の正常な出力で落ちる**（実際に一度落とした）。
        md.Should().NotContain("状態を照会できませんでした", "空（事象なし）と照会不能を混同しない");
    }

    [Fact]
    public void フォールバックの事実と_止まっていないことを書く()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            fellBacks: [new FxRateSourceFellBack("USD", "fred", 2, 2, T0)])));

        md.Should().Contain("フォールバックへ切替");
        md.Should().Contain("USD");
        md.Should().Contain("fred");
        // 警告と停止の役割の違い（ADR-0022 決定5）を読み手が誤らないこと。
        md.Should().Contain("新規建ては止まっていません");
    }

    // ADR-0022 決定2 は事実だけでなく**期間**の記録を求めている。
    [Fact]
    public void 復帰には_フォールバックしていた期間を書く()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            restorations: [new FxRateSourcePrimaryRestored("USD", "boj", T0.AddHours(-6), T0)])));

        md.Should().Contain("第一の情報源へ復帰");
        md.Should().Contain("フォールバックしていた期間 6 時間");
    }

    [Fact]
    public void 鮮度警告には_しきい値と停止の上限を併記する()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            stales: [new FxRateStale("USD", T0.AddDays(-7), 7, 5, 30, T0)])));

        md.Should().Contain("鮮度警告");
        md.Should().Contain("経過 7 日");
        // 「あとどれだけで止まるか」が読めること。
        md.Should().Contain("停止 30 日");
        md.Should().Contain("新規建ては止まっていません");
    }

    // ADR-0022 決定1: 出典の明記。
    [Fact]
    public void 日銀を使った期間は_クレジット表記を出す()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(credits: [FxSourceCredits.Boj])));

        md.Should().Contain("出典: " + FxSourceCredits.Boj);
    }

    // 🔴 **否定形（決定4）。** 使っていない情報源のクレジットを出すのは事実に反する。
    [Fact]
    public void 日銀を使っていない期間は_日銀のクレジットを出さない()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            fellBacks: [new FxRateSourceFellBack("USD", "fred", 2, 2, T0)],
            credits: [])));

        md.Should().NotContain(FxSourceCredits.Boj);
    }

    // 週報は計画が本節の記載を求めていない（求められていない節を勝手に増やさない）。
    [Fact]
    public void 週報には為替の情報源の節を出さない()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(), ReportKind.Weekly));

        md.Should().NotContain("為替レートの情報源");
    }
}
