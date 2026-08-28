using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Domain.Tests;

// FR-10, UC-06, ADR-0003, ADR-0016 決定7, #330, IADR-0133:
// 維持率割れによる建玉の自動縮小（回復目標＝閾値+5pt・対象＝必要証拠金の降順・量＝最小限・AI 非介在）。
//
// テスト戦略（docs/tests/README.md §2）の 3 点セットで構成する。
//   1. 境界値テーブル — 発動条件（閾値の直下 / 一致 / 直上）と縮小量（**過剰・過少の両方向**）
//   2. プロパティベース — 必要証拠金の降順であり含み損順・時刻順に退行しない／回復目標は閾値に連動する／
//      同じ入力からは同じ出力（＝完全に機械的である）
//   3. 否定形 — 連鎖しない（+5pt の余裕が効く）／維持率を報告しなければ回避できる、が塞がっている
//
// 閾値・回復目標はマジックナンバーで書かず統制設定（ShortSellingLimits）から引く。
public class MaintenanceMarginAutoReduceTests
{
    private static ShortSellingLimits Limits => TradingDefaults.CreateShortSellSettings().Limits;

    // 株価 $100（≧ $12.50）＝ 規制側 30% より自前 40% が厳しい＝**閾値 40% / 回復目標 45%** のケース。
    private const decimal SelfThresholdPrice = 100m;

    // 株価 $10（< $12.50）＝ 規制側 max($5/10, 30%) = 50% が効く＝**閾値 50% / 回復目標 55%** のケース。
    private const decimal RegulatoryThresholdPrice = 10m;

    private static MarginPosition Position(
        string symbol,
        decimal price,
        int quantity,
        decimal requiredMargin,
        TradeSide side = TradeSide.Sell,
        ProductType productType = ProductType.ShortSell) =>
        new()
        {
            Symbol = symbol,
            Market = Market.UnitedStates,
            Side = side,
            ProductType = productType,
            Quantity = quantity,
            PriceUsd = price,
            RequiredMarginUsd = requiredMargin,
        };

    private static MaintenanceMarginSnapshot Snapshot(decimal netEquity, params MarginPosition[] positions) =>
        new() { NetEquityUsd = netEquity, Positions = positions };

    // ---- 1. 境界値テーブル ---------------------------------------------------------------------

    // T-10-181: 発動条件は「維持率 ≦ 閾値」＝**割り込む前に**動く（IADR-0133 決定3）。
    // 計画（§5・FR-10）の「割り込む前に発動」を、計画に無い余裕 α を足さずに満たす唯一の読み方である。
    [Theory]
    [InlineData(0.41, false)] // 閾値の上 — まだ動かない
    [InlineData(0.40, true)]  // 閾値ちょうど — **ここで動く**（まだ割り込んでいない）
    [InlineData(0.39, true)]  // 割り込み済み — 当然動く
    public void 発動条件は維持率が閾値以下であること(double ratio, bool shouldTrigger)
    {
        // 建玉評価額 100,000（$100 × 1,000 株）に対し、純資産で維持率を作る。
        var snapshot = Snapshot((decimal)ratio * 100_000m, Position("AAPL", SelfThresholdPrice, 1_000, 30_000m));

        var plan = MaintenanceMarginReducer.Plan(snapshot, Limits);

        (plan is not null).Should().Be(shouldTrigger);
    }

    // T-10-182: 閾値 40% のとき回復目標は 45%。縮小量は**目標に届く最小限**である。
    // 過剰（1 株でも多く決済していない）と過少（1 株減らすと目標に届かない）を**両方向**で固定する。
    [Fact]
    public void 閾値40パーセントのとき回復目標45パーセントへ届く最小限だけ決済する()
    {
        // 純資産 40,000／建玉 100,000（$100 × 1,000 株）＝ 維持率 40%（＝閾値ちょうど）。
        // 必要な削減 X = 100,000 − 40,000 ÷ 0.45 = 11,111.11…／$100 ⇒ 112 株（切り上げ）。
        var snapshot = Snapshot(40_000m, Position("AAPL", SelfThresholdPrice, 1_000, 30_000m));

        var plan = MaintenanceMarginReducer.Plan(snapshot, Limits)!;

        plan.Threshold.Should().Be(0.40m);
        plan.RecoveryTarget.Should().Be(0.45m);
        plan.Legs.Should().ContainSingle();
        plan.Legs[0].Quantity.Should().Be(112);

        // 過少でない: 目標に到達している。
        plan.RatioAfter.Should().BeGreaterThanOrEqualTo(plan.RecoveryTarget);

        // 過剰でない: 1 株少ないと目標に届かない（＝これが最小限である）。
        var oneShareLess = 40_000m / (100_000m - (111 * SelfThresholdPrice));
        oneShareLess.Should().BeLessThan(plan.RecoveryTarget);
    }

