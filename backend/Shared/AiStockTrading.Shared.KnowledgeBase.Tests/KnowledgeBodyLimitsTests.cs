using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.KnowledgeBase.Tests;

// FR-08, #565, IADR-0272: 本文サイズ上限判定の純関数を境界値で固定する。
// 上限値・判定方法（UTF-8 バイト数）は platform DocumentBodyIntake.ExceedsLimit と同値でなければならない
// （送信側で緩く判定すると、platform 側の 413 を常に引いて無駄な往復になる）。
public class KnowledgeBodyLimitsTests
{
    [Fact]
    public void 上限ちょうどは超過しない_対の否定形()
    {
        var atLimit = new string('a', KnowledgeBodyLimits.MaxBytes);

        KnowledgeBodyLimits.Exceeds(atLimit).Should().BeFalse();
    }

    [Fact]
    public void 上限を1バイト超えると超過する_対の肯定形()
    {
        var overLimit = new string('a', KnowledgeBodyLimits.MaxBytes + 1);

        KnowledgeBodyLimits.Exceeds(overLimit).Should().BeTrue();
    }

    [Fact]
    public void 多バイト文字は文字数ではなくバイト数で判定する()
    {
        // 'あ' は UTF-8 で 3 バイト。文字数が上限の 3 分の 1 を超えた時点でバイト数は上限を超える。
        var multiByteOverLimit = new string('あ', KnowledgeBodyLimits.MaxBytes / 3 + 1);

        KnowledgeBodyLimits.Exceeds(multiByteOverLimit).Should().BeTrue();
    }

    [Fact]
    public void nullは超過しない_否定形()
    {
        KnowledgeBodyLimits.Exceeds(null).Should().BeFalse();
    }

    [Fact]
    public void 空文字は超過しない_否定形()
    {
        KnowledgeBodyLimits.Exceeds(string.Empty).Should().BeFalse();
    }
}
