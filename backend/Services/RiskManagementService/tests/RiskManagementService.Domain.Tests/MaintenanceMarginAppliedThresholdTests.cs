using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-10, UC-06, ADR-0016 決定7（2026-08-07 追記）, #420, IADR-0160:
// **維持率の適用閾値は口座単位**（建玉ごとの閾値の最大値）であり、自動縮小（MaintenanceMarginReducer）と
// 新規建ての評価（ShortSellEvaluator）が**同じ値**を用いること。あわせて維持率の算式（純資産 ÷ 建玉評価額の
// 合計）と信用買いの規制維持率（時価の 25%・FINRA Rule 4210(c)(1)）を固定する。
//
// 本クラスが塞ぐ欠陥: 評価器は「これから出す注文の株価だけ」で閾値を求めていたため、$6.00 の空売り建玉
// （要求 83.3%）を抱えたまま $50.00 の新規空売りを出すと閾値が 40% に緩み、**口座の実維持率 50% でも通った**。
// 自動縮小は 83.3% で発動しているため、縮小の最中に積み増しを許す自己矛盾になっていた。
//
// テスト戦略（docs/tests/README.md §2）の 3 点セット。
//   1. 境界値テーブル — 建玉 1 件のときの閾値境界（従前と同じ結果になること＝回帰）
//   2. プロパティベース — 縮小側と評価側が同じ入力から同じ閾値を出す／算式が 1 か所で定義される
//   3. 否定形 — 最大値が最小値・平均へ退行しない／新規注文自身が候補から外れない／
//               信用買いの 25% が空売り側の式へ退行しない／束が信頼できないときに素通ししない
public class MaintenanceMarginAppliedThresholdTests
{
    private static ShortSellingLimits Limits => TradingDefaults.CreateShortSellSettings().Limits;

    private static readonly DateOnly Today = new(2026, 8, 7);

    // 統制上限（1 銘柄 equity の 10%・空売り比率 50%）が先に効かない水準に置く。
    private const decimal Equity = 1_000_000m;

    private static MarginPosition Position(
        decimal priceUsd,
        int quantity,
        ProductType productType = ProductType.ShortSell,
        string symbol = "AAA") =>
        new()
        {
            Symbol = symbol,
            Market = Market.UnitedStates,
            Side = productType == ProductType.MarginLong ? TradeSide.Buy : TradeSide.Sell,
            ProductType = productType,
            Quantity = quantity,
            PriceUsd = priceUsd,
            RequiredMarginUsd = 1_000m,
        };

    private static MaintenanceMarginSnapshot Snapshot(decimal ratio, params MarginPosition[] positions)
    {
        var marketValue = positions.Sum(p => p.MarketValueUsd);
        return new MaintenanceMarginSnapshot { NetEquityUsd = ratio * marketValue, Positions = positions };
    }

    private static OrderIntent ShortEntryAt(decimal priceUsd, int quantity = 1) =>
        new("BBB", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, BrokerProvider.InternalPaper,
            quantity, priceUsd, PositionEffect.Open, StopLossPrice: priceUsd * 1.1m);

    private static ShortSellOrderContext Context(
        MaintenanceMarginSnapshot? marginSnapshot,
        decimal totalShortExposure = 10_000m,
        decimal totalExposure = 100_000m) =>
        new()
        {
            Today = Today,
            BorrowRateAnnual = 0.05m,
            ShortPermit = true,
            MarginSnapshot = marginSnapshot,
            TotalShortExposure = totalShortExposure,
            TotalExposure = totalExposure,
        };

    private static bool Breaches(OrderIntent intent, ShortSellOrderContext context) =>
        ShortSellEvaluator.Evaluate(intent, shortSellEnabled: true, Limits, Equity, context)
            .Contains(RejectionReason.MaintenanceMarginBreach);

    // ---- 1. 境界値テーブル ---------------------------------------------------------------------

