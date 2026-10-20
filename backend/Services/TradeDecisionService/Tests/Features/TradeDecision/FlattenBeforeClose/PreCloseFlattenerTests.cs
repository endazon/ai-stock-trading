extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.FlattenBeforeClose;
using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// T-10-2927〜T-10-2931, T-10-2933, FR-10, FR-19, #1396, IADR-0561 決定 3・4: 閉場前の手仕舞い。既定は無効で、構成か方針の決まった書式の行で有効になり、
// 閉場の M 分前以降の場中に保有の全量を成行で手仕舞う判断（Close・MarketOrder）を作る。「持ち越し可」に明記した銘柄は残す。
// 閉場は本番と同じ市場カレンダー（MarketCalendar＝共有カーネルの MarketHours）で求め、休場日・半日取引日を固定する。
public class PreCloseFlattenerTests
{
    // 2026-10-19（月）夏時間: 閉場 16:00 ET＝20:00 UTC。手仕舞いの窓は 15:45 ET＝19:45 UTC から。
    private static readonly DateTimeOffset InWindow = new(2026, 10, 19, 19, 50, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeWindow = new(2026, 10, 19, 19, 44, 0, TimeSpan.Zero);

    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.MoomooSimulate, TradingDefaults.CreateRiskLimits());

    private static PreCloseFlattenOptions Enabled => PreCloseFlattenOptions.Default with { Enabled = true };

    private sealed class Fixture
    {
        public DateTimeOffset Now { get; set; } = InWindow;
        public string? PolicyText { get; set; } = "方針";
        public List<WatchedSymbol>? Held { get; set; } = [new("MARA", Market.UnitedStates), new("NVDA", Market.UnitedStates)];
        public Dictionary<string, HeldPosition?> Positions { get; } = new()
        {
            ["MARA"] = new HeldPosition(150, 20.5m, 19.8m),
            ["NVDA"] = new HeldPosition(-10, 180m, 185m),
        };
        public HashSet<string> Covered { get; } = [];
        public HashSet<string> AnyOrderBlocked { get; } = [];
        public decimal? Rate { get; set; } = 1m;
        public decimal? Current { get; set; } = 21m;

        public PreCloseFlattener Build(PreCloseFlattenOptions? options = null) => new(
            options ?? PreCloseFlattenOptions.Default,
            new MarketCalendar(new Dictionary<Market, IReadOnlySet<DateOnly>>()),
            new PolicyStub(this), new HeldSymbolsStub(this), new PositionsStub(this), new InFlightStub(this), new BlockersStub(this),
            new FxStub(this), new PriceStub(this), new SizingStub(), new ClockStub(this), NullLogger<PreCloseFlattener>.Instance);

        private sealed class ClockStub(Fixture f) : IClock { public DateTimeOffset UtcNow => f.Now; }

        private sealed class PolicyStub(Fixture f) : IDailyPolicyProvider
        {
            public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) =>
                Task.FromResult(f.PolicyText is null ? null : new DailyPolicy(new DateOnly(2026, 10, 19), f.PolicyText));
        }

        private sealed class HeldSymbolsStub(Fixture f) : IHeldSymbolsProvider
        {
            public Task<IReadOnlyList<WatchedSymbol>?> GetHeldSymbolsAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<WatchedSymbol>?>(f.Held);
        }

        private sealed class PositionsStub(Fixture f) : IHeldPositionProvider
        {
            public bool IsEnabled => true;

            public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
                Task.FromResult(f.Positions.GetValueOrDefault(symbol)?.SignedQuantity);

            public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
                Task.FromResult(f.Positions.TryGetValue(symbol, out var p) ? p : HeldPosition.None);

            public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(string symbol, Market market, CancellationToken ct = default) =>
                Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
        }

        private sealed class InFlightStub(Fixture f) : IInFlightCloseProvider
        {
            public Task<InFlightCloseCoverage?> GetAsync(string symbol, Market market, TradeSide closeSide, CancellationToken ct = default) =>
                Task.FromResult<InFlightCloseCoverage?>(new InFlightCloseCoverage(f.Covered.Contains(symbol), 0, []));
        }

        private sealed class BlockersStub(Fixture f) : IEntryBlockersProvider
        {
            public Task<EntryBlockers?> GetAsync(string symbol, Market market, CancellationToken ct = default) =>
                Task.FromResult<EntryBlockers?>(f.AnyOrderBlocked.Contains(symbol)
                    ? new EntryBlockers([], [], [RejectionReason.BannedSymbol])
                    : EntryBlockers.None);
        }

