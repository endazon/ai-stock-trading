using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Domain.Tests;

// FR-19, FR-10, UC-06, #375, ADR-0021（決定2〜5）, IADR-0153: 現金口座（cash account）対応の判定。
//
// 計画の受け入れ基準（issue #375「退行防止（テスト必須）」）:
//   - 口座種別 × 市場 × 商品種別の組み合わせ表（**信用口座では #332 の現行挙動が保たれること**）
//   - 現金口座で差金決済ガードが米国株に**適用される**こと／信用口座では**適用されない**こと（両方向）
//   - GFV 回避ガードの境界（未決済資金の判定）
//   - 否定形: 現金口座で信用買い・空売りを設定・発注する経路が塞がれていること／
//             口座種別の照会失敗時にフェイルクローズすること
//   - `CashAccountSettlementHold` が統制違反（クラス C）に計上されないこと
//
// **本ファイルの主眼は否定形である。** 「現金口座で統制が増える」ことより、
// 「**口座種別が分からないときに止まる**」ことのほうが事故（GFV 3 回 → 90 日の口座制限）に直結する。
public class CashAccountControlsTests
{
    // ---- 判定入力の組み立て ----

    // 発注先は既定で moomoo SIMULATE にする。**内蔵 paper ではない**——内蔵 paper は口座種別を要求しない
    // （IADR-0153 決定2）ため、既定にすると口座種別に関する検証がすべて素通りしてしまう。
    private static OrderIntent Entry(
        string symbol = "AAPL",
        Market market = Market.UnitedStates,
        TradeSide side = TradeSide.Buy,
        ProductType productType = ProductType.Cash,
        BrokerProvider mode = BrokerProvider.MoomooSimulate,
        int quantity = 1,
        decimal price = 1_000m) =>
        new(symbol, market, side, productType, mode, quantity, price, PositionEffect.Open);

    private static OrderIntent Close(
        string symbol = "AAPL",
        Market market = Market.UnitedStates,
        TradeSide side = TradeSide.Sell,
        ProductType productType = ProductType.Cash,
        BrokerProvider mode = BrokerProvider.MoomooSimulate,
        int quantity = 1,
        decimal price = 1_000m) =>
        new(symbol, market, side, productType, mode, quantity, price, PositionEffect.Close);

    // #425, ADR-0025 決定2, IADR-0165: GFV 発生回数は**自前計数**であり、ブローカー照会
    // （BrokerAccountState）ではなくスナップショットの別欄（GoodFaithViolations）から供給される。
    // 既定は「数えた結果 0 件」（＝供給あり）とし、未供給は goodFaithViolations: null で明示する。
    private static PortfolioSnapshot Snapshot(
        BrokerAccountState? account,
        decimal dailyOrderedAmount = 0m,
        IReadOnlySet<(string Symbol, Market Market)>? symbolsTradedToday = null,
        int? goodFaithViolations = 0) =>
        new()
        {
            Capital = 100_000m,
            DailyOrderedAmount = dailyOrderedAmount,
            SymbolsTradedToday = symbolsTradedToday ?? new HashSet<(string, Market)>(),
            Account = account,
            GoodFaithViolations = goodFaithViolations is { } count
                ? GoodFaithViolationTally.Observed(count)
                : null,
        };

    private static readonly BrokerAccountState MarginAccount = new(AccountType.Margin);

    // 現金口座で、GFV 回避ガードの入力（決済済み資金）が**そろっている**状態。
    // これが無いと現金口座の買付は常に止まるため、他の統制を単独で観察できない。
    private static BrokerAccountState CashAccount(decimal settledCash = 1_000_000m) =>
        new(AccountType.Cash, settledCash);

    // 商品種別 3 種をすべて有効にした設定。口座種別による**実効値**（口座 ∩ 設定）だけを観察するため、
    // 設定側で絞ってしまわない。
    private static RiskManagementSettings AllProductTypesEnabled() => TradingDefaults.CreateSettings() with
    {
        Guard = TradingDefaults.CreateGuardSettings() with
        {
            EnabledProductTypes = new HashSet<ProductType>
            {
                ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell,
            },
        },
    };

    // 観測された口座種別と設定値を一致させる（決定3 の食い違い検知を発動させないため）。
    private static RiskManagementSettings SettingsFor(AccountType accountType)
    {
        var settings = AllProductTypesEnabled();
        return settings with { Guard = settings.Guard with { ConfiguredAccountType = accountType } };
    }

