using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Features.MarketMonitor.GetWatchlistAsOf;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-04, FR-13, FR-15, ADR-0044 決定 3, ADR-0046 決定 1・2, #1049, IADR-0442 決定 1: 当時の監視銘柄の再構成（純関数）。
// 前後値の文字列は本物の書き手（MonitorWatchlistService.Render）と同じ書式で書く。本物の書き手との往復は T-10-1626 が固定する。
public class WatchlistAsOfReconstructorTests
{
    private static readonly DateTimeOffset Seeded = new(2026, 9, 15, 16, 30, 52, TimeSpan.Zero);
    private static readonly DateTimeOffset First = new(2026, 9, 25, 18, 9, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Second = new(2026, 9, 25, 18, 10, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    private const string SeedList = "AAPL@UnitedStates, MSFT@UnitedStates";
    private const string AfterFirst = "AAPL@UnitedStates, MSFT@UnitedStates, META@UnitedStates";
    private const string AfterSecond = "MSFT@UnitedStates, META@UnitedStates";

    private static MonitorSettingsChangeEntry Added(DateTimeOffset at, string before, string after) =>
        new("owner", MonitorSettingsChangeType.WatchlistSymbolAdded, "理由", at, before, after);

    private static MonitorSettingsChangeEntry Removed(DateTimeOffset at, string before, string after) =>
        new("owner", MonitorSettingsChangeType.WatchlistSymbolRemoved, "理由", at, before, after);

    // 本物の台帳と同じく新しい順で渡す（再構成器は並びに依存しない）。
    private static IReadOnlyList<MonitorSettingsChangeEntry> TwoChanges() =>
        [Removed(Second, AfterFirst, AfterSecond), Added(First, SeedList, AfterFirst)];

    private static IReadOnlyList<MonitoredSymbol> Parse(string rendering) =>
        [.. rendering.Split(", ").Select(i => new MonitoredSymbol(i[..i.IndexOf('@')], Enum.Parse<Market>(i[(i.IndexOf('@') + 1)..])))];

    private static MonitorSeedState Seed(DateTimeOffset? seededAt, string current = AfterSecond) =>
        new(seededAt, Parse(current));

    private static WatchlistAsOfResponse At(
        DateTimeOffset at, IReadOnlyList<MonitorSettingsChangeEntry>? history = null, MonitorSeedState? seed = null) =>
        WatchlistAsOfReconstructor.Reconstruct(at, Now, history ?? TwoChanges(), seed ?? Seed(Seeded));

    // ---- T-10-1623: その時点以前で最後の変更の変更後（ADR-0044 決定 3） ----

    [Fact]
    public void 変更の間の時点は直前の変更の変更後の一覧になる()
    {
        var result = At(First.AddSeconds(30));

        result.Reconstructed.Should().BeTrue();
        result.Symbols.Should().Equal(Parse(AfterFirst));
        result.Basis.Should().Be(WatchlistAsOfResponse.Bases.AfterChange);
        result.BasisChangedAt.Should().Be(First);
        result.SeededAt.Should().Be(Seeded);
    }

    // 🔴 T-10-1623 境界値: 変更と**同じ時刻は含む**。1 tick 前は含まない（前の一覧のまま）。
    [Fact]
    public void 変更と同じ時刻は変更後を使い1tick前は変更前のままである()
    {
        At(Second).Symbols.Should().Equal(Parse(AfterSecond));
        At(Second - Tick).Symbols.Should().Equal(Parse(AfterFirst));
        At(First).Symbols.Should().Equal(Parse(AfterFirst));
        At(First - Tick).Symbols.Should().Equal(Parse(SeedList), "SeededAt 以降・最初の変更より前は変更前（T-10-1625）");
    }

    // T-10-1623: 最後の変更の後は、その変更後（現在の一覧と一致することを確かめたうえで）。
    [Fact]
    public void 最後の変更の後は現在の一覧と一致する変更後になる()
    {
        var result = At(Now);

        result.Reconstructed.Should().BeTrue();
        result.Symbols.Should().Equal(Parse(AfterSecond));
        result.BasisChangedAt.Should().Be(Second);
    }

    // T-10-1623: 変動閾値・クールダウンの行は監視銘柄の再構成に使わない（前後値は数値で、一覧として読まない）。
    [Fact]
    public void 閾値とクールダウンの行は無視する()
    {
        IReadOnlyList<MonitorSettingsChangeEntry> history =
        [
            new("owner", MonitorSettingsChangeType.MovementThresholdChanged, "理由", Second.AddMinutes(1), "0.03", "0.05"),
            new("owner", MonitorSettingsChangeType.CooldownChanged, "理由", Second.AddMinutes(2), "00:15:00", "00:30:00"),
            .. TwoChanges(),
        ];

        At(Now, history).Symbols.Should().Equal(Parse(AfterSecond));
    }

    // T-10-1623: 当時 0 件だったことは事実として返す（再構成できないのではない）。
    [Fact]
    public void 変更後が空なら0件の一覧を返す()
    {
        IReadOnlyList<MonitorSettingsChangeEntry> history = [Removed(First, "AAPL@UnitedStates", "(なし)")];

        var result = WatchlistAsOfReconstructor.Reconstruct(
            Now, Now, history, new MonitorSeedState(Seeded, []));

        result.Reconstructed.Should().BeTrue();
        result.Symbols.Should().BeEmpty();
    }

    // ---- 🔴 T-10-1624: 一貫性が取れない形はすべて「再構成できない」（空の一覧へ倒さない） ----

    [Fact]
    public void 同じ時刻で変更後が食い違えば再構成できない()
    {
        IReadOnlyList<MonitorSettingsChangeEntry> history =
        [
            Added(First, SeedList, AfterFirst),
            Added(First, AfterFirst, "AAPL@UnitedStates, MSFT@UnitedStates, META@UnitedStates, NVDA@UnitedStates"),
        ];

        var result = At(First.AddSeconds(1), history, Seed(Seeded, current: "AAPL@UnitedStates, MSFT@UnitedStates, META@UnitedStates, NVDA@UnitedStates"));

        result.Reconstructed.Should().BeFalse();
        result.Symbols.Should().BeNull();
        result.Reason.Should().Contain("食い違");
    }

    // 対照: 全置換は追加・削除の 2 行を同じ時刻・同じ前後値で記録する。これは食い違いではない。
    [Fact]
    public void 同じ時刻で前後値が同じ2行は1つの変更として読む()
    {
        IReadOnlyList<MonitorSettingsChangeEntry> history =
        [
            Added(First, SeedList, "MSFT@UnitedStates, NVDA@UnitedStates"),
            Removed(First, SeedList, "MSFT@UnitedStates, NVDA@UnitedStates"),
        ];

        var result = At(Now, history, Seed(Seeded, current: "MSFT@UnitedStates, NVDA@UnitedStates"));

        result.Reconstructed.Should().BeTrue();
        result.Symbols.Should().Equal(Parse("MSFT@UnitedStates, NVDA@UnitedStates"));
    }

    [Fact]
    public void 次の変更の変更前と合わなければ再構成できない()
    {
        IReadOnlyList<MonitorSettingsChangeEntry> history =
        [
            Added(First, SeedList, AfterFirst),
            Removed(Second, "TSLA@UnitedStates", AfterSecond), // 履歴の外で一覧が変わった形
        ];

        var result = At(First.AddSeconds(30), history);

        result.Reconstructed.Should().BeFalse();
        result.Reason.Should().Contain("次の変更");
    }

    [Fact]
    public void 最後の変更の後で現在の一覧と合わなければ再構成できない()
    {
        var result = At(Now, seed: Seed(Seeded, current: "TSLA@UnitedStates"));

        result.Reconstructed.Should().BeFalse();
        result.Reason.Should().Contain("現在の一覧");
    }

    [Fact]
    public void 最後の変更の後で行が無ければ再構成できない()
    {
        var result = WatchlistAsOfReconstructor.Reconstruct(Now, Now, TwoChanges(), seed: null);

        result.Reconstructed.Should().BeFalse();
        result.SeededAt.Should().BeNull();
    }

    [Theory]
    [InlineData("AAPL")] // @ が無い
    [InlineData("@UnitedStates")] // 銘柄が空
    [InlineData("AAPL@Mars")] // 未定義の市場名
    [InlineData("AAPL@1")] // 数値の市場
    [InlineData("AAPL@UnitedStates,MSFT@UnitedStates")] // 区切りが違う（銘柄に @ と , が入った形として読めてしまう）
    [InlineData("AAPL@UnitedStates, ")] // 空の要素
    public void 前後値を一覧へ戻せなければ再構成できない(string rendering)
    {
        // 連続性の検査は通る形にして（次の変更の変更前が同じ文字列）、読み戻しだけを見る。
        IReadOnlyList<MonitorSettingsChangeEntry> history =
            [Added(First, SeedList, rendering), Added(Second, rendering, AfterSecond)];

        var result = At(First.AddSeconds(1), history);

        result.Reconstructed.Should().BeFalse();
        result.Reason.Should().Contain("読み戻せません");
    }

    [Fact]
    public void 前後値がnullなら再構成できない()
    {
        IReadOnlyList<MonitorSettingsChangeEntry> history =
            [new("owner", MonitorSettingsChangeType.WatchlistSymbolAdded, "理由", First, SeedList, null)];

        At(First.AddSeconds(1), history).Reconstructed.Should().BeFalse();
    }

    [Fact]
    public void 未来の時点は再構成できない()
    {
        var result = At(Now + Tick);

        result.Reconstructed.Should().BeFalse();
        result.Reason.Should().Contain("未来");
        result.SeededAt.Should().Be(Seeded, "SeededAt は再構成できないときも返す（ADR-0046 決定 1）");
    }

    // ---- 🔴 T-10-1625: 最初の変更より前と、変更が無いとき（ADR-0046 決定 1・2） ----

    // SeededAt から最初の変更までの時点は、最初の変更の変更前。境界 at = SeededAt は含む。
    [Fact]
    public void SeededAtから最初の変更までは最初の変更の変更前を使う()
    {
        foreach (var at in new[] { Seeded, Seeded.AddDays(3), First - Tick })
        {
            var result = At(at);
            result.Reconstructed.Should().BeTrue($"{at:O} は SeededAt 以降・最初の変更より前");
            result.Symbols.Should().Equal(Parse(SeedList));
            result.Basis.Should().Be(WatchlistAsOfResponse.Bases.BeforeFirstChange);
            result.BasisChangedAt.Should().Be(First);
        }
    }

    [Fact]
    public void SeededAtより前は再構成できない()
    {
        var result = At(Seeded - Tick);

        result.Reconstructed.Should().BeFalse();
        result.Symbols.Should().BeNull("最初の変更の変更前があっても、SeededAt より前には使わない（ADR-0046 決定 1）");
        result.Reason.Should().Contain("SeededAt より前");
        result.SeededAt.Should().Be(Seeded);
    }

    [Fact]
    public void SeededAtがnullなら最初の変更より前は再構成できず推測で埋めない()
    {
        var result = At(First - TimeSpan.FromDays(1), seed: Seed(seededAt: null));

        result.Reconstructed.Should().BeFalse();
        result.SeededAt.Should().BeNull("推測で埋めない（ADR-0046 決定 2）");
        result.Reason.Should().Contain("推測");
        // 最初の変更の後は SeededAt が無くても変更後で再構成できる（下限の規則は最初の変更より前だけに掛かる）。
        At(Now, seed: Seed(seededAt: null)).Reconstructed.Should().BeTrue();
    }

    [Fact]
    public void 行が無ければ最初の変更より前は再構成できない()
    {
        WatchlistAsOfReconstructor.Reconstruct(First - Tick, Now, TwoChanges(), seed: null)
            .Reconstructed.Should().BeFalse();
    }

    [Fact]
    public void SeededAtが最初の変更より後なら矛盾として最初の変更より前は再構成できない()
    {
        var contradictory = Seed(seededAt: First + TimeSpan.FromHours(1));

        var result = At(First - Tick, seed: contradictory);

        result.Reconstructed.Should().BeFalse();
        result.Reason.Should().Contain("矛盾");
        // 境界: SeededAt が最初の変更と同じ時刻なら矛盾ではない。
        At(First - Tick, seed: Seed(seededAt: First)).Reconstructed.Should().BeFalse("at は SeededAt より前");
        At(First, seed: Seed(seededAt: First)).Reconstructed.Should().BeTrue();
    }

    [Fact]
    public void 変更が無ければSeededAt以降は現在の一覧でより前とnullは再構成できない()
    {
        var seed = Seed(Seeded, current: SeedList);

        var after = WatchlistAsOfReconstructor.Reconstruct(Seeded, Now, [], seed);
        after.Reconstructed.Should().BeTrue();
        after.Symbols.Should().Equal(Parse(SeedList));
        after.Basis.Should().Be(WatchlistAsOfResponse.Bases.SeedWithoutChanges);

        WatchlistAsOfReconstructor.Reconstruct(Seeded - Tick, Now, [], seed).Reconstructed.Should().BeFalse();
        WatchlistAsOfReconstructor.Reconstruct(Now, Now, [], Seed(seededAt: null, current: SeedList))
            .Reconstructed.Should().BeFalse();
        WatchlistAsOfReconstructor.Reconstruct(Now, Now, [], seed: null).Reconstructed.Should().BeFalse();
    }
}