    // T-10-183: 規制側が効いて閾値が上がると、回復目標も**同じだけ**上がる（連動する）。
    // 株価 $10 では規制側 max($5 ÷ $10, 30%) = 50% が効き、目標は 55% になる。
    [Fact]
    public void 規制側が効くと閾値も回復目標も連動して上がる()
    {
        // 純資産 50,000／建玉 100,000（$10 × 10,000 株）＝ 維持率 50%（＝閾値ちょうど）。
        // 必要な削減 X = 100,000 − 50,000 ÷ 0.55 = 9,090.90…／$10 ⇒ 910 株（切り上げ）。
        var snapshot = Snapshot(50_000m, Position("PENNY", RegulatoryThresholdPrice, 10_000, 30_000m));

        var plan = MaintenanceMarginReducer.Plan(snapshot, Limits)!;

        plan.Threshold.Should().Be(0.50m);
        plan.RecoveryTarget.Should().Be(0.55m);
        plan.Legs[0].Quantity.Should().Be(910);
        plan.RatioAfter.Should().BeGreaterThanOrEqualTo(plan.RecoveryTarget);

        // 過剰でない（1 株少ないと届かない）。
        (50_000m / (100_000m - (909 * RegulatoryThresholdPrice))).Should().BeLessThan(plan.RecoveryTarget);
    }

    // T-10-184: 建玉が複数あるとき、適用閾値は**最も厳しいもの**（IADR-0133 決定2・FR-10「常に厳しい方が効く」）。
    [Fact]
    public void 複数建玉では最も厳しい閾値が口座へ適用される()
    {
        var snapshot = Snapshot(
            45_000m,
            Position("AAPL", SelfThresholdPrice, 500, 15_000m),        // 閾値 40%
            Position("PENNY", RegulatoryThresholdPrice, 5_000, 15_000m)); // 閾値 50%

        var plan = MaintenanceMarginReducer.Plan(snapshot, Limits)!;

        plan.Threshold.Should().Be(0.50m);
        plan.RecoveryTarget.Should().Be(0.55m);
    }

    // T-10-185: 部分決済の必要証拠金は数量で按分する（日報が「決済した建玉の必要証拠金」を求めるため）。
    [Fact]
    public void 部分決済の必要証拠金は数量で按分される()
    {
        var snapshot = Snapshot(40_000m, Position("AAPL", SelfThresholdPrice, 1_000, 30_000m));

        var leg = MaintenanceMarginReducer.Plan(snapshot, Limits)!.Legs[0];

        leg.Quantity.Should().Be(112);
        leg.RequiredMarginUsd.Should().Be(30_000m * 112 / 1_000);
    }

    // ---- 2. プロパティベース -------------------------------------------------------------------

    // T-10-186: 対象は**必要証拠金の降順**であり、**含み損順・建玉時刻順に退行しない**（UC-06 が両者を棄却）。
    // 入力の並び順（時刻順の代理）と含み損の大小を意図的に必要証拠金と食い違わせ、全順列で不変を確かめる。
    [Fact]
    public void 対象は必要証拠金の降順であり含み損順や入力順に退行しない()
    {
        // 必要証拠金: SMALL(5,000) < MID(9,000) < BIG(20,000)。
        // 評価額（＝含み損の代理として大きさを変えた建玉）は必要証拠金と**逆順**に並べてある。
        var big = Position("BIG", SelfThresholdPrice, 100, 20_000m);
        var mid = Position("MID", SelfThresholdPrice, 300, 9_000m);
        var small = Position("SMALL", SelfThresholdPrice, 600, 5_000m);

        MarginPosition[][] permutations =
        [
            [big, mid, small], [big, small, mid], [mid, big, small],
            [mid, small, big], [small, big, mid], [small, mid, big],
        ];

        foreach (var positions in permutations)
        {
            // 建玉 100,000（$100 × 1,000 株）・維持率 20%（割り込み済み）＝ 3 件すべてに手が届く量を要する。
            var plan = MaintenanceMarginReducer.Plan(Snapshot(20_000m, positions), Limits)!;

            plan.Legs.Select(l => l.Symbol).Should().Equal(
                ["BIG", "MID", "SMALL"],
                "対象は必要証拠金の降順である（含み損の大小・建玉時刻・入力順は選択に用いない）");
        }
    }