    // =====================================================================================
    // 1. 差金決済ガード（同日再エントリー禁止）の適用範囲 — 口座種別 × 市場 × 商品種別
    // =====================================================================================

    // FR-19, ADR-0021 決定4-1, IADR-0132 決定5, #332, #375: **組み合わせ表**。
    //
    // 適用する = 実効商品種別が現物 かつ（日本市場 **または** 現金口座）
    //   - 日本株の現物: 日本の差金決済規制（金商法 161 条の 2）。**口座種別に依存しない**
    //   - 米国株の現物: **現金口座のみ**（Good Faith Violation）。信用口座では発生しない
    //   - 信用（信用買い・空売り）: 同一保証金での同日無制限回転が可能なため**常に適用外**
    //
    // 表そのものが #332 の退行防止である（信用口座 × 米国 × 現物 が false のままであること）。
    [Theory]
    // --- 信用口座（既定・#332 で確定した現行挙動。ここが変わったら退行である） ---
    [InlineData(AccountType.Margin, Market.Japan, ProductType.Cash, true)]
    [InlineData(AccountType.Margin, Market.UnitedStates, ProductType.Cash, false)]
    [InlineData(AccountType.Margin, Market.Japan, ProductType.MarginLong, false)]
    [InlineData(AccountType.Margin, Market.UnitedStates, ProductType.MarginLong, false)]
    [InlineData(AccountType.Margin, Market.Japan, ProductType.ShortSell, false)]
    [InlineData(AccountType.Margin, Market.UnitedStates, ProductType.ShortSell, false)]
    // --- 現金口座（#375 で加わる分岐。米国株の現物だけが false → true へ変わる） ---
    [InlineData(AccountType.Cash, Market.Japan, ProductType.Cash, true)]
    [InlineData(AccountType.Cash, Market.UnitedStates, ProductType.Cash, true)]
    // 現金口座では信用系がそもそも成立しないが、判定関数としては商品種別で先に落ちることを固定する。
    [InlineData(AccountType.Cash, Market.Japan, ProductType.MarginLong, false)]
    [InlineData(AccountType.Cash, Market.UnitedStates, ProductType.ShortSell, false)]
    public void 差金決済ガードの適用は口座種別と市場と商品種別の組で決まる(
        AccountType accountType, Market market, ProductType productType, bool expected) =>
        AccountTypePolicy.AppliesSameDayReentry(market, productType, accountType).Should().Be(expected);

    // FR-19, ADR-0021 決定3: 口座種別が**不明**なとき、日本株の現物には引き続き適用する
    // （日本の差金決済規制は口座種別に依存しない）。米国株は口座種別が分からない限り本ガードの対象にせず、
    // **代わりに BrokerAccountTypeUnverified が新規建てそのものを止める**（統制の穴にはならない）。
    [Theory]
    [InlineData(Market.Japan, true)]
    [InlineData(Market.UnitedStates, false)]
    public void 口座種別が不明でも日本株現物への差金決済ガードは効き続ける(Market market, bool expected) =>
        AccountTypePolicy.AppliesSameDayReentry(market, ProductType.Cash, accountType: null)
            .Should().Be(expected);

    // FR-19, #375, ADR-0021 決定4-1: **両方向**（issue #375 の必須項目）。
    // 同じ米国株・同じ「当日取引済み」でも、現金口座では拒否され信用口座では拒否されない。
    [Fact]
    public void 現金口座では米国株の同日再エントリーを拒否する()
    {
        var traded = new HashSet<(string, Market)> { ("AAPL", Market.UnitedStates) };

        var result = RiskEvaluator.Evaluate(
            Entry(), SettingsFor(AccountType.Cash), Snapshot(CashAccount(), symbolsTradedToday: traded));

        result.Reasons.Should().Contain(RejectionReason.SameDayReentry);
    }

    [Fact]
    public void 信用口座では米国株の同日再エントリーを拒否しない()
    {
        // #332 / IADR-0132 決定5 の退行防止。ここが赤くなったら #332 の是正を巻き戻したことになる。
        var traded = new HashSet<(string, Market)> { ("AAPL", Market.UnitedStates) };

        var result = RiskEvaluator.Evaluate(
            Entry(), SettingsFor(AccountType.Margin), Snapshot(MarginAccount, symbolsTradedToday: traded));

        result.Reasons.Should().NotContain(RejectionReason.SameDayReentry);
    }

