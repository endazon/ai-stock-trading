using NotificationService.Domain;
using AwesomeAssertions;
using Xunit;

namespace NotificationService.Tests;

// FR-07, FR-14, UC-03〜05, #834, IADR-0240: `/report` の period 入力補完の候補選び（純関数）。
//
// 統制系の 3 点セット（`docs/tests/README.md`）:
//   境界値テーブル → 候補数（0・上限 25・上限超過）
//   プロパティベース → 返る候補は常に元の一覧の部分列であり、上限を超えない
//   否定形         → 値域外のキーを候補に出さない・推測補正をしない
public class ReportPeriodSuggestionsTests
{
    [Fact]
    public void 入力が空なら新しい順のまま返す()
    {
        var keys = new[] { "daily-2026-09-18", "weekly-2026-W38", "monthly-2026-09" };

        ReportPeriodSuggestions.Filter(keys, null).Should().Equal(keys);
        ReportPeriodSuggestions.Filter(keys, "   ").Should().Equal(keys);
    }

    [Fact]
    public void 前方一致が部分一致より前に来る()
    {
        // 利用者が "2026-09" と打ったとき、"monthly-2026-09" より先に来るべきものは無い（前方一致が無い）が、
        // "daily" と打てば daily- が先に立つ。前方一致 → 部分一致の順を固定する。
        var keys = new[] { "monthly-2026-09", "weekly-2026-W38", "daily-2026-09-18" };

        ReportPeriodSuggestions.Filter(keys, "daily")
            .Should().Equal("daily-2026-09-18");

        ReportPeriodSuggestions.Filter(keys, "2026-09")
            .Should().Equal("monthly-2026-09", "daily-2026-09-18");
    }

    [Fact]
    public void 大小文字を無視して絞り込む()
    {
        // 週報キーの W は大文字（#835）。利用者が小文字で打っても候補に出す（**候補は補正しない**——
        // 返るのは原文のキーであり、選べば大文字のまま報告書サービスへ渡る）。
        var keys = new[] { "weekly-2026-W38" };

        ReportPeriodSuggestions.Filter(keys, "weekly-2026-w")
            .Should().Equal("weekly-2026-W38");
    }

    [Fact]
    public void 日付だけの入力では該当する候補だけが残る()
    {
        // #834 の実測: 利用者は "2026-09-15" と打った。推測補正はしないが、部分一致で該当キーは出る。
        var keys = new[] { "daily-2026-09-18", "daily-2026-09-15", "weekly-2026-W38" };

        ReportPeriodSuggestions.Filter(keys, "2026-09-15")
            .Should().Equal("daily-2026-09-15");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(24, 24)]
    [InlineData(25, 25)]
    [InlineData(60, 25)]
    public void 候補は_Discord_の上限_25_件に収まる(int available, int expected)
    {
        // 境界値: Discord の入力補完は 1 回に 25 件までしか返せない（超えると API が拒否する）。
        var keys = Enumerable.Range(0, available).Select(i => $"daily-2026-07-{i:00}").ToArray();

        var suggestions = ReportPeriodSuggestions.Filter(keys, null);

        suggestions.Should().HaveCount(expected);
        ReportPeriodSuggestions.MaxChoices.Should().Be(25);
    }

    [Fact]
    public void 値域外のキーは候補に出さない()
    {
        // 否定形: BotCommandParser が受け付けない値を候補に出すと、選んだ結果が Unknown へ倒れて何も起きない。
        var keys = new[] { "../secrets", "daily-2026-09-18?x=1", "daily 2026", string.Empty, "daily-2026-09-18" };

        ReportPeriodSuggestions.Filter(keys, null).Should().Equal("daily-2026-09-18");
    }

    [Fact]
    public void 重複するキーは一度しか出さない()
    {
        var keys = new[] { "daily-2026-09-18", "daily-2026-09-18", "daily-2026-09-17" };

        ReportPeriodSuggestions.Filter(keys, null).Should().Equal("daily-2026-09-18", "daily-2026-09-17");
    }

    [Theory]
    [InlineData("")]
    [InlineData("d")]
    [InlineData("daily")]
    [InlineData("2026")]
    [InlineData("存在しない")]
    public void 返る候補は常に元の一覧の部分集合で上限を超えない(string input)
    {
        // プロパティベース: どんな入力でも、候補は（a）元の一覧に含まれ（b）上限を超えず（c）補正されない。
        var keys = Enumerable.Range(1, 40).Select(i => $"daily-2026-08-{i % 28 + 1:00}").Distinct().ToArray();

        var suggestions = ReportPeriodSuggestions.Filter(keys, input);

        suggestions.Should().OnlyContain(k => keys.Contains(k));
        suggestions.Count.Should().BeLessThanOrEqualTo(ReportPeriodSuggestions.MaxChoices);
    }
}
