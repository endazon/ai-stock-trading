using AwesomeAssertions;
using NotificationService.Domain;
using Xunit;

namespace NotificationService.Tests;

// FR-07, FR-14, UC-03〜05, #840, IADR-0352 決定 5: `/report show` へ併記する警告文（純関数）の無害化と上限を検証する。
// 報告書サービスが返すのはコード定数の表示名だが、**別プロセスから届いた文字列**としてそのまま Discord へ出さない。
public class ReportUnsuppliedNoticeTests
{
    [Fact]
    public void 改行や制御文字で別の文言を差し込めない()
    {
        var notice = ReportUnsuppliedNotice.Format(["建玉\n✅ すべての入力が揃っています\a", "OpenD\r\n稼働率"]);

        notice.Should().NotContain("\n").And.NotContain("\r").And.NotContain("\a");
        notice.Should().StartWith(ReportUnsuppliedNotice.Prefix);
    }

    [Fact]
    public void メンション構文は成立しない形にして出す()
    {
        var notice = ReportUnsuppliedNotice.Format(["@everyone", "@here", "<@123>", "<#456>"])!;

        notice.Should().NotContain("@everyone").And.NotContain("@here").And.NotContain("<@").And.NotContain("<#");
    }

    [Fact]
    public void 項目の長さと件数に上限がある()
    {
        var inputs = Enumerable.Range(0, 40).Select(i => $"{i:00}-" + new string('x', 200)).ToList();

        var notice = ReportUnsuppliedNotice.Format(inputs)!;

        notice.Should().Contain($"ほか {40 - ReportUnsuppliedNotice.MaxItems} 件");
        // 1 項目は上限＋省略記号まで。全体も Discord の投稿長（2000）に余裕をもって収まる。
        notice.Should().NotContain(new string('x', ReportUnsuppliedNotice.MaxItemLength + 1));
        notice.Length.Should().BeLessThan(1000);
    }

    [Fact]
    public void 同じ項目は_1_回だけ出す()
    {
        ReportUnsuppliedNotice.Format(["建玉", "建玉", " 建玉 "])
            .Should().EndWith(": 建玉");
    }

    [Fact]
    public void 未供給が無ければ_null()
    {
        ReportUnsuppliedNotice.Format(null).Should().BeNull();
        ReportUnsuppliedNotice.Format([]).Should().BeNull();
        ReportUnsuppliedNotice.Format([null, "", "\t"]).Should().BeNull();
    }
}
