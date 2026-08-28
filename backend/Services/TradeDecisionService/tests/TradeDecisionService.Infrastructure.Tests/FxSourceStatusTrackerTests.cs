using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

// FR-10, FR-17, #381, ADR-0022 決定2・決定5, IADR-0196 決定1: 「いつ発行するか」の判定。
//
// 🔴 **本テストが守っているのは「出ること」ではなく「出すぎないこと」である。**
// レート源は watchlist の銘柄ごと・巡回ごとに呼ばれる。判定を間違えると
// **1 巡回で N 件の Discord 通知が飛び、監査台帳が同じ事実で埋まる。**
public class FxSourceStatusTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    private static FxSourceStatusTracker NewTracker() => new();

    // 🔴 #513 / IADR-0225 決定A: **平常時に何も残らないのが元の課題だった。**
    // 遷移が無くても**暦日ごとに 1 件だけ**使用記録を出す（洪水は防いだまま）。
    [Fact]
    public void 第一の源が使えている間は_日次の使用記録を1件だけ発行する()
    {
        var tracker = NewTracker();

        var first = tracker.OnSourceUsed("USD", "boj", rank: 1, totalSources: 2, T0);

        first.Should().BeOfType<FxRateSourceUsed>("静かな期間の出典はこの記録からしか導けない");
        var e = (FxRateSourceUsed)first!;
        e.SourceName.Should().Be("boj");
        e.Rank.Should().Be(1);
        e.IsPrimary.Should().BeTrue("第一の情報源を使った証拠になる");

        tracker.OnSourceUsed("USD", "boj", rank: 1, totalSources: 2, T0.AddMinutes(1))
            .Should().BeNull("同じ暦日・同じ源なら 1 件だけ（1 巡回で N 件出さない）");
    }

    // 🔴 **洪水の否定形（使用記録の側）。** watchlist の銘柄数だけ呼ばれても 1 件で止まること。
    [Fact]
    public void 使用記録は同じ日に何度呼んでも1件しか出さない()
    {
        var tracker = NewTracker();
        tracker.OnSourceUsed("USD", "boj", 1, 2, T0).Should().NotBeNull();

        var published = Enumerable.Range(1, 50)
            .Select(i => tracker.OnSourceUsed("USD", "boj", 1, 2, T0.AddSeconds(i)))
            .Count(e => e is not null);

        published.Should().Be(0, "抑止の鍵は (通貨, 源, 暦日) である");
    }

    // 🔴 **境界。** 1 度出して終わりにすると、翌日以降の出典が再び証明できなくなる。
    [Fact]
    public void 使用記録は日をまたげば再び発行する()
    {
        var tracker = NewTracker();
        tracker.OnSourceUsed("USD", "boj", 1, 2, T0).Should().NotBeNull();

        tracker.OnSourceUsed("USD", "boj", 1, 2, T0.AddDays(1))
            .Should().BeOfType<FxRateSourceUsed>("暦日が変われば再び 1 件出る");
    }

    [Fact]
    public void 使用記録は通貨ごとに独立して1件ずつ出す()
    {
        var tracker = NewTracker();

        tracker.OnSourceUsed("USD", "boj", 1, 2, T0).Should().BeOfType<FxRateSourceUsed>();
        tracker.OnSourceUsed("EUR", "boj", 1, 2, T0).Should()
            .BeOfType<FxRateSourceUsed>("通貨ごとに出典を示す必要がある（片方の記録で他方を黙らせない）");
    }

    // 🔴 **鍵に源を含める。** フォールバック中に源が入れ替わっても遷移は起きないため、
    // 含めないと**実際に使った源が台帳に残らない**。
    [Fact]
    public void フォールバック中に源が入れ替われば_その源の使用記録を出す()
    {
        var tracker = NewTracker();
        tracker.OnSourceUsed("USD", "fred", 2, 3, T0).Should().BeOfType<FxRateSourceFellBack>();

        var used = tracker.OnSourceUsed("USD", "other", 3, 3, T0.AddHours(1));

        used.Should().BeOfType<FxRateSourceUsed>("遷移は出ないが、使った源は残さなければならない");
        ((FxRateSourceUsed)used!).SourceName.Should().Be("other");
    }

    // 🔴 **否定形（決定B）。** 遷移イベントは SourceName を運ぶ＝使用の証拠そのものであり、
    // 同じ日に使用記録を重ねると**同じ事実が 2 件台帳へ入る**。
    [Fact]
    public void 遷移を発行した日は_同じ源の使用記録を重ねない()
    {
        var tracker = NewTracker();
        tracker.OnSourceUsed("USD", "fred", 2, 2, T0).Should().BeOfType<FxRateSourceFellBack>();

        tracker.OnSourceUsed("USD", "fred", 2, 2, T0.AddHours(1))
            .Should().BeNull("切替の記録が、その日の使用の証拠を兼ねる");
    }

    [Fact]
    public void 使用記録の発行に失敗した状態を戻すと同日でも再発行できる()
    {
        var tracker = NewTracker();
        var used = tracker.OnSourceUsed("USD", "boj", 1, 2, T0)!;

        tracker.Rollback("USD", used);

        tracker.OnSourceUsed("USD", "boj", 1, 2, T0.AddMinutes(5))
            .Should().BeOfType<FxRateSourceUsed>("欠測より重複を採る（証拠が永久に欠けるほうが悪い）");
    }

    [Fact]
    public void フォールバックへ落ちた最初の1回だけ発行する()
    {
        var tracker = NewTracker();

        var first = tracker.OnSourceUsed("USD", "fred", rank: 2, totalSources: 2, T0);
        first.Should().BeOfType<FxRateSourceFellBack>();
        ((FxRateSourceFellBack)first!).SourceName.Should().Be("fred");
        ((FxRateSourceFellBack)first).Rank.Should().Be(2);
    }

    // 🔴 **洪水の否定形。** これが落ちると 1 巡回で N 件出る。
    [Fact]
    public void フォールバックが続いている間は再発行しない_同一巡回でN件出さない()
    {
        var tracker = NewTracker();
        tracker.OnSourceUsed("USD", "fred", rank: 2, totalSources: 2, T0).Should().NotBeNull();

        // watchlist の 50 銘柄ぶん呼ばれたのと同じこと。
        var published = Enumerable.Range(1, 50)
            .Select(i => tracker.OnSourceUsed("USD", "fred", 2, 2, T0.AddSeconds(i)))
            .Count(e => e is not null);

        published.Should().Be(0, "フォールバックが続いている間は黙る（続いていることは日報が期間で示す）");
    }

    [Fact]
    public void 第一へ戻ると回復を発行し_期間を持つ()
    {
        var tracker = NewTracker();
        tracker.OnSourceUsed("USD", "fred", rank: 2, totalSources: 2, T0);

        var restored = tracker.OnSourceUsed("USD", "boj", rank: 1, totalSources: 2, T0.AddHours(6));

        restored.Should().BeOfType<FxRateSourcePrimaryRestored>();
        var e = (FxRateSourcePrimaryRestored)restored!;
        e.FellBackAt.Should().Be(T0, "期間は受け手に引き算させず、発行側が事実として渡す");
        e.FallbackDuration.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public void 回復したあとも第一が続く間は再発行しない()
    {
        var tracker = NewTracker();
        tracker.OnSourceUsed("USD", "fred", 2, 2, T0);
        tracker.OnSourceUsed("USD", "boj", 1, 2, T0.AddHours(1)).Should().NotBeNull();

        tracker.OnSourceUsed("USD", "boj", 1, 2, T0.AddHours(2)).Should().BeNull();
    }

    // 落ちる→戻る→また落ちる。2 度目の期間が 1 度目に汚染されないこと。
    [Fact]
    public void 再度フォールバックすると新しい期間で始まる()
    {
        var tracker = NewTracker();
        tracker.OnSourceUsed("USD", "fred", 2, 2, T0);
        tracker.OnSourceUsed("USD", "boj", 1, 2, T0.AddHours(1));
        tracker.OnSourceUsed("USD", "fred", 2, 2, T0.AddHours(5));

        var restored = (FxRateSourcePrimaryRestored)tracker.OnSourceUsed("USD", "boj", 1, 2, T0.AddHours(7))!;

        restored.FellBackAt.Should().Be(T0.AddHours(5));
        restored.FallbackDuration.Should().Be(TimeSpan.FromHours(2));
    }

    // 通貨ごとに独立。USD が劣化していても JPY は正常であり得る。
    [Fact]
    public void 通貨ごとに状態を独立して持つ()
    {
        var tracker = NewTracker();

        tracker.OnSourceUsed("USD", "fred", 2, 2, T0).Should().NotBeNull();
        tracker.OnSourceUsed("EUR", "fred", 2, 2, T0).Should()
            .NotBeNull("通貨が違えば別の遷移である（片方の状態でもう片方を黙らせない）");
    }

    [Fact]
    public void 鮮度警告は同じ日に1回だけ発行する()
    {
        var tracker = NewTracker();
        var asOf = T0.AddDays(-7);

        tracker.OnStale("USD", asOf, TimeSpan.FromDays(7), TimeSpan.FromDays(5), TimeSpan.FromDays(30), T0)
            .Should().NotBeNull();
        tracker.OnStale("USD", asOf, TimeSpan.FromDays(7), TimeSpan.FromDays(5), TimeSpan.FromDays(30), T0.AddHours(3))
            .Should().BeNull();
    }

    // 🔴 **1 度出して終わりにしない。** 気づくための警告であり、翌日も鮮度切れなら再通知する。
    [Fact]
    public void 鮮度警告は日をまたげば再通知する()
    {
        var tracker = NewTracker();
        var asOf = T0.AddDays(-7);

        tracker.OnStale("USD", asOf, TimeSpan.FromDays(7), TimeSpan.FromDays(5), TimeSpan.FromDays(30), T0);

        tracker.OnStale("USD", asOf, TimeSpan.FromDays(8), TimeSpan.FromDays(5), TimeSpan.FromDays(30), T0.AddDays(1))
            .Should().NotBeNull("鮮度切れが続いているなら翌日も知らせる（1 度で終わると気づけなくなる）");
    }

    [Fact]
    public void 鮮度警告のイベントはしきい値と上限の両方を運ぶ()
    {
        var tracker = NewTracker();

        var e = tracker.OnStale(
            "USD", T0.AddDays(-7), TimeSpan.FromDays(7), TimeSpan.FromDays(5), TimeSpan.FromDays(30), T0)!;

        e.AgeDays.Should().Be(7);
        e.WarnThresholdDays.Should().Be(5);
        // 受け手が「あとどれだけで止まるか」を判断できること（ADR-0022 決定5）。
        e.MaxAgeDays.Should().Be(30);
    }

    // --- #381 停止側（IADR-0198 決定1・決定2） ---------------------------------------------------

    // 🔴 **昇格は同じ日でも知らせる。** 抑止の鍵に状態を含めていないと、
    // **朝に警告を出した通貨が夕方に停止域へ落ちても黙る**——統制の発動が埋もれる。
    [Fact]
    public void 警告から停止への昇格は同じ日でも通知する()
    {
        var tracker = NewTracker();
        var asOf = T0.AddDays(-31);

        tracker.OnStale("USD", asOf, TimeSpan.FromDays(7), TimeSpan.FromDays(5), TimeSpan.FromDays(30), T0)
            .Should().NotBeNull();

        var blocked = tracker.OnStale(
            "USD", asOf, TimeSpan.FromDays(31), TimeSpan.FromDays(5), TimeSpan.FromDays(30),
            T0.AddHours(6), entryBlocked: true);

        blocked.Should().NotBeNull("同じ日でも、警告から停止への昇格は知らせる必要がある");
        blocked!.EntryBlocked.Should().BeTrue();
    }

    // 🔴 **否定形: 洪水を戻していないこと。** 状態を鍵へ足したせいで抑止が緩んでいないか。
    [Fact]
    public void 停止域が続いている間は同じ日に1回だけ発行する()
    {
        var tracker = NewTracker();
        var asOf = T0.AddDays(-31);

        tracker.OnStale(
            "USD", asOf, TimeSpan.FromDays(31), TimeSpan.FromDays(5), TimeSpan.FromDays(30),
            T0, entryBlocked: true).Should().NotBeNull();

        var published = Enumerable.Range(1, 50)
            .Select(i => tracker.OnStale(
                "USD", asOf, TimeSpan.FromDays(31), TimeSpan.FromDays(5), TimeSpan.FromDays(30),
                T0.AddSeconds(i), entryBlocked: true))
            .Count(e => e is not null);

        published.Should().Be(0, "同じ状態が続く間は 1 日 1 回（状態を鍵へ足しても洪水は防いだまま）");
    }

    // 🔴 **否定形: 警告域で「止まった」と言わない。**
    [Fact]
    public void 警告域では新規建てを止めていないことをイベントが示す()
    {
        var tracker = NewTracker();

        var e = tracker.OnStale(
            "USD", T0.AddDays(-7), TimeSpan.FromDays(7), TimeSpan.FromDays(5), TimeSpan.FromDays(30), T0)!;

        e.EntryBlocked.Should().BeFalse("警告域は続行する（ADR-0022 決定5）");
    }

    // 発行に失敗したら状態を戻す。戻さないと「発行済み」が残り、二度と出なくなる。
    [Fact]
    public void 発行に失敗した状態を戻すと次の機会に再発行できる()
    {
        var tracker = NewTracker();
        var first = tracker.OnSourceUsed("USD", "fred", 2, 2, T0)!;

        tracker.Rollback("USD", first);

        tracker.OnSourceUsed("USD", "fred", 2, 2, T0.AddMinutes(5))
            .Should().BeOfType<FxRateSourceFellBack>("巻き戻したなら次の機会に再発行できる");
    }

    [Fact]
    public void 回復の発行に失敗した状態を戻すとフォールバック中に留まる()
    {
        var tracker = NewTracker();
        tracker.OnSourceUsed("USD", "fred", 2, 2, T0);
        var restored = tracker.OnSourceUsed("USD", "boj", 1, 2, T0.AddHours(3))!;

        tracker.Rollback("USD", restored);

        var again = tracker.OnSourceUsed("USD", "boj", 1, 2, T0.AddHours(4));
        again.Should().BeOfType<FxRateSourcePrimaryRestored>("回復を発行できていないなら、まだ回復を知らせていない");
        ((FxRateSourcePrimaryRestored)again!).FellBackAt.Should().Be(T0, "巻き戻しで元の開始時刻が戻る");
    }

    [Fact]
    public void 鮮度警告の発行に失敗した状態を戻すと同日でも再発行できる()
    {
        var tracker = NewTracker();
        var e = tracker.OnStale("USD", T0.AddDays(-7), TimeSpan.FromDays(7), TimeSpan.FromDays(5), TimeSpan.FromDays(30), T0)!;

        tracker.Rollback("USD", e);

        tracker.OnStale("USD", T0.AddDays(-7), TimeSpan.FromDays(7), TimeSpan.FromDays(5), TimeSpan.FromDays(30), T0.AddHours(1))
            .Should().NotBeNull();
    }
}
