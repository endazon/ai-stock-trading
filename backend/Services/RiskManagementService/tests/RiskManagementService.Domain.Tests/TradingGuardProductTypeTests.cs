using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-19, UC-06, ADR-0007, ADR-0016 決定1/8/13, ADR-0009, #332, IADR-0132:
// 取引ガードの再実装（商品種別の 3 値化・差金決済ガードの日本株現物限定・禁止銘柄・発注パターン）。
//
// テスト戦略（docs/tests/README.md §2）の 3 点セットで構成する。
//   1. 境界値／組み合わせ表 — 商品種別 3 値 × 有効/無効 × 市場（既定＝現物のみ有効を固定）
//   2. プロパティベース — 入力によらず成り立つ不変条件（無効な種別はどの市場・どの金額でも通らない）
//   3. 否定形 — 統制を**迂回できない**こと（申告値の詐称・別表記・別市場・手仕舞い偽装・設定の緩和）
public class TradingGuardProductTypeTests
{
    private static readonly DateOnly Registered = new(2026, 7, 7);

    private static RiskManagementSettings SettingsWith(params ProductType[] enabled)
    {
        var settings = TradingDefaults.CreateSettings();
        return settings with
        {
            Guard = settings.Guard with { EnabledProductTypes = new HashSet<ProductType>(enabled) },
        };
    }

    private static PortfolioSnapshot Snapshot(HashSet<(string, Market)>? tradedToday = null) =>
        new()
        {
            Capital = TradingDefaults.InitialCapital,
            SymbolsTradedToday = tradedToday ?? new HashSet<(string, Market)>(),
        };

    // 商品種別ごとの代表的な新規建て。空売りは「売り × 新規建て」であることが定義である（IADR-0131 決定1）。
    private static OrderIntent Entry(
        ProductType productType,
        Market market = Market.UnitedStates,
        decimal price = 100m,
        int quantity = 1,
        string symbol = "AAPL") =>
        productType == ProductType.ShortSell
            ? new OrderIntent(symbol, market, TradeSide.Sell, ProductType.ShortSell, TradeMode.Paper,
                quantity, price, PositionEffect.Open, StopLossPrice: price * 1.1m)
            : new OrderIntent(symbol, market, TradeSide.Buy, productType, TradeMode.Paper,
                quantity, price, PositionEffect.Open);

    // ---------------------------------------------------------------------------------------
    // 1. 境界値／組み合わせ表
    // ---------------------------------------------------------------------------------------

    // FR-19, ADR-0016 決定1: 商品種別 3 値 × 有効/無効 × 市場（日本 / 米国）の全 12 通り。
    // 「有効ならば商品種別を理由に拒否されない・無効ならば必ず拒否される」を固定する。
    // 空売りは対象市場が米国株のみ（決定13）だが、その拒否理由は ShortSellDisabled であり
    // 商品種別ガード（ProductTypeDisabled）とは別物である。本テストが見るのは
    // ProductTypeDisabled の有無だけであり（空売り×日本×有効 が別理由で拒否されても本テストは
    // 通る）、ShortSellDisabled 自体は ShortSellingControlsTests が固定する。
    [Theory]
    [InlineData(ProductType.Cash, true, Market.Japan)]
    [InlineData(ProductType.Cash, true, Market.UnitedStates)]
    [InlineData(ProductType.Cash, false, Market.Japan)]
    [InlineData(ProductType.Cash, false, Market.UnitedStates)]
    [InlineData(ProductType.MarginLong, true, Market.Japan)]
    [InlineData(ProductType.MarginLong, true, Market.UnitedStates)]
    [InlineData(ProductType.MarginLong, false, Market.Japan)]
    [InlineData(ProductType.MarginLong, false, Market.UnitedStates)]
    [InlineData(ProductType.ShortSell, true, Market.Japan)]
    [InlineData(ProductType.ShortSell, true, Market.UnitedStates)]
    [InlineData(ProductType.ShortSell, false, Market.Japan)]
    [InlineData(ProductType.ShortSell, false, Market.UnitedStates)]
    public void 商品種別3値と有効無効と市場の組み合わせで商品種別ガードが決まる(
        ProductType productType, bool enabled, Market market)
    {
        var settings = enabled ? SettingsWith(productType) : SettingsWith();

        var result = RiskEvaluator.Evaluate(Entry(productType, market), settings, Snapshot());

        result.Reasons.Contains(RejectionReason.ProductTypeDisabled).Should().Be(!enabled,
            "商品種別 {0} は{1}である", productType, enabled ? "有効" : "無効");
    }