    // T-10-248: **建玉が 1 件（＝注文と同じ株価）のときは従前と同じ結果になる**（回帰）。
    // 口座単位化は「建玉が複数ある場合」を直すものであり、単一建玉の判定を動かしてはならない。
    // 閾値がちょうどの維持率は**通す**（規則 (4) は「割り込む」ことを拒否の条件とする。既存の境界）。
    [Theory]
    [InlineData(6.25)]  // 規制側が効く（$5.00 ÷ $6.25 ＝ 80%）
    [InlineData(10.0)]  // 規制側が効く（50%）
    [InlineData(12.5)]  // 自前と規制が一致する点（40%）
    [InlineData(100.0)] // 自前の 40% が効く
    public void 建玉が一件のときの適用閾値は従前の単一建玉の式と一致する(double priceInput)
    {
        var price = (decimal)priceInput;
        var threshold = Limits.MaintenanceMarginThresholdFor(price);

        MaintenanceMarginPolicy
            .AppliedThreshold(Limits, [Position(price, 100)], price, ProductType.ShortSell)
            .Should().Be(threshold, "候補が同一株価の 1 種類しか無いなら最大値はその値である");

        Breaches(ShortEntryAt(price), Context(Snapshot(threshold - 0.01m, Position(price, 100))))
            .Should().BeTrue("閾値を割り込んでいる");
        Breaches(ShortEntryAt(price), Context(Snapshot(threshold, Position(price, 100))))
            .Should().BeFalse("閾値ちょうどは割り込みではない（既存の境界）");
    }

    // ---- 2. プロパティベース -------------------------------------------------------------------

    // T-10-249: **自動縮小と新規建ての評価は、同じ入力から同じ適用閾値を出す。**
    // 両者が別々に閾値を書いていたことが #420 の欠陥そのものであり、この一致を直接固定する。
    [Fact]
    public void 縮小側と評価側は同じ入力から同じ適用閾値を出す()
    {
        // $6.25（閾値 80%）と $50.00（閾値 40%）の混在。口座に要るのは厳しい方の 80%。
        var positions = new[] { Position(6.25m, 800, symbol: "AAA"), Position(50m, 100, symbol: "CCC") };
        var applied = MaintenanceMarginPolicy.AppliedThreshold(Limits, positions);

        applied.Should().Be(0.80m);

        // 縮小側: 計画の閾値がポリシーと一致する。
        var triggered = MaintenanceMarginReducer.Plan(Snapshot(applied, positions), Limits);
        triggered!.Threshold.Should().Be(applied);
        triggered.RecoveryTarget.Should().Be(applied + Limits.MaintenanceRecoveryTargetOffset);

        // 評価側: 新規注文自身の閾値（$50.00 ＝ 40%）が緩い側であっても、口座の 80% が効く。
        // 閾値の下では拒否し、上では通す＝**評価側の閾値も 80% である**ことが振る舞いから決まる。
        Breaches(ShortEntryAt(50m), Context(Snapshot(applied - 0.01m, positions))).Should().BeTrue();
        Breaches(ShortEntryAt(50m), Context(Snapshot(applied + 0.01m, positions))).Should().BeFalse();

        // 縮小側も同じ位置で切り替わる（発動条件は「≦」＝割り込む前に動く。IADR-0133 決定3）。
        MaintenanceMarginReducer.Plan(Snapshot(applied - 0.01m, positions), Limits).Should().NotBeNull();
        MaintenanceMarginReducer.Plan(Snapshot(applied + 0.01m, positions), Limits).Should().BeNull();
    }

