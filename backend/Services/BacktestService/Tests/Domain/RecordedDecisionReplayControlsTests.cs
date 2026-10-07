using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using BacktestService.Domain;
using Xunit;

namespace BacktestService.Tests.Domain;

// 🔴 FR-10, FR-15, #1176, IADR-0495, #1209, IADR-0506: Stage 0 の再生は、再生の時点で**新規建て**になる注文に本番と同じ 2 統制を当て、
// 当たれば写さない（見送る）。
//   - 最小の名目額: 記録器が本番と同じ関数で判定した EntryBelowMinimumNotional = true → SizedBelowMinimumNotional（本番の判断の見送りの理由）
//   - 判断由来の決済の後の同日・同方向: 共有カーネルの DecisionExitReentry（本番の審査と同じ述語）→ DecisionExitSameDay（本番の拒否理由）
// 再生の時間軸は「判断日 D に注文を決め、D+1 の始値で約定」（BacktestSimulator）。決済は承認＝判断日、約定＝約定したバーの日として述語に渡す。
public class RecordedDecisionReplayControlsTests
{
    // 2026-06-01（月）〜 06-05（金）。
    private static readonly DateOnly D1 = new(2026, 6, 1);
    private static readonly DateOnly D2 = D1.AddDays(1);
    private static readonly DateOnly D3 = D1.AddDays(2);
    private static readonly DateOnly D4 = D1.AddDays(3);
    private static readonly DateOnly D5 = D1.AddDays(4);

    private static IReadOnlyList<Stage0AsOfInputStatus> AllReconstructed =>
    [
        new(Stage0AsOfInputKind.NewsAndDisclosures, Stage0AsOfInputAvailability.Reconstructed),
        new(Stage0AsOfInputKind.DailyPolicy, Stage0AsOfInputAvailability.Reconstructed),
        new(Stage0AsOfInputKind.FxRateToBase, Stage0AsOfInputAvailability.Reconstructed),
    ];

    private static Stage0DecisionRecord Record(DateOnly asOf, int signedQuantity, bool? belowMinimum = false, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, asOf, "fp", "test-model", VoteCount: 1,
            RawDecisions: [],
            MajorityAction: signedQuantity > 0 ? Stage0DecisionAction.Buy : Stage0DecisionAction.Sell,
            MajorityRationale: "根拠", SignedQuantity: signedQuantity,
            CostJpy: 1m, InputTokens: 1, OutputTokens: 1,
            AsOfInputs: AllReconstructed,
            EntryBelowMinimumNotional: belowMinimum);

    private static RecordedDecisionReplayStrategy Strategy(params Stage0DecisionRecord[] records) =>
        new(new Stage0DecisionRecordSet(
            D1, D5, [new Stage0RecordedSymbol("AAPL", Market.UnitedStates), new Stage0RecordedSymbol("MSFT", Market.UnitedStates)],
            new DateOnly(2026, 3, 31), DateTimeOffset.UnixEpoch, "test-model", "strategy-id", records));

    private static PriceBar Bar(DateOnly day, string symbol = "AAPL") => new(symbol, Market.UnitedStates, day, 100m, 101m, 99m, 100m, 1_000);

    // 各日に AAPL と MSFT のバー（skipAapl の日は AAPL のバーが無い＝その日の始値では約定しない）。
    private static List<PriceBar> Bars(DateOnly through, params DateOnly[] skipAapl) =>
    [
        .. Enumerable.Range(0, through.DayNumber - D1.DayNumber + 1)
            .Select(D1.AddDays)
            .SelectMany(d => skipAapl.Contains(d) ? new[] { Bar(d, "MSFT") } : [Bar(d), Bar(d, "MSFT")]),
    ];

    private static BacktestContext Context(DateOnly asOf, IReadOnlyList<PriceBar> history) =>
        new(asOf, history, new Dictionary<(string Symbol, Market Market), InventoryLot>(), 1_000_000m);

    // T-10-2414: 判定 true の新規建て（建玉 0 からの買い・売り）は写さず、本番と同じ理由（SizedBelowMinimumNotional）で見送る。
    // 判定 false と null（判定を持たない記録）は写す。
    [Theory]
    [InlineData(10, true, false)]
    [InlineData(-10, true, false)]
    [InlineData(10, false, true)]
    [InlineData(10, null, true)]
    public void T_10_2414_最小の名目額に満たない新規建ては写さず本番と同じ理由で見送る(int quantity, bool? belowMinimum, bool emitted)
    {
        var strategy = Strategy(Record(D1, quantity, belowMinimum));

        var day = strategy.Replay(Bars(D1), D1);

        strategy.DecideOrders(Context(D1, Bars(D1))).Should().HaveCount(emitted ? 1 : 0);
        day.Orders.Should().HaveCount(emitted ? 1 : 0);
        if (emitted)
        {
            day.SkippedEntries.Should().BeEmpty();
        }
        else
        {
            day.SkippedEntries.Should().ContainSingle().Which.Should().Be(new Stage0ReplaySkippedEntry(
                D1, "AAPL", Market.UnitedStates, quantity, DecisionSkipReason.SizedBelowMinimumNotional, null));
        }
    }

    // T-10-2414: 🔴 否定形。同じ判定 true の注文でも、再生の時点で建玉の決済（符号が建玉と逆）なら写す（本番は決済に名目額を掛けない）。
    // 買い増し（建玉と同じ符号）は新規建てなので見送る。
    [Fact]
    public void T_10_2414_判定trueでも決済なら写し買い増しは見送る()
    {
        // D2 の部分的な決済（D3 に約定）。D4 の買い増しは判断由来の決済の当日（承認 D2・約定 D3）に当たらないので、名目額だけで判定される。
        var strategy = Strategy(Record(D1, 10), Record(D2, -5, belowMinimum: true), Record(D4, 3, belowMinimum: true));
        var bars = Bars(D4);

        strategy.Replay(bars, D2).Orders.Should().Equal(new BacktestOrder("AAPL", Market.UnitedStates, -5));
        var d4 = strategy.Replay(bars, D4);
        d4.Orders.Should().BeEmpty("建玉 10 − 5 ＝ 5 のロングへの買い増しは新規建て");
        d4.SkippedEntries.Should().ContainSingle().Which.SkipReason.Should().Be(DecisionSkipReason.SizedBelowMinimumNotional);
    }