    // FR-19, ADR-0016 決定1, 05_trading-assumptions §5: **既定はいずれも「現物のみ有効」**。
    [Fact]
    public void 既定の有効な商品種別は現物のみである()
    {
        var guard = TradingDefaults.CreateGuardSettings();

        guard.EnabledProductTypes.Should().BeEquivalentTo([ProductType.Cash]);
        guard.EnabledProductTypes.Should().NotContain(ProductType.MarginLong);
        guard.EnabledProductTypes.Should().NotContain(ProductType.ShortSell);
    }

    // FR-19: 3 値は**それぞれ独立に**有効・無効を設定できる（8 通りの部分集合すべてで独立性が成り立つ）。
    [Theory]
    [InlineData(new ProductType[0])]
    [InlineData(new[] { ProductType.Cash })]
    [InlineData(new[] { ProductType.MarginLong })]
    [InlineData(new[] { ProductType.ShortSell })]
    [InlineData(new[] { ProductType.Cash, ProductType.MarginLong })]
    [InlineData(new[] { ProductType.Cash, ProductType.ShortSell })]
    [InlineData(new[] { ProductType.MarginLong, ProductType.ShortSell })]
    [InlineData(new[] { ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell })]
    public void 商品種別の有効化は互いに独立である(ProductType[] enabled)
    {
        var settings = SettingsWith(enabled);

        foreach (var productType in Enum.GetValues<ProductType>())
        {
            var result = RiskEvaluator.Evaluate(Entry(productType), settings, Snapshot());

            result.Reasons.Contains(RejectionReason.ProductTypeDisabled)
                .Should().Be(!enabled.Contains(productType),
                    "有効集合 [{0}] における {1}", string.Join(",", enabled), productType);
        }
    }

    // ---------------------------------------------------------------------------------------
    // 2. プロパティベース（入力によらず成り立つ不変条件）
    // ---------------------------------------------------------------------------------------

    // FR-19: **無効な商品種別は、どの市場・どの金額でも通らない。** 金額が上限内でも、
    // 市場が有効でも、禁止銘柄でなくても、商品種別が無効なら新規建ては必ず拒否される。
    [Fact]
    public void 無効な商品種別はどの市場でもどの金額でも通らない()
    {
        var settings = SettingsWith(ProductType.Cash); // 現物のみ有効（既定）

        foreach (var productType in new[] { ProductType.MarginLong, ProductType.ShortSell })
        {
            foreach (var market in Enum.GetValues<Market>())
            {
                foreach (var price in new[] { 5m, 100m, 1_000m, 10_000m })
                {
                    foreach (var quantity in new[] { 1, 3, 50 })
                    {
                        var result = RiskEvaluator.Evaluate(
                            Entry(productType, market, price, quantity), settings, Snapshot());

                        result.IsApproved.Should().BeFalse(
                            "{0} は無効（{1}・{2}×{3}）", productType, market, price, quantity);
                        result.Reasons.Should().Contain(RejectionReason.ProductTypeDisabled);
                    }
                }
            }
        }
    }

    // FR-19, IADR-0132 決定3: 実効商品種別は**申告値ではなく売買方向 × 建玉効果**から決まる。
    // 新規売り建ては、申告が何であれ常に空売りとして解決される（＝申告での迂回が構造的に不可能）。
    [Theory]
    [InlineData(ProductType.Cash)]
    [InlineData(ProductType.MarginLong)]
    [InlineData(ProductType.ShortSell)]
    public void 新規売り建ての実効商品種別は申告値によらず空売りである(ProductType declared)
    {
        var shortEntry = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, declared,
            TradeMode.Paper, 1, 100m, PositionEffect.Open, StopLossPrice: 110m);