    [Fact]
    public void 信用口座でも日本株現物の同日再エントリーは拒否する()
    {
        // 日本の差金決済規制は口座種別に依存しない（#332 が限定したのはこちらであり、#375 は触っていない）。
        var traded = new HashSet<(string, Market)> { ("7203", Market.Japan) };

        var result = RiskEvaluator.Evaluate(
            Entry(symbol: "7203", market: Market.Japan),
            SettingsFor(AccountType.Margin),
            Snapshot(MarginAccount, symbolsTradedToday: traded));

        result.Reasons.Should().Contain(RejectionReason.SameDayReentry);
    }

    // =====================================================================================
    // 2. fail-closed（決定3） — **最重要**。ここが素通しだと決定3 が防ごうとした事故が再発する
    // =====================================================================================

    // FR-19, ADR-0021 決定3: 口座種別の**照会結果が無い**（照会失敗・未供給・種別不明）とき、新規建てを止める。
    // 「不明なら信用口座とみなす」に倒すと、現金口座なのに GFV 回避ガードが無効のまま回り、
    // 3 回の Good Faith Violation で **90 日の口座制限**に至る（しかも事後にしか分からない）。
    [Fact]
    public void 口座種別を照会できていなければ新規建てを拒否する()
    {
        var result = RiskEvaluator.Evaluate(
            Entry(), SettingsFor(AccountType.Margin), Snapshot(account: null));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BrokerAccountTypeUnverified);
    }

    // FR-19, ADR-0021 決定3:「照会と設定の二重化は冗長に見えるが、**食い違いの検知そのものが目的**」。
    [Theory]
    [InlineData(AccountType.Margin, AccountType.Cash)]
    [InlineData(AccountType.Cash, AccountType.Margin)]
    public void 照会結果と設定値が食い違えば新規建てを拒否する(AccountType observed, AccountType configured)
    {
        var settings = SettingsFor(configured);
        var snapshot = Snapshot(observed == AccountType.Cash ? CashAccount() : MarginAccount);

        var result = RiskEvaluator.Evaluate(Entry(), settings, snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BrokerAccountTypeUnverified);
    }

    [Fact]
    public void 照会結果と設定値が一致していれば口座種別を理由に拒否しない()
    {
        // 上の 2 つの否定形が「常に拒否している」ことの裏返しでないことを固定する（トートロジー防止）。
        var result = RiskEvaluator.Evaluate(
            Entry(), SettingsFor(AccountType.Margin), Snapshot(MarginAccount));

        result.Reasons.Should().NotContain(RejectionReason.BrokerAccountTypeUnverified);
        result.IsApproved.Should().BeTrue();
    }

    // FR-10, FR-19, ADR-0009 の不変条件, ADR-0007 2026-08-04 追補:
    // **手仕舞い（Close）と損切りは止めない。** 口座種別が分からないことを理由に建玉を閉じられなくするのは
    // 統制ではなく事故である（FR-10 の不変条件に反する）。
    [Theory]
    [InlineData(ProductType.Cash, TradeSide.Sell)]
    [InlineData(ProductType.MarginLong, TradeSide.Sell)]
    [InlineData(ProductType.ShortSell, TradeSide.Buy)] // 空売り建玉の買戻し
    public void 口座種別が不明でも手仕舞いは止めない(ProductType productType, TradeSide side)
    {
        var result = RiskEvaluator.Evaluate(
            Close(productType: productType, side: side),
            SettingsFor(AccountType.Margin),
            Snapshot(account: null));

        result.Reasons.Should().NotContain(RejectionReason.BrokerAccountTypeUnverified);
        result.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void 現金口座でも信用建玉の手仕舞いは止めない()
    {
        // **口座種別を切り替えた瞬間に、その口座で建てた信用建玉が閉じられなくなる**ことを防ぐ。
        var result = RiskEvaluator.Evaluate(
            Close(productType: ProductType.MarginLong),
            SettingsFor(AccountType.Cash),
            Snapshot(CashAccount()));

        result.IsApproved.Should().BeTrue();
    }

    // FR-19, IADR-0153 決定2: 内蔵 paper（外部へ一度も発注しない擬似約定）は口座種別を要求しない。
    // ブローカー口座そのものが存在せず、口座種別に依存する事故（GFV・差金決済）が起こり得ないためである。
    [Fact]
    public void 内蔵paperの新規建ては口座種別を要求しない()
    {
        var result = RiskEvaluator.Evaluate(
            Entry(mode: BrokerProvider.InternalPaper),
            SettingsFor(AccountType.Margin),
            Snapshot(account: null));

        result.Reasons.Should().NotContain(RejectionReason.BrokerAccountTypeUnverified);
        result.IsApproved.Should().BeTrue();
    }

    // 迂回路になっていないことの裏返し: **外部へ発注する経路はすべて口座種別を要求する。**
    [Theory]
    [InlineData(BrokerProvider.MoomooSimulate)]
    [InlineData(BrokerProvider.MoomooReal)]
    public void 外部へ発注する発注先は口座種別の確認を要求する(BrokerProvider provider) =>
        AccountTypePolicy.RequiresVerifiedAccount(provider).Should().BeTrue();

    [Fact]
    public void 内蔵paperだけが口座種別の確認を免除される() =>
        AccountTypePolicy.RequiresVerifiedAccount(BrokerProvider.InternalPaper).Should().BeFalse();

    // =====================================================================================
    // 3. GFV 回避ガード（決定4-2） — 決済済み資金の境界
    // =====================================================================================

    // FR-19, ADR-0021 決定4-2: 判定は**当日の新規建て累計 ＋ 本注文**で行う（Issue #27 と同じ理由。
    // 1 件ずつの比較では、決済済み資金の範囲内の注文を複数回通して累計で超過できる）。
    [Theory]
    [InlineData(0, 999, false)]     // 上限の直下 → 通す
    [InlineData(0, 1_000, false)]   // 上限ちょうど → 通す（超過していない）
    [InlineData(0, 1_001, true)]    // 上限超過 → 拒否
    [InlineData(900, 100, false)]   // 累計ちょうど → 通す
    [InlineData(900, 101, true)]    // 累計で超過 → 拒否（1 件ずつの比較なら通ってしまう額）
    public void 決済済み資金を超える買付を境界値で拒否する(
        decimal alreadyOrdered, decimal notional, bool expectedRejected)
    {
        var snapshot = Snapshot(
            CashAccount(settledCash: 1_000m), dailyOrderedAmount: alreadyOrdered);

        var result = RiskEvaluator.Evaluate(
            Entry(quantity: 1, price: notional), SettingsFor(AccountType.Cash), snapshot);

        result.Reasons.Contains(RejectionReason.CashAccountSettlementHold).Should().Be(expectedRejected);
    }

    // FR-19, ADR-0021 決定4-2（**否定形・fail-closed**）:
    // 決済済み資金が供給されないとき、現金口座の買付は止まる。「残高が分からないから通す」は統制にならない。
    // moomoo API に決済済み資金の専用フィールドが存在しないことは実測済みであり（IADR-0153 決定4）、
    // 現時点では**この経路が常に発動する**。
    [Fact]
    public void 決済済み資金が未供給なら現金口座の買付を拒否する()
    {
        var snapshot = Snapshot(new BrokerAccountState(AccountType.Cash, SettledCashInBase: null));

        var result = RiskEvaluator.Evaluate(Entry(), SettingsFor(AccountType.Cash), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.CashAccountSettlementHold);
    }

    // 売却（Sell）は現金を払い出さないため本ガードの対象外である。GFV は「未決済の売却代金で買った」ことで
    // 起きるものであり、売り自体を止める理由が無い。
    [Fact]
    public void 決済済み資金が未供給でも現金口座の売却は本ガードで止めない()
    {
        var snapshot = Snapshot(new BrokerAccountState(AccountType.Cash, SettledCashInBase: null));

        var result = RiskEvaluator.Evaluate(
            Entry(side: TradeSide.Sell), SettingsFor(AccountType.Cash), snapshot);

        result.Reasons.Should().NotContain(RejectionReason.CashAccountSettlementHold);
    }

    // 信用口座では決済済み資金の供給が無くても本ガードは働かない（売却代金を決済前に再利用できるため）。
    [Fact]
    public void 信用口座では決済済み資金が未供給でも買付を止めない()
    {
        var result = RiskEvaluator.Evaluate(
            Entry(), SettingsFor(AccountType.Margin), Snapshot(MarginAccount));

        result.Reasons.Should().NotContain(RejectionReason.CashAccountSettlementHold);
        result.IsApproved.Should().BeTrue();
    }

    // 手仕舞いには適用しない（ADR-0009 の不変条件）。
    [Fact]
    public void 決済済み資金が未供給でも手仕舞いの買戻しは止めない()
    {
        var snapshot = Snapshot(new BrokerAccountState(AccountType.Cash, SettledCashInBase: null));

        var result = RiskEvaluator.Evaluate(
            Close(side: TradeSide.Buy), SettingsFor(AccountType.Cash), snapshot);

        result.Reasons.Should().NotContain(RejectionReason.CashAccountSettlementHold);
        result.IsApproved.Should().BeTrue();
    }

    // =====================================================================================
    // 4. GFV 発生回数の追跡と警告（決定4-3）
    // =====================================================================================

    // FR-19, ADR-0021 決定4-3:「2 回目で警告、**3 回目の手前で新規建てを停止**」。
    // 警告と停止が同じ回数で立つのは意図どおりである（3 回目が起きた時点で 90 日の制限が確定するため）。
    [Theory]
    [InlineData(null, true, false)] // **未供給は停止する**（2 回に達していないことを確認できない）
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(2, true, true)]     // 2 回で停止し、かつ警告する
    [InlineData(3, true, true)]
    public void GFV発生回数の停止と警告のしきい値を境界値で固定する(
        int? violationCount, bool expectedBlocked, bool expectedWarned)
    {
        // #425, IADR-0165 決定2: 件数は第一級の値（GoodFaithViolationTally）で渡す。**null は未供給**であり、
        // Observed(0)（＝数えた結果 0 件）とは別物である。
        var tally = violationCount is { } count ? GoodFaithViolationTally.Observed(count) : null;

        AccountTypePolicy.BlocksForGoodFaithViolations(tally).Should().Be(expectedBlocked);
        AccountTypePolicy.WarnsForGoodFaithViolations(tally).Should().Be(expectedWarned);
    }

    [Theory]
    [InlineData(null)] // fail-closed: 未供給でも止める
    [InlineData(2)]
    [InlineData(3)]
    public void GFV発生回数が停止基準に達していれば現金口座の新規建てを拒否する(int? violationCount)
    {
        var snapshot = Snapshot(CashAccount(), goodFaithViolations: violationCount);

        var result = RiskEvaluator.Evaluate(Entry(), SettingsFor(AccountType.Cash), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.GoodFaithViolationLimitReached);
    }

    [Fact]
    public void GFV発生回数が1回までなら現金口座の新規建てを止めない()
    {
        var result = RiskEvaluator.Evaluate(
            Entry(), SettingsFor(AccountType.Cash), Snapshot(CashAccount(), goodFaithViolations: 1));

        result.Reasons.Should().NotContain(RejectionReason.GoodFaithViolationLimitReached);
        result.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void GFV発生回数が未供給でも手仕舞いは止めない()
    {
        var snapshot = Snapshot(CashAccount(), goodFaithViolations: null);

        var result = RiskEvaluator.Evaluate(Close(), SettingsFor(AccountType.Cash), snapshot);

        result.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void 信用口座ではGFV回数が未供給でも新規建てを止めない()
    {
        // 信用口座では GFV が発生しないため、回数の供給を要求しない（過剰な停止を作らない）。
        var result = RiskEvaluator.Evaluate(
            Entry(), SettingsFor(AccountType.Margin), Snapshot(MarginAccount));

        result.Reasons.Should().NotContain(RejectionReason.GoodFaithViolationLimitReached);
        result.IsApproved.Should().BeTrue();
    }

    // =====================================================================================
    // 5. 信用買い・空売りの発注経路の遮断（決定4-4）と ADR-0016 の適用対象外化（決定5）
    // =====================================================================================

    // FR-19, ADR-0021 決定4-4: 現金口座では**株を借りられない**ため信用買い・空売りが成立しない。
    // 設定で有効になっていても実効的には無効である（実効値 = 利用者設定 ∩ 口座が対応する種別）。
    [Theory]
    [InlineData(AccountType.Margin, ProductType.Cash, true)]
    [InlineData(AccountType.Margin, ProductType.MarginLong, true)]
    [InlineData(AccountType.Margin, ProductType.ShortSell, true)]
    [InlineData(AccountType.Cash, ProductType.Cash, true)]
    [InlineData(AccountType.Cash, ProductType.MarginLong, false)]
    [InlineData(AccountType.Cash, ProductType.ShortSell, false)]
    public void 口座が対応する商品種別を口座種別ごとに固定する(
        AccountType accountType, ProductType productType, bool expected) =>
        AccountTypePolicy.Supports(accountType, productType).Should().Be(expected);

    // **否定形**: 設定で有効化されていても、現金口座では信用系の新規建てが通らない（多層防御の発注側）。
    [Theory]
    [InlineData(ProductType.MarginLong, TradeSide.Buy)]
    [InlineData(ProductType.ShortSell, TradeSide.Sell)]
    public void 現金口座では信用買いと空売りの新規建てを拒否する(ProductType productType, TradeSide side)
    {
        var result = RiskEvaluator.Evaluate(
            Entry(productType: productType, side: side),
            SettingsFor(AccountType.Cash), // 商品種別は 3 種すべて有効にしてある
            Snapshot(CashAccount()));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.ProductTypeDisabled);
    }

    [Theory]
    [InlineData(ProductType.MarginLong, TradeSide.Buy)]
    [InlineData(ProductType.ShortSell, TradeSide.Sell)]
    public void 信用口座では有効化された信用買いと空売りの新規建てを商品種別では拒否しない(
        ProductType productType, TradeSide side)
    {
        var result = RiskEvaluator.Evaluate(
            Entry(productType: productType, side: side),
            SettingsFor(AccountType.Margin),
            Snapshot(MarginAccount));

        result.Reasons.Should().NotContain(RejectionReason.ProductTypeDisabled);
    }

    // FR-19, ADR-0021 決定5: 現金口座では ADR-0016（空売り）の**全決定が適用対象外**である。
    // 借株料・維持率・権利確定日といった空売り専用の拒否理由を返しても実態（口座の能力の問題）と食い違う。
    [Fact]
    public void 現金口座では空売り統制群を評価しない()
    {
        var result = RiskEvaluator.Evaluate(
            Entry(productType: ProductType.ShortSell, side: TradeSide.Sell),
            SettingsFor(AccountType.Cash),
            Snapshot(CashAccount()));

        // 口座の能力の問題として ProductTypeDisabled 一本で止まる。
        result.Reasons.Should().Contain(RejectionReason.ProductTypeDisabled);
        result.Reasons.Should().NotContain(RejectionReason.ShortSellDisabled);
        result.Reasons.Should().NotContain(RejectionReason.BorrowUnavailable);
        result.Reasons.Should().NotContain(RejectionReason.StopOrderRequired);
    }

    [Theory]
    [InlineData(AccountType.Margin, true)]
    [InlineData(null, true)] // **不明なら評価する**——空売り統制自体がフェイルクローズであり、省くと緩む
    [InlineData(AccountType.Cash, false)]
    public void 空売り統制群の評価可否を口座種別ごとに固定する(AccountType? accountType, bool expected) =>
        AccountTypePolicy.AppliesShortSellControls(accountType).Should().Be(expected);

    // 口座種別が不明なときに空売り統制が評価されることの実地確認（省略すると緩む側になる）。
    [Fact]
    public void 口座種別が不明なら空売り統制群を評価する()
    {
        var result = RiskEvaluator.Evaluate(
            Entry(productType: ProductType.ShortSell, side: TradeSide.Sell),
            SettingsFor(AccountType.Margin),
            Snapshot(account: null));

        result.IsApproved.Should().BeFalse();
        // 口座種別の未確認と空売り統制（文脈未供給）の**両方**が立つ。
        result.Reasons.Should().Contain(RejectionReason.BrokerAccountTypeUnverified);
        result.Reasons.Should().Contain(RejectionReason.BorrowUnavailable);
    }

    // =====================================================================================
    // 6. 既定値（決定1）
    // =====================================================================================

    // FR-19, ADR-0021 決定1: 口座種別の既定は**信用口座**（現金口座は拡張として対応する）。
    // ただしこれは「照会できなければ信用口座として扱う」ことを意味しない（上の fail-closed 群を参照）。
    [Fact]
    public void 設定の既定の口座種別は信用口座である() =>
        TradingDefaults.CreateGuardSettings().ConfiguredAccountType.Should().Be(AccountType.Margin);
}
