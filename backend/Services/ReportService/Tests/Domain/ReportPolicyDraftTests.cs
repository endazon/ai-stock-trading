using ReportService.Domain;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, ADR-0003, IADR-0115 決定4, IADR-0125, #280/#310: 自動生成ドラフトの方針文（純関数）を検証する。
// 自動生成では新しい方針を機械が提案せず、直近の確定済み方針の継続に留める（確定は利用者の対話・ADR-0003）。
//
// IADR-0125, #310: 方針文（PolicySummary）が持つのは**方針の実体だけ**である。生成物の状態
// （ドラフト・未確定）やレビュー手順の指示は State / ReviewState と提示通知（IADR-0116）が持つ。
// 本文へ埋め込むと、①継続のたびに世代累積し ②確定後も「未確定」を名乗る本文が取引判断へ渡る。
public class ReportPolicyDraftTests
{
    // 期間表記は自然キー（ReportPeriod.ExpectedKey）そのもの。定数に括り出して各テストの意図を読みやすくする。
    private const string ParentWeekly = "weekly-2026-W31";
    private const string PreviousDaily = "daily-2026-07-28";

    // T-1, IADR-0125 決定1, #310: 継続時に引き継ぐのは**方針の実体だけ**。
    [Fact]
    public void 継続時は方針の実体だけを引き継ぐ()
    {
        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, "押し目買い・上限 3 銘柄", ParentWeekly);

