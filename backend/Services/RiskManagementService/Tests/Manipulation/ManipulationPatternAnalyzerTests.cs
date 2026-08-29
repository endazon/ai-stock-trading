using RiskManagementService.Domain;
using RiskManagementService.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests.Manipulation;

// FR-19, IADR-0040: 相場操縦検知アルゴリズム（自口座の直近発注統計に対する純関数ヒューリスティック）。
// 受け入れ基準（20260711_manipulation-detector）: 4 シグナルの検知・最小標本未満/正常取引の無嫌疑・境界。
public class ManipulationPatternAnalyzerTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 11, 9, 30, 0, TimeSpan.Zero);

    private static ManipulationDetectionSettings Settings() =>
        TradingDefaults.CreateManipulationDetectionSettings();

    private static OrderActivityRecord Filled(
        double placedOffsetSec = 0, TradeSide side = TradeSide.Buy, int amendments = 0) =>
        new()
        {
            PlacedAt = Base.AddSeconds(placedOffsetSec),
            Side = side,
            Quantity = 10,
            FilledQuantity = 10,
            Status = OrderStatus.Filled,
            AmendmentCount = amendments,
            TerminalAt = Base.AddSeconds(placedOffsetSec + 5),
        };

    private static OrderActivityRecord CancelledNoFill(
        double placedOffsetSec = 0,
        double lifetimeSec = 30,
        TradeSide side = TradeSide.Buy,
        int amendments = 0) =>
        new()
        {
            PlacedAt = Base.AddSeconds(placedOffsetSec),
            Side = side,
            Quantity = 10,
            FilledQuantity = 0,
            Status = OrderStatus.Cancelled,
            AmendmentCount = amendments,
            TerminalAt = Base.AddSeconds(placedOffsetSec + lifetimeSec),
        };

    private static OrderActivityWindow Window(params OrderActivityRecord[] records) =>
        new()
        {
            Symbol = "AAPL",
            Market = Market.UnitedStates,
            AsOf = Base.AddMinutes(5),
            Records = records,
        };

    [Fact]
    public void 空窓は無嫌疑()
    {
        var verdict = ManipulationPatternAnalyzer.Analyze(
            OrderActivityWindow.Empty("AAPL", Market.UnitedStates, Base), Settings());

        verdict.IsSuspected.Should().BeFalse();
        verdict.Signals.Should().BeEmpty();
    }

    [Fact]
    public void 最小標本未満は取消が多くても無嫌疑()
    {
        // 発注 4 件（既定 MinimumSampleSize=5 未満）は全件約定なし取消でも無嫌疑（低頻度正常取引の誤検知防止）。
        var verdict = ManipulationPatternAnalyzer.Analyze(
            Window(
                CancelledNoFill(0), CancelledNoFill(1), CancelledNoFill(2), CancelledNoFill(3)),
            Settings());

        verdict.IsSuspected.Should().BeFalse();
    }

    [Fact]
    public void 正常なデイトレードは無嫌疑()
    {
        // 5 発注すべて約定・訂正なし。取消なし・約定率 1.0。
        var verdict = ManipulationPatternAnalyzer.Analyze(
            Window(Filled(0), Filled(10), Filled(20), Filled(30), Filled(40)), Settings());

        verdict.IsSuspected.Should().BeFalse();
        verdict.Signals.Should().BeEmpty();
    }

    [Fact]
    public void 過剰な取消を検知する()
    {
        // 10 発注中 8 件が約定なし取消（比率 0.8 > 既定 0.7）。取消は板を離して長命（レイヤリング/見せ玉と切り分け）。
        var records = new List<OrderActivityRecord>();
        for (var i = 0; i < 8; i++)
        {
            // 生存 60 秒・時間差で重ならないよう配置（レイヤリング条件を避ける）。
            records.Add(CancelledNoFill(i * 20, lifetimeSec: 5, side: i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        records.Add(Filled(170));
        records.Add(Filled(180));

        var verdict = ManipulationPatternAnalyzer.Analyze(Window([.. records]), Settings());

        verdict.Signals.Should().Contain(ManipulationSignal.ExcessiveCancellations);
    }

    [Fact]
    public void 取消比率が境界以下なら過剰取消は検知しない()
    {
        // 10 発注中 7 件取消（比率 0.7＝境界。> 0.7 でないので非該当）。短命でなく約定率も十分。
        var records = new List<OrderActivityRecord>();
        for (var i = 0; i < 7; i++)
        {
            records.Add(CancelledNoFill(i * 20, lifetimeSec: 5, side: i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        for (var i = 0; i < 3; i++)
        {
            records.Add(Filled(150 + i * 5));
        }

        var verdict = ManipulationPatternAnalyzer.Analyze(Window([.. records]), Settings());

        verdict.Signals.Should().NotContain(ManipulationSignal.ExcessiveCancellations);
    }

    [Fact]
    public void 過剰な訂正を検知する()
    {
        // 5 発注・訂正総数 16（1 発注あたり 3.2 > 既定 3.0）。すべて約定（取消・見せ玉と切り分け）。
        var verdict = ManipulationPatternAnalyzer.Analyze(
            Window(
                Filled(0, amendments: 4),
                Filled(10, amendments: 4),
                Filled(20, amendments: 4),
                Filled(30, amendments: 2),
                Filled(40, amendments: 2)),
            Settings());

        verdict.Signals.Should().Contain(ManipulationSignal.ExcessiveAmendments);
    }

    [Fact]
    public void 過剰訂正は境界ちょうど3では検知しない()
    {
        // 5 発注・訂正総数 15（1 発注あたり 3.0＝境界）。> 3.0 でないので非該当（`>` の意図を固定）。
        var verdict = ManipulationPatternAnalyzer.Analyze(
            Window(
                Filled(0, amendments: 3),
                Filled(10, amendments: 3),
                Filled(20, amendments: 3),
                Filled(30, amendments: 3),
                Filled(40, amendments: 3)),
            Settings());

        verdict.Signals.Should().NotContain(ManipulationSignal.ExcessiveAmendments);
    }

    [Fact]
    public void 見せ玉_約定率が境界ちょうど0_1では検知しない()
    {
        // 10 発注中 1 件約定（約定率 0.1＝境界）。< 0.1 でないので見せ玉は非該当（`<` の意図を固定）。
        // 残り 9 件は即取消だが、約定率境界により NoExecutionIntent には至らない。
        var records = new List<OrderActivityRecord> { Filled(0) };
        for (var i = 0; i < 9; i++)
        {
            records.Add(CancelledNoFill(10 + i * 10, lifetimeSec: 1, side: TradeSide.Buy));
        }

        var verdict = ManipulationPatternAnalyzer.Analyze(Window([.. records]), Settings());

        verdict.Signals.Should().NotContain(ManipulationSignal.NoExecutionIntent);
    }

    [Fact]
    public void 見せ玉_短命取消が閾値ちょうど3件で検知する()
    {
        // 約定なし取消 5 件（約定率 0）・うち短命（1 秒）ちょうど 3 件＋長命 2 件。>= 3 で該当（境界を固定）。
        // 生存区間が重ならないよう配置し Layering と切り分ける。
        var records = new List<OrderActivityRecord>
        {
            CancelledNoFill(0, lifetimeSec: 30, side: TradeSide.Buy),
            CancelledNoFill(40, lifetimeSec: 30, side: TradeSide.Buy),
            CancelledNoFill(80, lifetimeSec: 1, side: TradeSide.Buy),
            CancelledNoFill(90, lifetimeSec: 1, side: TradeSide.Buy),
            CancelledNoFill(100, lifetimeSec: 1, side: TradeSide.Buy),
        };

        var verdict = ManipulationPatternAnalyzer.Analyze(Window([.. records]), Settings());

        verdict.Signals.Should().Contain(ManipulationSignal.NoExecutionIntent);
    }

    [Fact]
    public void 見せ玉_短命取消が2件では検知しない()
    {
        // 約定なし取消 5 件（約定率 0）・うち短命 2 件（< 3）。閾値未満で NoExecutionIntent 非該当（境界を下側から固定）。
        var records = new List<OrderActivityRecord>
        {
            CancelledNoFill(0, lifetimeSec: 30, side: TradeSide.Buy),
            CancelledNoFill(40, lifetimeSec: 30, side: TradeSide.Buy),
            CancelledNoFill(80, lifetimeSec: 30, side: TradeSide.Buy),
            CancelledNoFill(120, lifetimeSec: 1, side: TradeSide.Buy),
            CancelledNoFill(130, lifetimeSec: 1, side: TradeSide.Buy),
        };

        var verdict = ManipulationPatternAnalyzer.Analyze(Window([.. records]), Settings());

        verdict.Signals.Should().NotContain(ManipulationSignal.NoExecutionIntent);
    }

    [Fact]
    public void 見せ玉_低約定率かつ短命取消の反復を検知する()
    {
        // 6 発注すべて約定なし・即取消（生存 1 秒 ≤ 2 秒）。約定率 0 < 0.1・短命取消 6 ≥ 3。
        var records = new List<OrderActivityRecord>();
        for (var i = 0; i < 6; i++)
        {
            // 時間をずらし同時生存を避ける（レイヤリングと切り分け）。
            records.Add(CancelledNoFill(i * 10, lifetimeSec: 1, side: TradeSide.Buy));
        }

        var verdict = ManipulationPatternAnalyzer.Analyze(Window([.. records]), Settings());

        verdict.Signals.Should().Contain(ManipulationSignal.NoExecutionIntent);
    }

    [Fact]
    public void 低約定率でも短命取消が閾値未満なら見せ玉は検知しない()
    {
        // 約定なし取消 6 件だが生存 30 秒（短命でない）。NoExecutionIntent は非該当（過剰取消は別途該当し得る）。
        var records = new List<OrderActivityRecord>();
        for (var i = 0; i < 6; i++)
        {
            records.Add(CancelledNoFill(i * 20, lifetimeSec: 10, side: i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        var verdict = ManipulationPatternAnalyzer.Analyze(Window([.. records]), Settings());

        verdict.Signals.Should().NotContain(ManipulationSignal.NoExecutionIntent);
    }

    [Fact]
    public void 板演出_同一方向の約定なし取消の同時多段生存を検知する()
    {
        // 10 発注: 同一方向（Buy）の約定なし取消 3 本が同時に生存（板に 3 段を並べる）＋約定 7 件。
        // 取消比率 0.3・約定率 0.7 で他シグナルを避け、Layering を単独検知する。
        var records = new List<OrderActivityRecord>
        {
            CancelledNoFill(0, lifetimeSec: 40, side: TradeSide.Buy),
            CancelledNoFill(1, lifetimeSec: 40, side: TradeSide.Buy),
            CancelledNoFill(2, lifetimeSec: 40, side: TradeSide.Buy),
        };

        for (var i = 0; i < 7; i++)
        {
            records.Add(Filled(100 + i * 5));
        }

        var verdict = ManipulationPatternAnalyzer.Analyze(Window([.. records]), Settings());

        verdict.Signals.Should().Contain(ManipulationSignal.Layering);
        verdict.Signals.Should().NotContain(ManipulationSignal.ExcessiveCancellations);
    }

    [Fact]
    public void 板演出_時間差で重ならない取消は検知しない()
    {
        // 同一方向の約定なし取消 3 本だが生存区間が重ならない（順次）。同時多段でないため Layering 非該当。
        var records = new List<OrderActivityRecord>
        {
            CancelledNoFill(0, lifetimeSec: 2, side: TradeSide.Buy),
            CancelledNoFill(20, lifetimeSec: 2, side: TradeSide.Buy),
            CancelledNoFill(40, lifetimeSec: 2, side: TradeSide.Buy),
        };

        for (var i = 0; i < 7; i++)
        {
            records.Add(Filled(100 + i * 5));
        }

        var verdict = ManipulationPatternAnalyzer.Analyze(Window([.. records]), Settings());

        verdict.Signals.Should().NotContain(ManipulationSignal.Layering);
    }

    [Fact]
    public void 板演出_反対方向が混在すると同時本数に数えない()
    {
        // Buy 2 本 + Sell 1 本が同時生存でも、同一方向の同時本数は 2（< 3）で Layering 非該当。
        var records = new List<OrderActivityRecord>
        {
            CancelledNoFill(0, lifetimeSec: 40, side: TradeSide.Buy),
            CancelledNoFill(1, lifetimeSec: 40, side: TradeSide.Buy),
            CancelledNoFill(2, lifetimeSec: 40, side: TradeSide.Sell),
        };

        for (var i = 0; i < 7; i++)
        {
            records.Add(Filled(100 + i * 5));
        }

        var verdict = ManipulationPatternAnalyzer.Analyze(Window([.. records]), Settings());

        verdict.Signals.Should().NotContain(ManipulationSignal.Layering);
    }

    [Fact]
    public void 複合該当は全シグナルを列挙する()
    {
        // 6 発注すべて Buy・約定なし・即取消（1 秒）・同時生存。過剰取消 + 見せ玉 + 板演出が同時該当。
        var records = new List<OrderActivityRecord>();
        for (var i = 0; i < 6; i++)
        {
            records.Add(CancelledNoFill(i * 0.1, lifetimeSec: 1, side: TradeSide.Buy));
        }

        var verdict = ManipulationPatternAnalyzer.Analyze(Window([.. records]), Settings());

        verdict.IsSuspected.Should().BeTrue();
        verdict.Signals.Should().Contain(ManipulationSignal.ExcessiveCancellations);
        verdict.Signals.Should().Contain(ManipulationSignal.NoExecutionIntent);
        verdict.Signals.Should().Contain(ManipulationSignal.Layering);
    }
}