    // T-10-187: 回復目標は株価によらず常に「適用閾値 + 5 ポイント」である（#329 の解決メソッドを消費している）。
    [Theory]
    [InlineData(5.0)]
    [InlineData(9.99)]
    [InlineData(12.50)]
    [InlineData(16.67)]
    [InlineData(100)]
    [InlineData(3000)]
    public void 回復目標は常に適用閾値プラス5ポイントである(double price)
    {
        var priceUsd = (decimal)price;
        // 維持率 1%（確実に発動する）。
        var snapshot = Snapshot(priceUsd, Position("SYM", priceUsd, 100, 10m));

        var plan = MaintenanceMarginReducer.Plan(snapshot, Limits)!;

        plan.Threshold.Should().Be(Limits.MaintenanceMarginThresholdFor(priceUsd));
        plan.RecoveryTarget.Should().Be(plan.Threshold + Limits.MaintenanceRecoveryTargetOffset);
        plan.RecoveryTarget.Should().Be(Limits.MaintenanceRecoveryTargetFor(priceUsd));
    }

    // T-10-188: **同じ入力からは常に同じ出力**（UC-06「完全に機械的である」）。
    // 入力の並び順を変えても、繰り返し呼んでも、決済する建玉と数量は一致する。
    [Fact]
    public void 同じ入力からは常に同じ出力が出る()
    {
        var a = Position("AAA", SelfThresholdPrice, 200, 9_000m);
        var b = Position("BBB", SelfThresholdPrice, 300, 9_000m); // 必要証拠金が同値（同値解決の決定性）
        var c = Position("CCC", SelfThresholdPrice, 500, 12_000m);

        var first = MaintenanceMarginReducer.Plan(Snapshot(35_000m, a, b, c), Limits)!;
        var again = MaintenanceMarginReducer.Plan(Snapshot(35_000m, a, b, c), Limits)!;
        var shuffled = MaintenanceMarginReducer.Plan(Snapshot(35_000m, c, b, a), Limits)!;

        again.Should().BeEquivalentTo(first);
        shuffled.Should().BeEquivalentTo(first);
    }

    // T-10-189: 決済するのは目標に届くまでであり、**それ以上は決済しない**（§5「目標を満たした時点で決済を止める」）。
    // 必要証拠金が最大の建玉だけで足りるなら、2 件目には手を付けない。
    [Fact]
    public void 目標を満たした時点で決済を止める()
    {
        var snapshot = Snapshot(
            40_000m,
            Position("BIG", SelfThresholdPrice, 500, 20_000m),
            Position("SMALL", SelfThresholdPrice, 500, 5_000m));

        var plan = MaintenanceMarginReducer.Plan(snapshot, Limits)!;

        plan.Legs.Should().ContainSingle().Which.Symbol.Should().Be("BIG");
        plan.Legs[0].Quantity.Should().BeLessThan(500, "必要な分だけの部分決済であり建玉 1 件を丸ごと閉じない");
    }

    // ---- 3. 否定形（迂回・退行を塞ぐ） ---------------------------------------------------------