    // T-10-250: **維持率の算式は「純資産 ÷ 建玉評価額の合計」であり、定義は 1 か所にしかない**
    // （ADR-0016 決定7 の 2026-08-07 追記）。供給元が別定義の値を返しても、判定はこの算式を通る。
    [Fact]
    public void 維持率は純資産を建玉評価額の合計で割った値である()
    {
        MaintenanceMarginPolicy.Ratio(39_000m, 100_000m).Should().Be(0.39m);

        // 建玉が無ければ維持率という概念が成立しない（0% ではなく「無い」）。
        MaintenanceMarginPolicy.Ratio(39_000m, 0m).Should().BeNull();

        // スナップショットは割り算を再実装せず、同じ定義を通す。
        var snapshot = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 39_000m,
            Positions = [Position(100m, 1_000)],
        };
        snapshot.TotalMarketValueUsd.Should().Be(100_000m);
        snapshot.MaintenanceMarginRatio.Should()
            .Be(MaintenanceMarginPolicy.Ratio(snapshot.NetEquityUsd, snapshot.TotalMarketValueUsd));
    }

    // T-10-251: 回復目標は適用閾値に**連動**する（＝適用閾値 + 5pt）。閾値の口座単位化にも追随する。
    [Theory]
    [InlineData(6.25)]
    [InlineData(50.0)]
    [InlineData(100.0)]
    public void 回復目標は口座単位の適用閾値に連動する(double lowestPriceInput)
    {
        var positions = new[] { Position((decimal)lowestPriceInput, 100), Position(3_000m, 1) };

        MaintenanceMarginPolicy.AppliedRecoveryTarget(Limits, positions).Should().Be(
            MaintenanceMarginPolicy.AppliedThreshold(Limits, positions) + Limits.MaintenanceRecoveryTargetOffset);
    }

    // ---- 3. 否定形（迂回・退行を塞ぐ）-----------------------------------------------------------

    // T-10-252: **issue #420 の破れ方をそのまま固定する。** $6.00 の空売り建玉（要求 83.3%）を抱えたまま
    // $50.00 の新規空売りを出しても、口座の実維持率 50% では通らない。
    // **適用閾値を「これから出す注文の株価だけ」へ戻すと赤くなる**（従前はこれで通っていた）。
    [Fact]
    public void 低位の空売り建玉を抱えたまま高価格の新規空売りで閾値を緩められない()
    {
        var held = Position(6.00m, 1_000);

        // 対照: 注文の株価だけを見た閾値は 40% であり、維持率 50% は「上回っている」ように見える。
        Limits.MaintenanceMarginThresholdFor(50m).Should().Be(0.40m);
        // 実際に口座へ要求される閾値は保有建玉が決める 83.3% である。
        MaintenanceMarginPolicy.AppliedThreshold(Limits, [held], 50m, ProductType.ShortSell)
            .Should().Be(ShortSellingLimits.RegulatoryFixedMaintenancePerShareUsd / 6.00m);

        Breaches(ShortEntryAt(50m), Context(Snapshot(0.50m, held)))
            .Should().BeTrue("口座の維持率 50% は $6.00 建玉が要求する 83.3% を割り込んでいる");
    }

    // T-10-253: **適用閾値は最大値であり、最小値・平均へ退行しない。**
    // 維持率 50% は最小値（40%）なら通り、70% は平均（61.7%）なら通る。どちらも最大値では通らない。
    [Theory]
    [InlineData(0.50, true)]  // 最小値へ退行すると通ってしまう水準
    [InlineData(0.70, true)]  // 平均へ退行すると通ってしまう水準
    [InlineData(0.84, false)] // 最も厳しい 83.3% を上回っており、正しく通る
    public void 適用閾値は最大値であり最小値や平均へ退行しない(double ratioInput, bool expectedBreach)
    {
        var held = Position(6.00m, 1_000);

        Breaches(ShortEntryAt(50m), Context(Snapshot((decimal)ratioInput, held)))
            .Should().Be(expectedBreach);
    }

    // T-10-254: **新規注文自身も建玉になるため、その閾値も最大値の候補である。**
    // 保有建玉が緩く（$50.00 ＝ 40%）新規注文が厳しい（$6.00 ＝ 83.3%）向きでも、厳しい方が効く。
    // 候補から新規注文を外すと赤くなる。
    [Fact]
    public void 新規注文自身の閾値も適用閾値の候補に含まれる()
    {
        var held = Position(50m, 100);

        MaintenanceMarginPolicy.AppliedThreshold(Limits, [held]).Should().Be(0.40m);
        MaintenanceMarginPolicy.AppliedThreshold(Limits, [held], 6.00m, ProductType.ShortSell)
            .Should().Be(ShortSellingLimits.RegulatoryFixedMaintenancePerShareUsd / 6.00m);

        Breaches(ShortEntryAt(6.00m), Context(Snapshot(0.50m, held)))
            .Should().BeTrue("これから建てる $6.00 の建玉が 83.3% を要求する");
    }

    // T-10-255: **信用買いの規制維持率は時価の 25%（4210(c)(1)）であり、空売り側の式で代用しない。**
    // 代用は IADR-0133 決定2 が計画の不記載を受けて置いた暫定であり、ADR-0016 決定7 の 2026-08-07 追記が
    // 条文を確定させた。空売り側の式へ戻すと、$6.25 の**信用買い**が 80% を要求して赤くなる。
    [Fact]
    public void 信用買いの規制維持率は時価の25パーセントであり空売り側の式を代用しない()
    {
        MaintenanceMarginPolicy.RegulatoryFor(6.25m, ProductType.MarginLong).Should().Be(0.25m);
        MaintenanceMarginPolicy.RegulatoryFor(6.25m, ProductType.MarginLong).Should().NotBe(
            ShortSellingLimits.RegulatoryMaintenanceMarginFor(6.25m),
            "空売り（4210(c)(3)）と信用買い（4210(c)(1)）は別の条文である");

        // 株価に依存しない（時価に対する比率であるため）。実効値は自前 40% が常に上回る。
        MaintenanceMarginPolicy.RegulatoryFor(3_000m, ProductType.MarginLong).Should().Be(0.25m);
        MaintenanceMarginPolicy.ThresholdFor(Limits, 6.25m, ProductType.MarginLong).Should().Be(0.40m);

        // 空売り側は従前どおり株価に依存する（取り違えの退行を塞ぐ）。
        MaintenanceMarginPolicy.ThresholdFor(Limits, 6.25m, ProductType.ShortSell).Should().Be(0.80m);

        // 口座単位でも同じ: $6.25 の信用買いと $50.00 の空売りなら適用閾値は 40% である。
        var positions = new[]
        {
            Position(6.25m, 800, ProductType.MarginLong, "AAA"),
            Position(50m, 100, ProductType.ShortSell, "CCC"),
        };
        MaintenanceMarginPolicy.AppliedThreshold(Limits, positions).Should().Be(0.40m);

        Breaches(ShortEntryAt(50m), Context(Snapshot(0.45m, positions)))
            .Should().BeFalse("信用買いに空売り側の式を代用すると 80% を要求して誤って拒否する");
        MaintenanceMarginReducer.Plan(Snapshot(0.45m, positions), Limits)
            .Should().BeNull("同じ理由で縮小側も発動しない");
    }

    // T-10-256: **束が供給されない／信頼できない／維持率を導出できない場合に素通ししない**
    // （IADR-0131 決定4 のフェイルクローズ。評価側の安全側は「通さない」であり、縮小側の
    // 「動かさない」とは向きが逆である）。建玉が無ければ維持率の概念が成立せず対象外とする。
    [Theory]
    [InlineData(true)]  // 空売り建玉を保有している → 確認できないまま積み増さない
    [InlineData(false)] // 建玉が無い → 維持率という概念が成立しない（対象外）
    public void 維持率を確認できないときは空売り建玉があるなら通さない(bool holdsShort)
    {
        var shortExposure = holdsShort ? 10_000m : 0m;

        // 供給が無い。
        Breaches(ShortEntryAt(50m), Context(null, shortExposure)).Should().Be(holdsShort);

        // 束はあるが信頼できない（IADR-0133 決定8）。**維持率がいくら良く見えても通さない**——
        // 壊れた建玉は分母（建玉評価額）を歪め、維持率を実際より良く見せるためである。
        // 必要証拠金が負の建玉は**評価額としては成立してしまう**（維持率 90% が導出できる）ため、
        // 信頼性の検査を外すと「40% を上回っているから可」で素通りする。
        var untrustedButRatioDerivable = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 0.90m * 10_000m,
            Positions =
            [
                Position(50m, 100, symbol: "AAA"),
                Position(50m, 100, symbol: "CCC") with { RequiredMarginUsd = -1m },
            ],
        };
        untrustedButRatioDerivable.IsTrustworthy.Should().BeFalse();
        untrustedButRatioDerivable.MaintenanceMarginRatio.Should().Be(0.90m);
        Breaches(ShortEntryAt(50m), Context(untrustedButRatioDerivable, shortExposure)).Should().Be(holdsShort);

        // 株価が 0 以下の建玉。閾値の算出は非正の株価に対して**例外を投げる**ため、信頼性の検査を外すと
        // 評価ループごと落ちる（統制が実質オフラインになる）。拒否はしても例外にはしない。
        var negativePrice = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 9_000m,
            Positions = [Position(50m, 100, symbol: "AAA"), Position(-10m, 100, symbol: "CCC")],
        };
        negativePrice.IsTrustworthy.Should().BeFalse();
        Breaches(ShortEntryAt(50m), Context(negativePrice, shortExposure)).Should().Be(holdsShort);

        // 数量・株価が 0 で維持率そのものが導出できない場合も同じ扱いである。
        var zeroPrice = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 9_000m,
            Positions = [Position(0m, 1_000)],
        };
        zeroPrice.IsTrustworthy.Should().BeFalse();
        Breaches(ShortEntryAt(50m), Context(zeroPrice, shortExposure)).Should().Be(holdsShort);

        // 束はあるが建玉が 1 件も無い（維持率を導出できない）。
        var empty = new MaintenanceMarginSnapshot { NetEquityUsd = 9_000m, Positions = [] };
        empty.MaintenanceMarginRatio.Should().BeNull();
        Breaches(ShortEntryAt(50m), Context(empty, shortExposure)).Should().Be(holdsShort);
    }
}