        ProductTypeResolver.Resolve(shortEntry).Should().Be(ProductType.ShortSell);
    }

    // FR-19, IADR-0132 決定3: 新規売り建て以外は申告どおり（過剰な読み替えをしない）。
    [Theory]
    [InlineData(TradeSide.Buy, PositionEffect.Open, ProductType.Cash)]
    [InlineData(TradeSide.Buy, PositionEffect.Open, ProductType.MarginLong)]
    [InlineData(TradeSide.Sell, PositionEffect.Close, ProductType.Cash)]
    [InlineData(TradeSide.Buy, PositionEffect.Close, ProductType.ShortSell)]
    public void 新規売り建て以外の実効商品種別は申告どおりである(
        TradeSide side, PositionEffect effect, ProductType declared)
    {
        var intent = new OrderIntent("AAPL", Market.UnitedStates, side, declared,
            TradeMode.Paper, 1, 100m, effect);

        ProductTypeResolver.Resolve(intent).Should().Be(declared);
    }

    // FR-19, #332: 差金決済防止は「日本株 かつ 現物 かつ 新規建て」でのみ作動する。
    // 市場 × 商品種別の全組み合わせで不変条件を固定する（誤適用と取りこぼしの双方を止める）。
    [Fact]
    public void 差金決済ガードは日本株現物の新規建てにのみ作動する()
    {
        var settings = SettingsWith(ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell);

        foreach (var market in Enum.GetValues<Market>())
        {
            foreach (var productType in Enum.GetValues<ProductType>())
            {
                var intent = Entry(productType, market, symbol: "7203");
                var snapshot = Snapshot(new HashSet<(string, Market)> { ("7203", market) });

                var result = RiskEvaluator.Evaluate(intent, settings, snapshot);

                var expected = market == Market.Japan && productType == ProductType.Cash;
                result.Reasons.Contains(RejectionReason.SameDayReentry).Should().Be(expected,
                    "{0} の {1}", market, productType);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // 3. 否定形（統制を迂回できないこと）
    // ---------------------------------------------------------------------------------------

    // **誤適用の退行防止（本 issue の必須要請）**: 差金決済ガードは日本の差金決済規制
    //（金商法 161 条の 2）向けであり、**米国株には適用しない**。米国口座は信用口座（margin account）で
    // 運用するため Good Faith Violation が発生せず、決済制度由来の回転数制約が無いためである
    //（05_trading-assumptions §5「米国口座の種別・決済」・FR-19 本文・project-planning#81）。
    // 誤適用すると米国株の当日回転が丸ごと止まり、日中スイング戦略そのものが成立しなくなる。
    [Fact]
    public void 差金決済ガードは米国株では作動しない()
    {
        var snapshot = Snapshot(new HashSet<(string, Market)> { ("AAPL", Market.UnitedStates) });

        var result = RiskEvaluator.Evaluate(
            Entry(ProductType.Cash, Market.UnitedStates), TradingDefaults.CreateSettings(), snapshot);

        result.Reasons.Should().NotContain(RejectionReason.SameDayReentry);
        result.IsApproved.Should().BeTrue("米国株の同日再エントリーは決済制度上の制約を受けない");
    }

    // 日本株現物では作動する（正の対照）。ガードを緩めた結果として全市場で無効化していないことを示す。
    [Fact]
    public void 差金決済ガードは日本株現物では作動する()
    {
        var snapshot = Snapshot(new HashSet<(string, Market)> { ("7203", Market.Japan) });

        var result = RiskEvaluator.Evaluate(
            Entry(ProductType.Cash, Market.Japan, symbol: "7203"),
            TradingDefaults.CreateSettings(),
            snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.SameDayReentry);
    }

    // FR-19: 米国株の回転数は差金決済ではなく**日次発注金額上限・保有建玉数上限**で管理する
    //（§5「米国口座の種別・決済」）。ガードを外した分の統制が別経路で効いていることを固定する。
    [Fact]
    public void 米国株の回転数は日次発注金額上限で管理される()
    {
        var settings = TradingDefaults.CreateSettings();
        var equity = TradingDefaults.InitialCapital;
        var snapshot = new PortfolioSnapshot
        {
            Capital = equity,
            SymbolsTradedToday = new HashSet<(string, Market)> { ("AAPL", Market.UnitedStates) },
            DailyOrderedAmount = settings.Limits.MaxDailyOrderAmountFor(equity),
        };

        var result = RiskEvaluator.Evaluate(
            Entry(ProductType.Cash, Market.UnitedStates), settings, snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.DailyOrderAmountExceeded);
    }

    // **迂回 1: 申告値の詐称**。新規売り建てを「現物」と申告しても空売りとして扱われ、
    // 既定（空売り無効）では商品種別ガードと空売り統制の両方で止まる。
    [Fact]
    public void 空売りを現物と申告してもガードを迂回できない()
    {
        var disguised = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            TradeMode.Paper, 1, 100m, PositionEffect.Open, StopLossPrice: 110m);

        var result = RiskEvaluator.Evaluate(disguised, TradingDefaults.CreateSettings(), Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.ProductTypeDisabled);
        result.Reasons.Should().Contain(RejectionReason.ShortSellDisabled);
    }

    // **迂回 2: 別表記**。禁止銘柄は前後空白・大文字小文字の違いで迂回できない（IADR-0132 決定6）。
    [Theory]
    [InlineData("aapl")]
    [InlineData("AAPL ")]
    [InlineData(" Aapl")]
    public void 禁止銘柄は表記差で迂回できない(string symbol)
    {
        var settings = TradingDefaults.CreateSettings();
        settings = settings with
        {
            Guard = settings.Guard with
            {
                BannedSymbols = [new BannedSymbol("AAPL", Market.UnitedStates, "利用者登録: 検証用", Registered)],
            },
        };

        var result = RiskEvaluator.Evaluate(
            Entry(ProductType.Cash, Market.UnitedStates, symbol: symbol), settings, Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BannedSymbol);
    }

    // **迂回 3: 別市場**。禁止銘柄は（銘柄, 市場）で照合するため、**登録した市場では必ず止まる**。
    // 計画は登録単位を（銘柄, 市場）としており、別市場の同一コードは別銘柄である（Issue #26）。
    // 迂回として塞ぐべきは「登録済みの市場で通ってしまうこと」であり、ここを 3 値すべてで固定する。
    [Theory]
    [InlineData(ProductType.Cash)]
    [InlineData(ProductType.MarginLong)]
    [InlineData(ProductType.ShortSell)]
    public void 禁止銘柄は商品種別を変えても迂回できない(ProductType productType)
    {
        // 計画確定の禁止銘柄（グローリー 6457・日本株）。商品種別を変えても禁止は禁止である。
        var settings = SettingsWith(ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell);

        var result = RiskEvaluator.Evaluate(
            Entry(productType, Market.Japan, symbol: "6457"), settings, Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BannedSymbol);
    }

    // FR-19, ADR-0007, INDEX 決定 20: 計画が登録済みとする 3 銘柄が、理由と登録日を伴って強制される。
    [Theory]
    [InlineData("6457")]
    [InlineData("6902")]
    [InlineData("6502")]
    public void 計画が登録した禁止銘柄は発注前に拒否される(string symbol)
    {
        var settings = TradingDefaults.CreateSettings();

        var banned = settings.Guard.BannedSymbols.Single(b => b.Symbol == symbol);
        banned.Market.Should().Be(Market.Japan);
        banned.Reason.Should().NotBeNullOrWhiteSpace();
        banned.RegisteredOn.Should().Be(Registered);

        var result = RiskEvaluator.Evaluate(
            Entry(ProductType.Cash, Market.Japan, symbol: symbol), settings, Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BannedSymbol);
    }

    // FR-20, ADR-0016 決定10, project-planning#58: 「統制違反 0 件」に計上されるのは**クラス C 限定**
    //（BannedSymbol / ManipulativeOrderPattern）。商品種別ガードはクラス B であり計上しない。
    [Fact]
    public void 商品種別ガードの拒否は統制違反に計上されない()
    {
        var result = RiskEvaluator.Evaluate(
            Entry(ProductType.MarginLong), TradingDefaults.CreateSettings(), Snapshot());

        RejectionReasonClassification.ClassOf(RejectionReason.ProductTypeDisabled)
            .Should().Be(RejectionReasonClass.B);
        RejectionReasonClassification.CountsAsControlViolation(result.Reasons).Should().BeFalse();

        // 対照: 禁止銘柄はクラス C（計上する）。
        var bannedResult = RiskEvaluator.Evaluate(
            Entry(ProductType.Cash, Market.Japan, symbol: "6457"),
            TradingDefaults.CreateSettings(),
            Snapshot());
        RejectionReasonClassification.CountsAsControlViolation(bannedResult.Reasons).Should().BeTrue();
    }

    // **迂回 4: 手仕舞い経路の逆用ではなく、手仕舞いが止まらないこと**（ADR-0009・FR-10 の不変条件）。
    // 3 値化により「無効な商品種別の建玉を手仕舞えない」経路が実在するようになった
    //（既定では空売りが無効であり、空売り建玉の買い戻しは ProductType.ShortSell の注文になる）。
    // 商品種別ガードを新規建てに限定していなければ、損失に上限が無い建玉を閉じられなくなる。
    [Theory]
    [InlineData(ProductType.ShortSell, TradeSide.Buy)]
    [InlineData(ProductType.MarginLong, TradeSide.Sell)]
    [InlineData(ProductType.Cash, TradeSide.Sell)]
    public void 無効な商品種別でも手仕舞いは止めない(ProductType productType, TradeSide side)
    {
        // 既定＝現物のみ有効。信用買い・空売りはいずれも無効である。
        var settings = TradingDefaults.CreateSettings();
        var close = new OrderIntent("AAPL", Market.UnitedStates, side, productType,
            TradeMode.Paper, 1, 100m, PositionEffect.Close);

        var result = RiskEvaluator.Evaluate(close, settings, Snapshot());

        result.IsApproved.Should().BeTrue("手仕舞い（Close）と損切りは統制で止めない（ADR-0009）");
    }

    // **迂回 5: 設定の緩和**。商品種別で空売りを有効化しても、空売り専用統制 8 規則は解除されない
    //（借株照会の経路が無い＝ context null ならフェイルクローズ。IADR-0131 決定2）。
    [Fact]
    public void 空売りを有効化しても専用統制は解除されない()
    {
        var settings = SettingsWith(ProductType.Cash, ProductType.ShortSell);

        var result = RiskEvaluator.Evaluate(Entry(ProductType.ShortSell), settings, Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().NotContain(RejectionReason.ShortSellDisabled, "商品種別としては有効化した");
        result.Reasons.Should().Contain(RejectionReason.BorrowUnavailable, "借株照会の経路が無い（フェイルクローズ）");
    }

    // **迂回 6: 二重の情報源**。空売りの有効・無効は取引ガードの商品種別が単一情報源であり
    //（IADR-0132 決定2）、他の場所の設定で有効化できない。
    [Fact]
    public void 空売りの有効無効は取引ガードの商品種別だけで決まる()
    {
        var settings = TradingDefaults.CreateSettings();

        // 空売り統制値だけを（安全側に見えるよう）差し替えても、有効・無効は変わらない。
        var withLimits = settings with
        {
            ShortSell = settings.ShortSell with
            {
                Limits = settings.ShortSell.Limits with { PerSymbolCapRatio = 1.00m },
            },
        };

        RiskEvaluator.Evaluate(Entry(ProductType.ShortSell), withLimits, Snapshot())
            .Reasons.Should().Contain(RejectionReason.ShortSellDisabled);

        RiskEvaluator.Evaluate(
                Entry(ProductType.ShortSell),
                SettingsWith(ProductType.Cash, ProductType.ShortSell),
                Snapshot())
            .Reasons.Should().NotContain(RejectionReason.ShortSellDisabled);
    }

    // FR-19: 相場操縦とみなされ得る発注パターンの禁止は**既定で有効**であり、拒否はクラス C である
    //（しきい値の較正は #251。検出器の判定内容は ManipulativeOrderPatternDetectorTests が担う）。
    [Fact]
    public void 発注パターン禁止は既定で有効でありクラスCとして計上される()
    {
        TradingDefaults.CreateGuardSettings().ProhibitManipulativeOrderPatterns.Should().BeTrue();
        RejectionReasonClassification.ClassOf(RejectionReason.ManipulativeOrderPattern)
            .Should().Be(RejectionReasonClass.C);
    }
}