    // T-10-190: **連鎖しない**。縮小直後に不利方向の小幅な価格変動（2%）が起きても再発動しない
    //（+5 ポイントの余裕が効いている＝計画が「発動の連鎖の防止」として与えた理由どおり）。
    // ちょうど閾値まで戻す規則（回復目標＝閾値）だと、同じ変動で即座に再発動することも併せて示す。
    [Fact]
    public void 縮小直後の小幅な価格変動では再発動しない()
    {
        // 純資産 39,000／建玉 100,000 ＝ 維持率 39%（閾値 40% を割り込んだ状態）。
        var snapshot = Snapshot(39_000m, Position("AAPL", SelfThresholdPrice, 1_000, 30_000m));
        var plan = MaintenanceMarginReducer.Plan(snapshot, Limits)!;

        // 縮小後の建玉（888 株）に対し、ショートに不利な +2% の価格変動が起きたとする。
        var remainingQuantity = 1_000 - plan.Legs[0].Quantity;
        var movedPrice = SelfThresholdPrice * 1.02m;
        var after = Snapshot(39_000m, Position("AAPL", movedPrice, remainingQuantity, 30_000m));

        MaintenanceMarginReducer.Plan(after, Limits).Should().BeNull("5 ポイントの余裕が発動の連鎖を防ぐ");

        // 対照: 回復目標を「閾値ちょうど」にしていたら、同じ 2% の変動で再発動する。
        var noBuffer = Limits with { MaintenanceRecoveryTargetOffset = 0m };
        var atThreshold = MaintenanceMarginReducer.Plan(snapshot, noBuffer)!;
        var afterNoBuffer = Snapshot(
            39_000m,
            Position("AAPL", movedPrice, 1_000 - atThreshold.Legs[0].Quantity, 30_000m));

        MaintenanceMarginReducer.Plan(afterNoBuffer, noBuffer).Should().NotBeNull(
            "余裕が無ければ次の小さな価格変動で再発動する（計画が +5pt を置いた理由）");
    }

    // T-10-191: **維持率を報告しなければ縮小を回避できる**——縮小側では塞げない（データが無いのに決済しない）。
    // 塞ぐのは**積み増し側**である（#329・IADR-0131 決定4）。両者を 1 つの事実として固定する。
    [Fact]
    public void 維持率が供給されないなら縮小しないが積み増しもできない()
    {
        // 縮小側: 供給なし（null）・建玉なしのいずれでも決済しない。
        MaintenanceMarginReducer.Plan(null, Limits).Should().BeNull();
        MaintenanceMarginReducer.Plan(Snapshot(0m), Limits).Should().BeNull();

        // 積み増し側: 維持率が取得できないのに空売り建玉があるなら新規売り建ては通らない（フェイルクローズ）。
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, BrokerProvider.InternalPaper,
            Quantity: 1, Price: 100m, PositionEffect.Open, StopLossPrice: 110m);
        var context = new ShortSellOrderContext
        {
            Today = new DateOnly(2026, 8, 4),
            BorrowRateAnnual = 0.05m,
            ShortPermit = true,
            MarginSnapshot = null, // 報告しない（#420・IADR-0160 で維持率は束として供給される）
            TotalShortExposure = 10_000m,
            TotalExposure = 100_000m,
        };

