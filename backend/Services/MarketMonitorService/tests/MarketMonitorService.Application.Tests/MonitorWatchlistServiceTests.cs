using MarketMonitorService.Application.Adapters;
using MarketMonitorService.Application.Services;
using MarketMonitorService.Application.State;
using MarketMonitorService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace MarketMonitorService.Application.Tests;

// FR-03, FR-11, FR-13, UC-06: 監視銘柄の追加/削除サービスの検証・履歴・理由必須をユニットで確認する。
public class MonitorWatchlistServiceTests
{
    private static MonitorWatchlistService NewService(
        out InMemoryMonitoredSymbolStore store,
        out InMemoryMonitorSettingsChangeLog log,
        params MonitoredSymbol[] initial)
    {
        store = new InMemoryMonitoredSymbolStore(MonitorDefaults.CreateSettings(initial));
        log = new InMemoryMonitorSettingsChangeLog();
        return new MonitorWatchlistService(store, log, new FakeClock(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void 監視銘柄を追加できストアと履歴に反映される()
    {
        var svc = NewService(out var store, out var log);

        var result = svc.Add("AAPL", Market.UnitedStates, "endazon", "米株の監視開始");

        result.Should().ContainSingle(s => s.Symbol == "AAPL" && s.Market == Market.UnitedStates);
        store.GetSettings().MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "AAPL");
        log.GetHistory().Should().ContainSingle(e =>
            e.ChangeType == MonitorSettingsChangeType.WatchlistSymbolAdded
            && e.Actor == "endazon"
            && e.Reason == "米株の監視開始");
    }

    [Fact]
    public void 監視銘柄を削除できストアと履歴に反映される()
    {
        var svc = NewService(out var store, out var log,
            new MonitoredSymbol("AAPL", Market.UnitedStates));

        var result = svc.Remove("AAPL", Market.UnitedStates, "endazon", "監視終了");

        result.Should().BeEmpty();
        store.GetSettings().MonitoredSymbols.Should().BeEmpty();
        log.GetHistory().Should().ContainSingle(e =>
            e.ChangeType == MonitorSettingsChangeType.WatchlistSymbolRemoved);
    }

    [Fact]
    public void 重複追加は検証エラー()
    {
        var svc = NewService(out _, out var log,
            new MonitoredSymbol("AAPL", Market.UnitedStates));

        // 大文字小文字を無視して重複と判定する。
        var act = () => svc.Add("aapl", Market.UnitedStates, "endazon", "重複");

        act.Should().Throw<ArgumentException>();
        log.GetHistory().Should().BeEmpty();
    }

    [Fact]
    public void 同一銘柄でも市場が異なれば追加できる()
    {
        var svc = NewService(out var store, out _,
            new MonitoredSymbol("7203", Market.Japan));

        svc.Add("7203", Market.UnitedStates, "endazon", "別市場");

        store.GetSettings().MonitoredSymbols.Should().HaveCount(2);
    }

    [Fact]
    public void 不在銘柄の削除は検証エラー()
    {
        var svc = NewService(out _, out var log);

        var act = () => svc.Remove("MSFT", Market.UnitedStates, "endazon", "不在");

        act.Should().Throw<ArgumentException>();
        log.GetHistory().Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空の銘柄は検証エラー(string symbol)
    {
        var svc = NewService(out _, out _);

        var act = () => svc.Add(symbol, Market.UnitedStates, "endazon", "空");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 未定義の市場は検証エラー()
    {
        var svc = NewService(out _, out _);

        var act = () => svc.Add("AAPL", (Market)999, "endazon", "未定義市場");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 理由が空の追加は検証エラー(string reason)
    {
        var svc = NewService(out _, out _);

        var act = () => svc.Add("AAPL", Market.UnitedStates, "endazon", reason);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 理由が空の削除は検証エラー()
    {
        var svc = NewService(out _, out _,
            new MonitoredSymbol("AAPL", Market.UnitedStates));

        var act = () => svc.Remove("AAPL", Market.UnitedStates, "endazon", " ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void アクターが空の追加は検証エラー()
    {
        var svc = NewService(out _, out _);

        var act = () => svc.Add("AAPL", Market.UnitedStates, "", "理由");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 銘柄コードの前後空白は正規化される()
    {
        var svc = NewService(out var store, out _);

        svc.Add("  AAPL  ", Market.UnitedStates, "endazon", "空白正規化");

        store.GetSettings().MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "AAPL");
    }

    [Fact]
    public void 履歴は新しい順で返る()
    {
        var svc = NewService(out _, out _);

        svc.Add("AAPL", Market.UnitedStates, "endazon", "1件目");
        svc.Add("MSFT", Market.UnitedStates, "endazon", "2件目");

        var history = svc.GetHistory();
        history.Should().HaveCount(2);
        history[0].Reason.Should().Be("2件目");
        history[1].Reason.Should().Be("1件目");
    }
}