        private sealed class FxStub(Fixture f) : IFxRateProvider
        {
            public Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken ct = default) => Task.FromResult(f.Rate);
        }

        private sealed class PriceStub(Fixture f) : ICurrentPriceProvider
        {
            public bool IsEnabled => true;

            public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
                Task.FromResult(f.Current is { } p ? new CurrentPriceReading(p, IntradayPriceContext.Unknown) : null);
        }

        private sealed class SizingStub : ISizingContextProvider
        {
            public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(Context);
        }
    }

    private static Task<IReadOnlyList<AiStockTrading.Shared.Contracts.Events.TradeDecisionMade>> Run(Fixture f, PreCloseFlattenOptions? options = null) =>
        f.Build(options).FlattenAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task T_10_2927_既定は無効で窓の中でも手仕舞わない()
    {
        PreCloseFlattenOptions.Default.Enabled.Should().BeFalse();

        (await Run(new Fixture())).Should().BeEmpty();
    }

    [Fact]
    public async Task T_10_2928_構成で有効なら閉場の15分前以降に全建玉を成行で全量手仕舞う()
    {
        var decisions = await Run(new Fixture(), Enabled);

        decisions.Should().HaveCount(2);
        var mara = decisions.Single(d => d.Intent.Symbol == "MARA").Intent;
        mara.PositionEffect.Should().Be(PositionEffect.Close);
        mara.MarketOrder.Should().BeTrue("閉場前の手仕舞いは成行");
        mara.Side.Should().Be(TradeSide.Sell);
        mara.Quantity.Should().Be(150, "保有の全量");
        mara.Price.Should().Be(21m, "参照価格は現在値");
        mara.Mode.Should().Be(BrokerProvider.MoomooSimulate);
        mara.StopLossPrice.Should().BeNull();
        var nvda = decisions.Single(d => d.Intent.Symbol == "NVDA").Intent;
        nvda.Side.Should().Be(TradeSide.Buy, "ショートの手仕舞いは買い戻し");
        nvda.Quantity.Should().Be(10);
        decisions.Should().OnlyContain(d => d.Rationale.StartsWith(PreCloseFlattener.RationalePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task T_10_2929_方針の閉場前の手仕舞い有効の行でも有効になる()
    {
        var f = new Fixture { PolicyText = "日中のみ保有する。\n- 閉場前の手仕舞い: 有効" };

        (await Run(f)).Should().HaveCount(2);
    }

    [Fact]
    public async Task T_10_2929_方針の自由文では有効にならない()
    {
        var f = new Fixture { PolicyText = "保有は原則日中のみとし、取引終盤は新規建てを控えて手仕舞いを優先する" };

        (await Run(f)).Should().BeEmpty();
    }

    [Fact]
    public async Task T_10_2930_持ち越し可に明記した銘柄だけを残す()
    {
        var f = new Fixture { PolicyText = "閉場前の手仕舞い: 有効\n持ち越し可: nvda" };

        (await Run(f)).Should().ContainSingle().Which.Intent.Symbol.Should().Be("MARA");
    }

    // 方針が読めない（未確定）ときは、構成で有効なら持ち越しの指定を確かめられないので全建玉を手仕舞う。
    [Fact]
    public async Task T_10_2930_方針が無くても構成で有効なら全建玉を手仕舞う()
    {
        (await Run(new Fixture { PolicyText = null }, Enabled)).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(19, 44)] // 15:44 ET（窓の前）
    [InlineData(20, 0)]  // 16:00 ET（閉場。場中ではない）
    [InlineData(21, 0)]  // 閉場の後
    public async Task T_10_2931_窓の外と閉場の後は手仕舞わない(int hour, int minute)
    {
        var f = new Fixture { Now = new DateTimeOffset(2026, 10, 19, hour, minute, 0, TimeSpan.Zero) };

        (await Run(f, Enabled)).Should().BeEmpty();
    }

    [Fact]
    public async Task T_10_2931_窓に入るのは閉場の15分前ちょうどから()
    {
        var f = new Fixture { Now = new DateTimeOffset(2026, 10, 19, 19, 45, 0, TimeSpan.Zero) };

        (await Run(f, Enabled)).Should().HaveCount(2);
    }

    // 半日取引日（2026-11-27・冬時間）: 閉場 13:00 ET＝18:00 UTC。窓は 12:45 ET＝17:45 UTC から（16:00 を基準にしない）。
    [Fact]
    public async Task T_10_2931_半日取引日は前倒しの閉場の15分前から手仕舞う()
    {
        (await Run(new Fixture { Now = new DateTimeOffset(2026, 11, 27, 17, 50, 0, TimeSpan.Zero) }, Enabled)).Should().HaveCount(2);
        (await Run(new Fixture { Now = new DateTimeOffset(2026, 11, 27, 17, 40, 0, TimeSpan.Zero) }, Enabled)).Should().BeEmpty();
    }

    [Fact]
    public async Task T_10_2931_休場日は手仕舞わない()
    {
        // 2026-11-26（感謝祭）15:50 ET。
        (await Run(new Fixture { Now = new DateTimeOffset(2026, 11, 26, 20, 50, 0, TimeSpan.Zero) }, Enabled)).Should().BeEmpty();
    }

    [Fact]
    public async Task T_10_2931_処理中の決済が覆う銘柄と全注文が拒否される銘柄は出さない()
    {
        var f = new Fixture();
        f.Covered.Add("MARA");
        f.AnyOrderBlocked.Add("NVDA");

        (await Run(f, Enabled)).Should().BeEmpty();
    }

    [Fact]
    public async Task T_10_2931_保有照会の不明と換算レートの欠落では出さない()
    {
        (await Run(new Fixture { Held = null }, Enabled)).Should().BeEmpty();

        var unknown = new Fixture();
        unknown.Positions["MARA"] = null;
        (await Run(unknown, Enabled)).Should().ContainSingle().Which.Intent.Symbol.Should().Be("NVDA");

        (await Run(new Fixture { Rate = null }, Enabled)).Should().BeEmpty();
    }

    [Fact]
    public async Task T_10_2931_現在値が無ければ平均取得単価を参照価格にする()
    {
        var decisions = await Run(new Fixture { Current = null }, Enabled);

        decisions.Single(d => d.Intent.Symbol == "MARA").Intent.Price.Should().Be(20.5m);
    }

    [Fact]
    public async Task T_10_2931_東証は大引け15時30分JSTの15分前から手仕舞う()
    {
        var f = new Fixture { Now = new DateTimeOffset(2026, 10, 20, 6, 20, 0, TimeSpan.Zero) }; // 15:20 JST
        f.Held = [new("7203", Market.Japan)];
        f.Positions["7203"] = new HeldPosition(100, 3_000m, 2_900m);
        f.Rate = 0.0065m;

        var decision = (await Run(f, Enabled)).Should().ContainSingle().Subject;
        decision.Intent.Market.Should().Be(Market.Japan);
        decision.Intent.FxRateToBase.Should().Be(0.0065m);

        // 11:20 JST（前場の引け前）は閉場ではない。
        f.Now = new DateTimeOffset(2026, 10, 20, 2, 20, 0, TimeSpan.Zero);
        (await Run(f, Enabled)).Should().BeEmpty();
    }

    // 🔴 PR #1399 のレビュー: 同じ 5 分の区切りの中で 2 回出しても（入れ替え中の新旧の Pod・巡回の重なり）DecisionId は同じ＝発注執行の一意予約が 2 本目を止める。
    // 区切りが変われば別の ID（最初の決済が見送られても次の区切りで出し直せる）。方向・銘柄・日付でも分かれる。
    [Fact]
    public async Task T_10_2931_DecisionIdは5分の区切りの中で同じで区切りが変われば変わる()
    {
        var f = new Fixture { Now = new DateTimeOffset(2026, 10, 19, 19, 45, 10, TimeSpan.Zero) }; // 15:45:10 ET
        var first = (await Run(f, Enabled)).ToDictionary(d => d.Intent.Symbol, d => d.DecisionId);

        f.Now = new DateTimeOffset(2026, 10, 19, 19, 49, 50, TimeSpan.Zero); // 同じ区切り（15:45〜15:50）
        var sameBucket = (await Run(f, Enabled)).ToDictionary(d => d.Intent.Symbol, d => d.DecisionId);
        f.Now = new DateTimeOffset(2026, 10, 19, 19, 50, 0, TimeSpan.Zero); // 次の区切り
        var nextBucket = (await Run(f, Enabled)).ToDictionary(d => d.Intent.Symbol, d => d.DecisionId);

        sameBucket.Should().Equal(first);
        nextBucket["MARA"].Should().NotBe(first["MARA"]);
        first["MARA"].Should().NotBe(first["NVDA"]);
    }

    [Fact]
    public async Task T_10_2933_分数は市場ごとの構成を使う()
    {
        var options = Enabled with { Minutes = new Dictionary<Market, int> { [Market.UnitedStates] = 5, [Market.Japan] = 15 } };

        (await Run(new Fixture(), options)).Should().BeEmpty("閉場の 10 分前は 5 分の構成では窓の外");
    }

    [Fact]
    public void T_10_2933_構成の既定と読み取り()
    {
        var defaults = PreCloseFlattenOptionsLoader.FromConfiguration(new ConfigurationBuilder().Build());
        defaults.Enabled.Should().BeFalse();
        defaults.MinutesFor(Market.UnitedStates).Should().Be(15);
        defaults.MinutesFor(Market.Japan).Should().Be(15);
        defaults.IntervalSeconds.Should().Be(60);

        var configured = PreCloseFlattenOptionsLoader.FromConfiguration(Config(
            ("Enabled", "true"), ("Minutes:UnitedStates", "10"), ("IntervalSeconds", "30")));
        configured.Enabled.Should().BeTrue();
        configured.MinutesFor(Market.UnitedStates).Should().Be(10);
        configured.MinutesFor(Market.Japan).Should().Be(15);
        configured.IntervalSeconds.Should().Be(30);
    }

    [Theory]
    [InlineData("Enabled", "yes")]
    [InlineData("Minutes:UnitedStates", "0")]
    [InlineData("Minutes:UnitedStates", "61")]
    [InlineData("Minutes:Japan", "x")]
    [InlineData("Minutes:Europe", "15")]
    [InlineData("IntervalSeconds", "5")]
    [InlineData("IntervalSeconds", "301")]
    public void T_10_2933_範囲外や読めない値は起動を止める(string key, string value)
    {
        var act = () => PreCloseFlattenOptionsLoader.FromConfiguration(Config((key, value)));

        act.Should().Throw<InvalidOperationException>();
    }

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>($"{PreCloseFlattenOptionsLoader.Section}:{e.Key}", e.Value)))
            .Build();
}
