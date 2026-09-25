using AwesomeAssertions;
using ReportService.Common.Abstractions;
using Xunit;

namespace ReportService.Tests;

// NFR, #947, IADR-0397: 本番の組み立てが IClock へ結線する本物（SystemClock）を試験で 1 度は通す。
// 組み立てガード（W3）の所見: 試験は偽の時計だけを使い、本物を 1 度も通していなかった ——
// 本体を固定時刻・現地時刻へ変えても既存の試験は全部緑のまま、本番の日時だけがずれる。
public class SystemClockTests
{
    [Fact]
    public void 本物の時計は現在のUTCを返す()
    {
        var before = DateTimeOffset.UtcNow;

        var now = new SystemClock().UtcNow;

        var after = DateTimeOffset.UtcNow;
        now.Offset.Should().Be(TimeSpan.Zero, "UtcNow はオフセット 0 の UTC である");
        now.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