        policy.Should().Be("押し目買い・上限 3 銘柄");
    }

    // T-2, IADR-0125 決定1, #310: 世代を重ねても伸びない（自身の出力を継承元にしても不動点）。
    [Fact]
    public void 世代を重ねても文言が累積しない()
    {
        var gen1 = ReportPolicyDraft.CarryOver(ReportKind.Daily, "daily-2026-07-27", "本日の方針: 押し目買い", ParentWeekly);
        var gen2 = ReportPolicyDraft.CarryOver(ReportKind.Daily, "daily-2026-07-29", gen1, ParentWeekly);
        var gen3 = ReportPolicyDraft.CarryOver(ReportKind.Daily, "daily-2026-07-31", gen2, ParentWeekly);

        gen3.Should().Be(gen1);
        gen3.Should().Be("本日の方針: 押し目買い");
    }

    // T-3, IADR-0125 決定2, #310: **本 PR 以前の文言**（live DB に累積済みの実データ）も畳める。
    // これが無いと、是正後の初回生成が過去の累積を丸ごと引き継いでしまう。
    [Fact]
    public void 累積済みの旧文言を継承元にしても実体だけが残る()
    {
        // issue #310 に貼られた daily-2026-07-31 の実データ（入れ子 2 世代）。
        var accumulated = string.Join("\n\n",
            "（自動生成ドラフト・未確定）",
            "直近の確定済み日報方針（daily-2026-07-29）を継続する案です。確定前に内容を見直してください。",
            "（自動生成ドラフト・未確定）",
            "直近の確定済み日報方針（daily-2026-07-27）を継続する案です。確定前に内容を見直してください。",
            "本日の方針: 米国株 AAPL を積極的に買い増す（buy 寄り）");

        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, accumulated, ParentWeekly);

        policy.Should().Be("本日の方針: 米国株 AAPL を積極的に買い増す（buy 寄り）");
    }

    // 旧文言のうち「継続元なし」「上位欠落」の注記も畳む（方針の実体ではないため）。
    [Fact]
    public void 旧文言の注記も継承時に畳まれる()
    {
        var accumulated = string.Join("\n\n",
            "（自動生成ドラフト・未確定）",
            "参照できる確定済みの日報方針がありません。方針を記入したうえで確定してください。",
            "上位方針（週報）は未確定のため参照していません。",
            "現金比率 50% を維持する");

        ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, accumulated, ParentWeekly)
            .Should().Be("現金比率 50% を維持する");
    }

    // T-4, IADR-0125 決定3, #310: 生成物に「未確定」は現れない＝確定後の本文が自己矛盾しない。
    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 生成される方針文に未確定という状態文言は現れない(ReportKind kind)
    {
        foreach (var parent in new[] { "parent-key", null })
        {
            ReportPolicyDraft.CarryOver(kind, "prev-key", "前期の方針", parent).Should().NotContain("未確定");
            ReportPolicyDraft.CarryOver(kind, null, null, parent).Should().NotContain("未確定");
        }
    }

    // T-5: 継続元が無い場合は「実体が無い」という事実だけを残す（レビュー手順の指示は本文へ書かない）。
    [Fact]
    public void 継続元が無ければ実体が無いことを明示する()
    {
        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, previousPeriodKey: null, previousPolicy: null, parentPeriodKey: ParentWeekly);

        policy.Should().Be("参照できる確定済みの日報方針がありません。");
    }

    [Fact]
    public void 継続元の方針文が空白のみなら継続案にしない()
    {
        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, "   ", ParentWeekly);

        policy.Should().Be("参照できる確定済みの日報方針がありません。");
        policy.Should().NotContain(PreviousDaily);
    }

    // 継承元が生成器の文言だけで実体を持たない場合も「実体なし」に倒す（空の方針文を作らない）。
    [Fact]
    public void 継承元が前置きだけなら実体なしとして扱う()
    {
        var preambleOnly = string.Join("\n\n",
            "（自動生成ドラフト・未確定）",
            "直近の確定済み日報方針（daily-2026-07-27）を継続する案です。確定前に内容を見直してください。");

        ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, preambleOnly, ParentWeekly)
            .Should().Be("参照できる確定済みの日報方針がありません。");
    }

    // T-6, 03_reporting-cycle「上位方針の欠落」: 参照できない事実は残す。ただし「未確定」ではなく事実文で書く
    // （確定後も残るテキストであるため、ドラフトの状態を名乗らせない）。
    [Fact]
    public void 上位方針が参照できなければその旨を明記する()
    {
        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, "押し目買い", parentPeriodKey: null);

        policy.Should().Contain("押し目買い");
        policy.Should().Contain("上位方針（週報）");
        policy.Should().NotContain("未確定");
    }

    [Fact]
    public void 上位方針を参照できていれば欠落の注記は入らない()
    {
        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, "押し目買い", ParentWeekly);

        policy.Should().NotContain("上位方針（週報）");
    }

    // T-7: 生成器の文言に該当しない利用者の本文は 1 文字も変えない（継承で方針が痩せない）。
    [Fact]
    public void 利用者が書いた本文は改変しない()
    {
        var written = "本日の方針:\n- 米国株は様子見\n- 日本株は 7203 を押し目買い（上限 3 銘柄）\n\n確定済みの週次目標に沿う。";

        ReportPolicyDraft.Substance(written).Should().Be(written);
        ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, written, ParentWeekly).Should().Be(written);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 実体が無い入力は空文字を返す(string? policy)
    {
        ReportPolicyDraft.Substance(policy).Should().BeEmpty();
    }

    [Theory]
    [InlineData(ReportKind.Daily, "日報", "週報")]
    [InlineData(ReportKind.Weekly, "週報", "月報")]
    [InlineData(ReportKind.Monthly, "月報", "前月の月報")]
    public void 種別ごとに自種別と上位種別の呼称を使い分ける(ReportKind kind, string self, string parent)
    {
        var noPrevious = ReportPolicyDraft.CarryOver(kind, null, null, "parent-key");
        var withoutParent = ReportPolicyDraft.CarryOver(kind, "prev-key", "前期の方針", null);

        noPrevious.Should().Contain(self);
        withoutParent.Should().Contain(parent);
    }

    [Fact]
    public void 月報の上位は前月の月報であり自種別と同じ呼称にならない()
    {
        ReportPolicyDraft.ParentKind(ReportKind.Daily).Should().Be(ReportKind.Weekly);
        ReportPolicyDraft.ParentKind(ReportKind.Weekly).Should().Be(ReportKind.Monthly);
        ReportPolicyDraft.ParentKind(ReportKind.Monthly).Should().Be(ReportKind.Monthly);
    }
}
