extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;
using static TradeDecisionService.Tests.DailyBarsTestData;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;
using FakeSource = TradeDecisionService.Tests.CachedDailyBarsProviderTests.FakeSource;

namespace TradeDecisionService.Tests;

// FR-10, FR-04, FR-11, ADR-0003, ADR-0048 決定3, ADR-0049 決定1〜4, #1122, IADR-0486: 損切り幅の下限＝1.0 × ATR(14, 日足)。
// ATR は判断時点の前営業日までの確定足で、直近 14 本の True Range（前日終値を使う）の単純平均。得られなければ参照価格の 2%。
// 下限は判断ごとにプロンプトの前に 1 回だけ読み、同じ値をプロンプト・適用・発注意図の印・監査・観測ログへ使う。1 注文上限（25%）は緩めない。
public class Atr14StopWidthFloorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 15, 0, 0, TimeSpan.Zero);

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 9, 29), "META は押し目で新規買いを検討する。");

    // equity 100,000・段階残枠 50,000・当日残枠 20,000 → 1 取引リスク 1,000・1 注文上限 25,000・残枠 20,000。
    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static string Buy(decimal referencePrice, decimal width) =>
        $$"""{"action":"Buy","rationale":"押し目","referencePrice":{{referencePrice}},"stopLossDistancePerShare":{{width}}}""";

    private static string Sell(decimal referencePrice, decimal width) =>
        $$"""{"action":"Sell","rationale":"戻り売り","referencePrice":{{referencePrice}},"stopLossDistancePerShare":{{width}}}""";

    private static DecisionTrigger ScheduledMeta() => DecisionTrigger.Scheduled("META", Market.UnitedStates, Now);

    // 前営業日（月 9/28）で終わる連続した取引日の足を count 本。各足は 始値 100・高値 100 + r/2・安値 100 − r/2・終値 100
    // （前日終値 100 が高安の内側にあるので True Range ＝ r。ATR(14) ＝ r）。
    private static List<DailyBar> FlatBars(decimal range, int count = 15, DateOnly? last = null) =>
        [.. Bars(last ?? Monday, Repeat(1_000, count)).Select(b => b with { Open = 100m, High = 100m + range / 2, Low = 100m - range / 2, Close = 100m })];

    // 手計算の 15 本（古い順）。True Range は 2・5（窓を空けて上げ: |高値 − 前日終値|）・5（窓を空けて下げ: |安値 − 前日終値|）・2 × 11。
    // 合計 34 → ATR ＝ 34 ÷ 14。高値 − 安値だけなら 2・2・4・2 × 11 ＝ 30（前日終値を使わない誤りを捕まえる）。
    private static List<DailyBar> HandBars(DateOnly? last = null)
    {
        var dates = Bars(last ?? Monday, Repeat(1, 15)).Select(b => b.Date).ToList();
        var bars = new List<DailyBar>
        {
            new(dates[0], 100m, 101m, 99m, 100m, 1),
            new(dates[1], 100m, 102m, 100m, 101m, 1),
            new(dates[2], 105m, 106m, 104m, 105m, 1),
            new(dates[3], 103m, 104m, 100m, 101m, 1),
        };
        for (var i = 4; i < 15; i++)
            bars.Add(new DailyBar(dates[i], 101m, 102m, 100m, 101m, 1));
        return bars;
    }

    private static ConfirmedDailyBars Confirmed(IEnumerable<DailyBar> bars) => new(Tuesday, Monday, [.. bars]);

    private static Atr14StopWidthFloorSource Atr(IDailyBarsProvider bars) =>
        new(bars, NullLogger<Atr14StopWidthFloorSource>.Instance);

    private static AppSvc Service(
        string llmOutput,
        IStopWidthFloorSource? floor = null,
        SizingContext? context = null,
        StateLogger? logger = null,
        RecordingLlm? llm = null,
        IHeldPositionProvider? held = null) =>
        new(llm ?? new RecordingLlm(llmOutput), new FakePolicy(), new FakeSizing(context ?? Context), new FakeClock(),
            logger ?? new StateLogger(), heldPosition: held, stopWidthFloor: floor);

    // ================================================================================================
    // 純関数（ATR(14)）
    // ================================================================================================

    // T-10-2190, FR-10, ADR-0049 決定2, IADR-0486 決定3: ATR(14) ＝ 直近 14 本の True Range の単純平均。True Range は前日終値を使う。
    // 16 本目（最も古い足）に巨大な値幅を置き、窓が「最後の 15 本」であることも確かめる（先頭の 15 本を使うと値が変わる）。
    [Fact]
    public void T_10_2190_ATRは直近14本のTrueRangeを前日終値込みで単純平均する()
    {
        var hand = HandBars();
        var withOlder = new List<DailyBar>
        {
            new(MarketTradingDays.PreviousTradingDay(Market.UnitedStates, hand[0].Date), 100m, 200m, 50m, 100m, 1),
        };
        withOlder.AddRange(hand);

        AverageTrueRange.Compute(Confirmed(hand)).Should().Be(34m / 14m);
        AverageTrueRange.Compute(Confirmed(withOlder)).Should().Be(34m / 14m, "窓は前営業日で終わる最後の 15 本");
        AverageTrueRange.TrueRange(hand[2], hand[1].Close).Should().Be(5m, "|高値 106 − 前日終値 101|");
        AverageTrueRange.TrueRange(hand[3], hand[2].Close).Should().Be(5m, "|安値 100 − 前日終値 105|");
        AverageTrueRange.Period.Should().Be(14);
        AverageTrueRange.RequiredBars.Should().Be(15);
        AverageTrueRange.Compute(Confirmed(FlatBars(2.5m))).Should().Be(2.5m);
    }

    // T-10-2191, ADR-0049 決定2: 🔴 否定形。得られないときは null（呼び出し側が 2% へ退避する。0 として持たない）。
    // 足が無い・14 本（15 本未満）・最後の足が前営業日でない（古い・前営業日の足が無い）・壊れた足（高値 ＜ 安値・0 以下）・値動きが無い。
    [Theory]
    [InlineData("none")]
    [InlineData("empty")]
    [InlineData("14bars")]
    [InlineData("stale")]
    [InlineData("highBelowLow")]
    [InlineData("zeroLow")]
    [InlineData("zeroClose")]
    [InlineData("flat")]
    public void T_10_2191_ATRが得られないときはnull_否定形(string kind)
    {
        ConfirmedDailyBars? bars = kind switch
        {
            "none" => null,
            "empty" => Confirmed([]),
            "14bars" => Confirmed(HandBars().Skip(1)),
            "stale" => new ConfirmedDailyBars(Tuesday, Monday, HandBars(new DateOnly(2026, 9, 25))),
            "highBelowLow" => Confirmed(HandBars().Select((b, i) => i == 7 ? b with { High = 99m, Low = 100m } : b)),
            "zeroLow" => Confirmed(HandBars().Select((b, i) => i == 0 ? b with { Low = 0m } : b)),
            "zeroClose" => Confirmed(HandBars().Select((b, i) => i == 14 ? b with { Close = 0m } : b)),
            _ => Confirmed(FlatBars(0m)),
        };

        AverageTrueRange.Compute(bars).Should().BeNull();
        StopWidthFloorPolicy.FromAtr(AverageTrueRange.Compute(bars)).Should().BeNull();
    }

    // T-10-2191: 窓の外（16 本目より古い足）の壊れた足は値に効かない（窓の 15 本だけを検める）。
    [Fact]
    public void T_10_2191_窓の外の壊れた足は効かない()
    {
        var hand = HandBars();
        var older = new DailyBar(MarketTradingDays.PreviousTradingDay(Market.UnitedStates, hand[0].Date), 0m, 0m, 0m, 0m, 0);

        AverageTrueRange.Compute(Confirmed([older, .. hand])).Should().Be(34m / 14m);
    }

    // T-10-2192, ADR-0049 決定2, #1117 の実測: 分割の日の True Range が分割を値動きとして数えないのは、足が前復権（分割で価格を揃えた）だから。
    // 10:1 の分割をまたぐ前復権の足（値幅 2 のまま連続）は ATR 2。同じ期間の未調整の足（分割前 1,000 円台）を渡すと分割の日の True Range が
    // 約 900 になり ATR が歪む——この関数は分割を補正しないので、口（CachedDailyBarsProvider・発注執行の照会）が前復権の足を 1 回の取得で揃えることが前提である。
    [Fact]
    public void T_10_2192_前復権の足なら分割の日のTrueRangeは歪まない()
    {
        var adjusted = FlatBars(2m);
        var unadjusted = adjusted
            .Select((b, i) => i < 8 ? b with { Open = b.Open * 10, High = b.High * 10, Low = b.Low * 10, Close = b.Close * 10 } : b)
            .ToList();

        AverageTrueRange.Compute(Confirmed(adjusted)).Should().Be(2m);
        AverageTrueRange.Compute(Confirmed(unadjusted)).Should().BeGreaterThan(60m, "未調整なら分割の日の True Range（約 900）が平均を歪める");
    }

    // T-10-2189, FR-10, ADR-0049 決定2, IADR-0486 決定3: 下限 ＝ 1.0 × ATR。出所は Atr14。ATR の値も持つ（プロンプト・観測ログに出す）。端数は丸めない。
    [Fact]
    public void T_10_2189_下限はATRの1倍で出所はAtr14()
    {
        StopWidthFloorPolicy.FromAtr(34m / 14m).Should().Be(new StopWidthFloor(34m / 14m, StopWidthFloorSource.Atr14, 34m / 14m));
        StopWidthFloorPolicy.FromAtr(null).Should().BeNull();
        StopWidthFloorPolicy.FromAtr(0m).Should().BeNull();
        StopWidthFloorPolicy.FromAtr(-1m).Should().BeNull();
    }

    // ================================================================================================
    // 供給口（Atr14StopWidthFloorSource）
    // ================================================================================================

    // T-10-2193, IADR-0486 決定1・決定3: 供給口は日足の口から ATR を計算し、下限（1.0 × ATR・出所 Atr14・ATR の値）を返す。
    // 🔴 当日の未確定の足（巨大な値幅）は口（CachedDailyBarsProvider）が捨て、値に効かない。
    [Fact]
    public async Task T_10_2193_供給口は前営業日までの確定足からATRの下限を返し当日の足を使わない()
    {
        var source = new FakeSource(_ => [.. HandBars(), new DailyBar(Tuesday, 100m, 300m, 10m, 100m, 1)]);
        using var cached = new CachedDailyBarsProvider(source, new ManualTimeProvider(TuesdayMorning), NullLogger<CachedDailyBarsProvider>.Instance);
        var atr = Atr(cached);

        var floor = await atr.GetFloorAsync("META", Market.UnitedStates, TestContext.Current.CancellationToken);

        atr.IsEnabled.Should().BeTrue();
        floor.Should().Be(new StopWidthFloor(34m / 14m, StopWidthFloorSource.Atr14, 34m / 14m));
        source.Requests.Should().ContainSingle().Which.To.Should().Be(Monday, "要求の期間は取引日の前日まで");
    }

    // T-10-2193: 🔴 否定形。前営業日の足が無い（古い）・足りない・取れない・例外は null（取引判断が 2% へ退避する）。キャンセルは伝える。
    [Theory]
    [InlineData("null")]
    [InlineData("stale")]
    [InlineData("short")]
    [InlineData("throw")]
    public async Task T_10_2193_得られないときはnullを返す_否定形(string kind)
    {
        var bars = new FixedBars(() => kind switch
        {
            "null" => null,
            "stale" => new ConfirmedDailyBars(Tuesday, Monday, HandBars(new DateOnly(2026, 9, 25))),
            "short" => Confirmed(HandBars().Skip(2)),
            _ => throw new InvalidOperationException("boom"),
        });

        (await Atr(bars).GetFloorAsync("META", Market.UnitedStates, TestContext.Current.CancellationToken)).Should().BeNull();
        (await Atr(bars).GetFloorAsOfAsync("META", Market.UnitedStates, Tuesday, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task T_10_2193_キャンセルは伝える()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var bars = new FixedBars(() => throw new OperationCanceledException(cts.Token));

        await Atr(bars).Invoking(a => a.GetFloorAsync("META", Market.UnitedStates, cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
        await Atr(bars).Invoking(a => a.GetFloorAsOfAsync("META", Market.UnitedStates, Tuesday, cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    // ================================================================================================
    // 判断サービス
    // ================================================================================================

    // T-10-2194, ADR-0049 決定1〜3, IADR-0486 決定2・決定5: 🔴 ATR の下限は参照価格の 2% より狭くても、そのまま下限として使う（2% へ広げない）。
    // 参照価格 100・ATR 1.2（1.2%）・AI の幅 0.5 → 1.2 まで広げ、ライン 98.8。発注意図の印は Atr14、監査の出所も Atr14。
    [Fact]
    public async Task T_10_2194_ATRの下限は2パーセントより狭くてもそのまま使い印をAtr14にする()
    {
        var bars = new FixedBars(Confirmed(FlatBars(1.2m)));

        var decision = await Service(Buy(100m, 0.5m), floor: Atr(bars)).DecideAsync(ScheduledMeta());

        decision.Should().NotBeNull();
        decision!.StopWidth.Should().Be(new StopWidthFloorApplication(0.5m, 1.2m, StopWidthFloorSource.Atr14, 1.2m, Widened: true));
        decision.Intent.StopLossPrice.Should().Be(98.8m, "100 − 1.2（2% の 98 ではない）");
        decision.Intent.StopFloorSource.Should().Be(StopWidthFloorSource.Atr14);
        bars.Calls.Should().Be(1, "判断ごとに 1 回だけ読む");
    }

    // T-10-2194: ATR が得られないときは 2% へ退避し、印は Fallback2Pct。下限の機能が無効（NoAtr）でも新規建ての印は Fallback2Pct
    // （下限を掛けてラインを引いた事実は変わらない）。決済の発注意図は印を持たない（null）。
    [Fact]
    public async Task T_10_2194_得られないときは2パーセントで印はFallback2Pct_決済は印なし()
    {
        var unavailable = await Service(Buy(100m, 0.5m), floor: Atr(new FixedBars((ConfirmedDailyBars?)null))).DecideAsync(ScheduledMeta());
        var disabled = await Service(Buy(100m, 0.5m), floor: new NoAtrStopWidthFloorSource()).DecideAsync(ScheduledMeta());
        var close = await Service(Sell(100m, 0.5m), floor: Atr(new FixedBars(Confirmed(FlatBars(1.2m)))), held: new Held(10))
            .DecideAsync(ScheduledMeta());

        unavailable!.Intent.StopLossPrice.Should().Be(98m);
        unavailable.Intent.StopFloorSource.Should().Be(StopWidthFloorSource.Fallback2Pct);
        disabled!.Intent.StopFloorSource.Should().Be(StopWidthFloorSource.Fallback2Pct);
        close!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        close.Intent.StopFloorSource.Should().BeNull();
    }

    // T-10-2195, ADR-0049 決定2, IADR-0486 決定2・決定3: 有効ならプロンプトのリスク制約節に ATR と下限の値（系が掛ける値と同じ）を書く。
    // 得られなければ「未提供・参照価格の 2%」と書く。🔴 無効（既定）なら下限の行を出さず、プロンプトは従来と一字一句同じで、口を読まない。
    [Fact]
    public async Task T_10_2195_プロンプトにATRと下限を書き無効なら従来のまま()
    {
        var withAtr = new RecordingLlm(Buy(100m, 0.5m));
        var unavailable = new RecordingLlm(Buy(100m, 0.5m));
        var disabled = new RecordingLlm(Buy(100m, 0.5m));
        var baseline = new RecordingLlm(Buy(100m, 0.5m));
        var disabledSource = new CountingNoAtr();

        await Service(string.Empty, floor: Atr(new FixedBars(Confirmed(HandBars()))), llm: withAtr).DecideAsync(ScheduledMeta());
        await Service(string.Empty, floor: Atr(new FixedBars((ConfirmedDailyBars?)null)), llm: unavailable).DecideAsync(ScheduledMeta());
        await Service(string.Empty, floor: disabledSource, llm: disabled).DecideAsync(ScheduledMeta());
        await Service(string.Empty, llm: baseline).DecideAsync(ScheduledMeta());

        withAtr.Prompts.Should().ContainSingle().Which.Should().Contain(
            "- 損切り幅の下限: 2.4286（1 株あたり。ATR(14, 日足・前営業日までの確定足 14 本の True Range の単純平均) 2.4286 の 1.0 倍）。");
        unavailable.Prompts.Should().ContainSingle().Which.Should().Contain($"- {TradeDecisionPromptBuilder.StopWidthFloorAtrUnavailableLine}");
        disabled.Prompts.Should().Equal(baseline.Prompts);
        disabled.Prompts.Single().Should().NotContain("損切り幅の下限:");
        disabledSource.Calls.Should().Be(0, "無効なら供給口を読まない");
        TradeDecisionPromptBuilder.StopWidthFloorLine(null).Should().BeNull();
    }

    // T-10-2196, ADR-0049 決定3・決定4, IADR-0486 決定3: 🔴 1 注文上限（equity の 25%）は緩めない。ATR の下限でもサイジングは同じ規則。
    // equity 3,000・価格 100（上限 750）: ATR 1.2（≦ 4%）→ 7 株（上限が決める）、ATR 100・150（≧ 価格）→ 見送り。
    // #1291, ADR-0063 決定1・決定2, IADR-0527: ATR 5（ATR ÷ 価格 5% ≥ 4%）は高ボラティリティ銘柄の区分に入り、上限は equity の 5%（150）→ 1 株
    // （旧 6 株＝リスク基準 30 ÷ 5 は区分の上限で抑えられる）。
    [Theory]
    [InlineData(1.2, 7)]
    [InlineData(5, 1)]
    [InlineData(100, 0)]
    [InlineData(150, 0)]
    public async Task T_10_2196_一注文上限は緩めずATRが価格以上なら見送る(double atr, int expected)
    {
        var small = new SizingContext(3_000m, 3_000m, 4_500m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

        var logger = new StateLogger();
        var decision = await Service(
                Buy(100m, 0.5m), floor: Atr(new FixedBars(Confirmed(FlatBars((decimal)atr)))), context: small, logger: logger)
            .DecideAsync(ScheduledMeta());

        if (expected == 0)
        {
            decision.Should().BeNull("広げた幅が参照価格以上ではラインが成立しない（IADR-0465 決定1 ④）");
            logger.Entries.Should().Contain(
                e => e.Message.StartsWith("損切り幅の下限が現在値以上のため見送り", StringComparison.Ordinal),
                "サイジングの数量 0 ではなく、幅の検査で見送る（幅を価格未満へ縮めない）");
            return;
        }

        decision!.Intent.Quantity.Should().Be(expected);
        decision.Intent.Notional.Should().BeLessThanOrEqualTo(750m, "1 注文上限 ＝ equity 3,000 × 25%");
    }

    // T-10-2200, IADR-0460 決定3, IADR-0486 決定3: 観測ログに ATR(14) の値を出す（出所が ATR のとき）。2% の退避では「不明」。
    [Fact]
    public async Task T_10_2200_観測ログにATRの値を出す()
    {
        var withAtr = new StateLogger();
        var fallback = new StateLogger();

        await Service(Buy(100m, 0.5m), floor: Atr(new FixedBars(Confirmed(FlatBars(1.2m)))), logger: withAtr).DecideAsync(ScheduledMeta());
        await Service(Buy(100m, 0.5m), floor: Atr(new FixedBars((ConfirmedDailyBars?)null)), logger: fallback).DecideAsync(ScheduledMeta());

        var atr = withAtr.Entries.Single(e => e.Message.StartsWith("損切り幅の観測", StringComparison.Ordinal));
        atr.Values["Atr14"].Should().Be(1.2m);
        atr.Values["StopWidthFloor"].Should().Be(1.2m);
        atr.Values["FloorSource"].Should().Be(StopWidthFloorSource.Atr14);
        fallback.Entries.Single(e => e.Message.StartsWith("損切り幅の観測", StringComparison.Ordinal))
            .Values["Atr14"].Should().Be(AppSvc.Unknown);
    }

    // ------------------------------------------------------------------------------------------------

    private sealed class FixedBars(Func<ConfirmedDailyBars?> answer) : IDailyBarsProvider
    {
        public FixedBars(ConfirmedDailyBars? bars)
            : this(() => bars)
        {
        }

        public int Calls { get; private set; }

        public List<DateOnly> AsOfDays { get; } = [];

        public bool IsEnabled => true;

        public Task<ConfirmedDailyBars?> GetConfirmedBarsAsync(
            string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer());
        }

        public Task<ConfirmedDailyBars?> GetConfirmedBarsAsOfAsync(
            string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default)
        {
            AsOfDays.Add(tradingDay);
            return Task.FromResult(answer());
        }
    }

    private sealed class CountingNoAtr : IStopWidthFloorSource
    {
        public int Calls { get; private set; }

        public bool IsEnabled => false;

        public ValueTask<StopWidthFloor?> GetFloorAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult<StopWidthFloor?>(null);
        }

        public ValueTask<StopWidthFloor?> GetFloorAsOfAsync(
            string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult<StopWidthFloor?>(null);
        }
    }

    private sealed class Held(int signedQuantity) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(signedQuantity);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<HeldPosition?>(new HeldPosition(signedQuantity, 100m, null));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
    }

    internal sealed class RecordingLlm(string output) : ILlmCompletionClient
    {
        public List<string> Prompts { get; } = [];

        public string Output { get; set; } = output;

        public Task<string> CompleteAsync(string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(Output);
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

    private sealed class StateLogger : ILogger<AppSvc>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), values));
        }
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(Policy);
    }

    private sealed class FakeSizing(SizingContext context) : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(context);
    }
}
