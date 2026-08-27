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
        IReadOnlyList<string>? credits = null,
        IReadOnlyList<PositionClosedWithStaleFxRate>? staleCloses = null,
        IReadOnlyList<FxRateSourceUsed>? usages = null) =>
        new(fellBacks ?? [], restorations ?? [], stales ?? [], credits ?? [], staleCloses ?? [], usages ?? []);

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

        md.Should().Contain("情報源の切替・鮮度警告・鮮度切れでの決済の記録はありません");
        // 本節の文言だけを見る。「照会できませんでした」は他節（維持率割れの記録）も使うため、
        // 素で否定すると**別の節の正常な出力で落ちる**（実際に一度落とした）。
        md.Should().NotContain("状態を照会できませんでした", "空（事象なし）と照会不能を混同しない");
    }

    // 🔴 **否定形（IADR-0199 決定5）。** 記録は**遷移時にしか残らない**ため、
    // **記録が無いことは「第一の源を使った」ことを意味しない**（為替を一度も使わなかった期間と区別が付かない）。
    // 旧文「第一の情報源から取得できており」は**証拠が支えていない主張**だった。
    [Fact]
    public void 記録が無い期間に_第一の情報源を使ったとは書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status()));

        md.Should().NotContain("第一の情報源から取得できており",
            "遷移が無いことは「第一の源を使った」ことの証拠にならない");
    }

    // 🔴 **出典が空であることを黙って通さない。** 読み手が「書き忘れ」と「書ける根拠が無い」を
    // 区別できなければ、本節が一貫して避けてきた形（黙って劣化させない）に反する。
    [Fact]
    public void 出典の証拠が無い期間は_特定できないと明記する()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            stales: [new FxRateStale("USD", T0.AddDays(-7), 7, 5, 30, T0)])));

        md.Should().Contain("出典: **記録からは特定できません**");
    }

    // 🔴 **否定形（対）。** 証拠があるときに「特定できません」を出さない（読み分けが壊れると意味が無い）。
    [Fact]
    public void 出典の証拠がある期間は_特定できないとは書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(credits: [FxSourceCredits.Boj])));

        md.Should().Contain("出典: " + FxSourceCredits.Boj);
        md.Should().NotContain("記録からは特定できません");
    }

    // --- #513（IADR-0225 決定D・決定E）: 静かな期間の出典 ---------------------------------------

    // 🔴 **証拠ができたので、外していた文言を戻す。** IADR-0199 決定5 が外したのは
    // 「証拠が支えていない主張」であって、文言そのものではない。
    [Fact]
    public void 第一の源の使用記録があれば_第一から取得できていたと書く()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            usages: [new FxRateSourceUsed("USD", "boj", 1, 2, T0)])));

        md.Should().Contain("第一の情報源（boj）から取得できており");
        md.Should().Contain("記録はありません", "使用記録は劣化ではない");
    }

    // 🔴 **否定形（決定D）。** 使用記録を IsClean に混ぜると、**平常運転の日が「劣化あり」と読める**。
    [Fact]
    public void 使用記録だけの期間は_劣化ありとは書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            usages: [new FxRateSourceUsed("USD", "boj", 1, 2, T0)])));

        // 明細行の見出しで見る（「情報源の切替・鮮度警告…の記録はありません」という
        // **劣化なしの文自身が語を含む**ため、素の語で否定すると正常な出力で落ちる）。
        md.Should().NotContain("- **フォールバックへ切替**");
        md.Should().NotContain("- **鮮度警告**");
    }

    // 🔴 **否定形（決定E）。** 使用記録が入ったことで「使った源は分かるが、その源はクレジットを
    // 求めていない」状態が生じた。ここを「特定できません」と書くと**端的に誤りになる**。
    [Fact]
    public void クレジットを求めない源だけを使った期間は_特定できないとは書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            usages: [new FxRateSourceUsed("USD", "fred", 2, 2, T0)])));

        md.Should().Contain("出典: fred（クレジット表記を求めていない情報源です）");
        md.Should().NotContain("記録からは特定できません", "使った源は台帳から特定できている");
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

    // --- #381 停止側 / IADR-0198 -----------------------------------------------------------------

    // 🔴 **停止域を警告と同じ文で書かない。** 従来は `EntryBlocked` を見ずに
    // 「新規建ては止まっていません」と書いていたため、**統制が発動した日の日報が
    // 「発動していない」と読める**状態だった。
    [Fact]
    public void 停止域は_新規建てを停止したことを書く()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            stales: [new FxRateStale("USD", T0.AddDays(-31), 31, 5, 30, T0, EntryBlocked: true)])));

        md.Should().Contain("鮮度切れ（新規建て停止）");
        md.Should().Contain("新規建てを停止しました");
        // 手仕舞いまで止まったと読ませない（ADR-0022 決定5）。
        md.Should().Contain("手仕舞い・損切りは止めていません");
    }

    // 🔴 **否定形。** 警告域の行が停止域の文言へ引きずられていないこと。
    [Fact]
    public void 警告域は_停止したとは書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            stales: [new FxRateStale("USD", T0.AddDays(-7), 7, 5, 30, T0)])));

        md.Should().NotContain("新規建てを停止しました");
        md.Should().Contain("新規建ては止まっていません");
    }

    // 🔴 **取引の記録は 1 件ずつ出す**（IADR-0198 決定3）。鮮度警告は 1 日 1 回へ抑止されるが、
    // **決済は件数も金額も後から復元できなければならない。**
    [Fact]
    public void 鮮度切れでの決済は_観測日つきで1件ずつ出す()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(staleCloses: [
            new PositionClosedWithStaleFxRate("7203", Market.Japan, "JPY", 300, 0.0067m, T0.AddDays(-31), 31, T0),
            new PositionClosedWithStaleFxRate("6758", Market.Japan, "JPY", 100, 0.0067m, T0.AddDays(-31), 31, T0),
        ])));

        md.Should().Contain("7203");
        md.Should().Contain("6758", "抑止されないため 2 件とも出る");
        md.Should().Contain("観測日 2026-07-15");
        md.Should().Contain("換算額は実勢から乖離し得ます");
    }

    // 🔴 **否定形。** 鮮度警告が当日ぶん既に出ていて空でも、決済だけが残ることがある。
    // ここを落とすと「劣化はありませんでした」と決済の明細が並ぶ（復帰で踏んだ穴と同じ形）。
    [Fact]
    public void 鮮度切れでの決済だけがある場合は_劣化なしと書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(staleCloses: [
            new PositionClosedWithStaleFxRate("7203", Market.Japan, "JPY", 300, 0.0067m, T0.AddDays(-31), 31, T0),
        ])));

        md.Should().Contain("鮮度切れのレートで決済");
        md.Should().NotContain("鮮度警告もありません", "劣化した値で取引したなら劣化はあった");
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

    // 🔴 月報は**回数のみ**（直前後の節と同じ規律）。鮮度警告は暦日ごとに 1 件出るため、
    // 明細にすると月報で 20 行を超えて他の節を押し流す。
    [Fact]
    public void 月報は明細ではなく回数で出す()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            fellBacks: [new FxRateSourceFellBack("USD", "fred", 2, 2, T0)],
            restorations: [new FxRateSourcePrimaryRestored("USD", "boj", T0, T0.AddHours(3))],
            stales: [
                new FxRateStale("USD", T0.AddDays(-7), 7, 5, 30, T0),
                new FxRateStale("USD", T0.AddDays(-31), 31, 5, 30, T0, EntryBlocked: true),
            ],
            staleCloses: [
                new PositionClosedWithStaleFxRate("7203", Market.Japan, "JPY", 300, 0.0067m, T0.AddDays(-31), 31, T0),
            ]), ReportKind.Monthly));

        md.Should().Contain("フォールバックへの切替 1 件");
        md.Should().Contain("鮮度警告 2 件");
        // 🔴 **停止に至った回数を回数の中に埋めない。** 「警告 2 件」だけだと
        // **統制が発動した月と、警告どまりの月が同じに見える。**
        md.Should().Contain("うち新規建て停止 1 件");
        md.Should().Contain("鮮度切れでの決済 1 件");
        md.Should().Contain("該当日報を参照");
        // 明細の文言は出さない（出すと日報と同じ量になる）。
        md.Should().NotContain("観測日");
    }

    [Fact]
    public void 月報でも出典のクレジット表記は出す()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(Status(credits: [FxSourceCredits.Boj]), ReportKind.Monthly));

        md.Should().Contain("出典: " + FxSourceCredits.Boj);
    }

    // 🔴 期間より前から続いていたフォールバックが期間内に復帰した場合、FellBacks は空になる。
    // ここで IsClean を true にすると「劣化はありません」と復帰の明細が並んで出て、報告書が自己矛盾する。
    [Fact]
    public void 期間内に復帰だけがある場合は_劣化なしと書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(Status(
            restorations: [new FxRateSourcePrimaryRestored("USD", "boj", T0.AddDays(-2), T0)])));

        md.Should().Contain("第一の情報源へ復帰");
        md.Should().NotContain("鮮度警告もありません", "復帰があったなら期間内に劣化していた");
    }
}