        ShortSellEvaluator.Evaluate(intent, shortSellEnabled: true, Limits, equity: 1_000_000m, context)
            .Should().Contain(RejectionReason.MaintenanceMarginBreach);
    }

    // T-10-192: 回復目標へ**到達できない**場合（純資産が尽きている）は全量決済する（IADR-0133 決定4）。
    // 何もしないのは「最も危険な状態で統制が沈黙する」ことを意味する。
    [Fact]
    public void 回復目標へ到達できないなら全量決済する()
    {
        var snapshot = Snapshot(
            0m,
            Position("AAA", SelfThresholdPrice, 100, 12_000m),
            Position("BBB", SelfThresholdPrice, 200, 8_000m));

        var plan = MaintenanceMarginReducer.Plan(snapshot, Limits)!;

        plan.Legs.Select(l => (l.Symbol, l.Quantity)).Should().Equal(("AAA", 100), ("BBB", 200));
        plan.RatioAfter.Should().BeNull("建玉が無くなれば維持率という概念自体が消える（0% ではない）");
    }

    // T-10-193: **信用買い（ロング）も対象である**（UC-06「信用建玉・空売り建玉の維持率」）。
    // 空売りだけを縮小対象と読むと、信用買いのマージンコールを止められない。
    [Fact]
    public void 信用買いの建玉も縮小の対象になる()
    {
        var snapshot = Snapshot(
            40_000m,
            Position("LONG", SelfThresholdPrice, 1_000, 30_000m, TradeSide.Buy, ProductType.MarginLong));

        var plan = MaintenanceMarginReducer.Plan(snapshot, Limits)!;

        plan.Legs[0].PositionSide.Should().Be(TradeSide.Buy);
        plan.Legs[0].ProductType.Should().Be(ProductType.MarginLong);
    }

    // T-10-194: 建玉数量を超える決済を作らない（部分決済のクランプ）。
    [Fact]
    public void 決済数量は建玉数量を超えない()
    {
        // 建玉 20,000（$100 × 100 株 × 2 件）・維持率 10% ＝ 必要な削減が 1 件目の評価額を超える。
        var snapshot = Snapshot(
            2_000m,
            Position("AAA", SelfThresholdPrice, 100, 12_000m),
            Position("BBB", SelfThresholdPrice, 100, 8_000m));

        var plan = MaintenanceMarginReducer.Plan(snapshot, Limits)!;

        plan.Legs.Should().OnlyContain(l => l.Quantity <= 100);
        plan.Legs[0].Quantity.Should().Be(100, "1 件目は全量決済でクランプされる");
    }

    // T-10-208（境界）: 値として成立しない建玉（**株価・数量が 0 以下／必要証拠金が負**）を含むスナップショットは
    // 信頼できないものとして扱い、**決済しない**（IADR-0133 決定8）。例外で中断もしない。
    //
    // 閾値算出 `MaintenanceMarginThresholdFor` は非正の株価に対して例外を投げるため、入口で弾かないと
    // `Plan` 全体が例外で中断する（＝定期評価ドライバの評価ループが死ぬ）。本テストがその入口を固定する。
    [Theory]
    [InlineData(0, 1_000, 30_000)]   // 株価 0（フィードの欠損値）
    [InlineData(-1, 1_000, 30_000)]  // 負の株価
    [InlineData(100, 0, 30_000)]     // 数量 0（分母が歪む）
    [InlineData(100, -10, 30_000)]   // 負の数量
    [InlineData(100, 1_000, -1)]     // 負の必要証拠金（決済の優先順位が歪む）
    public void 成立しない値を含むスナップショットでは決済も例外も起きない(
        double price, int quantity, double requiredMargin)
    {
        var snapshot = Snapshot(
            40_000m,
            Position("BROKEN", (decimal)price, quantity, (decimal)requiredMargin));

        var act = () => MaintenanceMarginReducer.Plan(snapshot, Limits);

        act.Should().NotThrow("評価ループごと死ぬ経路を作らない（拒否は呼び出し側が観測可能にする）");
        act().Should().BeNull("壊れたデータで建玉を決済する方が、しないことより危険である");
        snapshot.IsTrustworthy.Should().BeFalse();
        snapshot.UntrustedPositions.Should().ContainSingle().Which.Symbol.Should().Be("BROKEN");
    }

    // T-10-209（否定形）: 壊れた建玉が 1 件でもあれば**スナップショット全体**を信頼しない。
    // 壊れた建玉だけを除くと分母（建玉評価額の合計）が縮み、**維持率が実際より良く見えて過少縮小へ倒れる**。
    // 除外した場合に何が起きるか（別の、より緩い縮小計画が立つこと）を対照で示す。
    [Fact]
    public void 壊れた建玉を除外して分母を縮めない()
    {
        var healthy = Position("AAPL", SelfThresholdPrice, 1_000, 30_000m);
        var broken = Position("BROKEN", 0m, 500, 10_000m);

        MaintenanceMarginReducer.Plan(Snapshot(40_000m, healthy, broken), Limits)
            .Should().BeNull("1 件でも壊れていれば全体を信頼しない");

        // 対照（＝棄却した「該当建玉だけ除外する案」の姿）: 除外すると発動し、しかも
        // 壊れた建玉ぶんの評価額が分母から消えているため、実際より小さい縮小量で「回復した」と判断される。
        var excludedPlan = MaintenanceMarginReducer.Plan(Snapshot(40_000m, healthy), Limits)!;
        excludedPlan.RatioBefore.Should().Be(0.40m,
            "除外後の維持率は健全建玉だけで計算され、壊れた建玉を含む実際の口座より良く見える");
    }
}