    // T-10-2415: 🔴 D1 に買い（D2 に約定）、D2 にロングを判断で決済（D3 の始値で約定）。D3 の同じ方向（買い）は本番と同じ理由
    // （DecisionExitSameDay）で見送る ——本番の述語は「決済の約定が当日」で数える（共有カーネルの述語＝リスク管理の射影と同値。T-10-2411）。
    [Fact]
    public void T_10_2415_判断由来の決済が約定した日の同じ方向の新規建ては見送る()
    {
        var strategy = Strategy(Record(D1, 10), Record(D2, -10), Record(D3, 10));

        var d3 = strategy.Replay(Bars(D3), D3);

        d3.Orders.Should().BeEmpty();
        d3.SkippedEntries.Should().ContainSingle().Which.Should().Be(new Stage0ReplaySkippedEntry(
            D3, "AAPL", Market.UnitedStates, 10, null, RejectionReason.DecisionExitSameDay));
        strategy.DecideOrders(Context(D3, Bars(D3))).Should().BeEmpty();
    }

    // T-10-2415: 🔴 否定形。反対方向（売りの新規建て）・翌々日（D4）・別の銘柄・決済が約定しなかった場合（承認は前日のまま）は止めない。
    [Fact]
    public void T_10_2415_反対方向と翌々日と別銘柄と約定しなかった決済は止めない()
    {
        // 反対方向: D3 の売り（建玉 0 からの新規の売り）は通す。
        Strategy(Record(D1, 10), Record(D2, -10), Record(D3, -4)).Replay(Bars(D3), D3).Orders
            .Should().Equal(new BacktestOrder("AAPL", Market.UnitedStates, -4));

        // 翌々日: D3 に記録が無く、D4 の買いは通す（決済の承認 D2・約定 D3 とも当日ではない）。
        Strategy(Record(D1, 10), Record(D2, -10), Record(D4, 10)).Replay(Bars(D4), D4).Orders
            .Should().Equal(new BacktestOrder("AAPL", Market.UnitedStates, 10));

        // 別の銘柄: AAPL の決済は MSFT の買いを止めない。
        Strategy(Record(D1, 10), Record(D2, -10), Record(D3, 10, symbol: "MSFT")).Replay(Bars(D3), D3).Orders
            .Should().Equal(new BacktestOrder("MSFT", Market.UnitedStates, 10));

        // 約定しなかった決済: D3 に AAPL のバーが無い（D2 の決済は約定しない）。D3 の買い（ロング 10 への買い増し）は通す（承認は D2）。
        Strategy(Record(D1, 10), Record(D2, -10), Record(D3, 10)).Replay(Bars(D3, skipAapl: D3), D3).Orders
            .Should().Equal(new BacktestOrder("AAPL", Market.UnitedStates, 10));
    }

    // T-10-2415: 両方に当たる注文は本番と同じ順で DecisionExitSameDay を先に名乗る（本番は可否の口が LLM の前に止め、名目額の判定まで届かない）。
    [Fact]
    public void T_10_2415_両方に当たれば判断由来の決済の理由を先に名乗る()
    {
        var d3 = Strategy(Record(D1, 10), Record(D2, -10), Record(D3, 10, belowMinimum: true)).Replay(Bars(D3), D3);

        d3.SkippedEntries.Should().ContainSingle().Which.RejectionReason.Should().Be(RejectionReason.DecisionExitSameDay);
        d3.SkippedEntries[0].SkipReason.Should().BeNull();
    }

    // T-10-2416: 🔴 シミュレータを通した端から端。見送った注文は約定に現れず（D3 の買いは D4 に約定しない）、同じ戦略を
    // 2 回走らせても同じ結果（走行をまたいで状態を持たない）。D3 から始める窓（建玉ゼロ・過去の決済を知らない）では D3 の買いは通る。
    [Fact]
    public void T_10_2416_シミュレータの端から端で見送りが約定に現れず走行をまたいで同じ()
    {
        var strategy = Strategy(Record(D1, 10), Record(D2, -10), Record(D3, 10), Record(D4, 5, belowMinimum: true));
        var bars = Bars(D5);
        var config = new BacktestConfig(1_000_000m, new BacktestCostModel(TradingAssumptionsDefaults.Create(), SlippageRatio: 0m), CostSensitivity.Baseline);

        var first = BacktestSimulator.Run(bars, strategy, config);
        var second = BacktestSimulator.Run(bars, strategy, config);

        first.Fills.Where(f => f.Symbol == "AAPL").Select(f => (f.Date, f.SignedQuantity))
            .Should().Equal((D2, 10), (D3, -10));
        second.Fills.Should().Equal(first.Fills);
        second.EquityCurve.Should().Equal(first.EquityCurve);

        // 窓（D3 以降のバーだけ）: 建玉ゼロから始まり、D2 の決済はこの走行に無い。D3 の買いは D4 に約定し、D4 の判定 true の買い増しは見送る。
        var window = BacktestSimulator.Run([.. bars.Where(b => b.Date >= D3)], strategy, config);
        window.Fills.Where(f => f.Symbol == "AAPL").Select(f => (f.Date, f.SignedQuantity)).Should().Equal((D4, 10));
    }
}
