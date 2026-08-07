using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-19, FR-10, FR-11, UC-06, #425, ADR-0025 決定2, ADR-0021 決定4-2/4-3, IADR-0166:
// **GFV 発生回数の自前計数**（未決済資金による買付を自ら記録して数える）の判定。
//
// issue #425「退行防止（テスト必須）」が挙げた項目:
//   - MaxCashBuy / AvlWithdrawalCash / MaxWithdrawal が決済済み資金として使われていないこと（構造で担保。
//     機械的検査は scripts/check-banned-settled-cash-sources.js、単体試験は scripts/scripts.repo.test.js）
//   - 計数値が供給されないとき新規建てが GoodFaithViolationLimitReached で拒否されること（fail-closed。
//     **CashAccountSettlementHold へ写像しない**）
//   - ガードが働いている限り計数が 0 のままであること／**0 が未供給と区別されること**
//   - 2 件目で警告・3 件目の手前で新規建て停止（境界）
//   - 信用口座は本統制の影響を受けないこと（否定形）
//
// ★★ 本ファイルが数えているものを取り違えないこと（ADR-0025 §理由） ★★
//   自前で数えられるのは**自らのガードをすり抜けた買付**だけであり、ブローカー側が独自に GFV と判定した
//   事象は捕捉できない。したがって本計数は「ブローカーの GFV カウンタの写し」ではなく
//   「**自らのガードの失敗回数**」である。**両者が一致する保証はない。**
public class GoodFaithViolationCountingTests
{
    private static OrderIntent Entry(
        TradeSide side = TradeSide.Buy,
        int quantity = 1,
        decimal price = 1_000m) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate,
            quantity, price, PositionEffect.Open);

    private static PortfolioSnapshot Snapshot(
        BrokerAccountState? account,
        int? goodFaithViolations,
        decimal dailyOrderedAmount = 0m) =>
        new()
        {
            Capital = 100_000m,
            DailyOrderedAmount = dailyOrderedAmount,
            Account = account,
            GoodFaithViolations = goodFaithViolations is { } c ? GoodFaithViolationTally.Observed(c) : null,
        };

    private static RiskManagementSettings SettingsFor(AccountType accountType)
    {
        var settings = TradingDefaults.CreateSettings();
        return settings with { Guard = settings.Guard with { ConfiguredAccountType = accountType } };
    }

    // =====================================================================================
    // 1. 停止・警告のしきい値（境界値）— **計画確定値は 2 のまま**
    // =====================================================================================

    // FR-19, ADR-0021 決定4-3, ADR-0025 決定2: 停止基準は 2 件のままである（#425 は変更しない）。
    // 数値そのものを固定するのは、しきい値の書き換えが**最も静かに統制を壊す**からである。
    [Fact]
    public void GFVの停止基準は2件のままである() =>
        AccountTypePolicy.GoodFaithViolationStopThreshold.Should().Be(2);

    // 境界: 1 件では止まらず、2 件で止まる。**3 件目の手前で止める**という決定4-3 の要求そのもの。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]  // 1 件では止まらない（正の対照＝トートロジー防止）
    [InlineData(2, true)]   // 境界
    [InlineData(3, true)]
    public void 自前計数は2件目から新規建てを止める(int count, bool expectedBlocked)
    {
        var tally = GoodFaithViolationTally.Observed(count);

        tally.BlocksNewEntry.Should().Be(expectedBlocked);
        AccountTypePolicy.BlocksForGoodFaithViolations(tally).Should().Be(expectedBlocked);
    }

    // 警告も同じ回数で立つ（3 回目が起きた時点で 90 日の口座制限が確定するため）。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void 自前計数は2件目から警告する(int count, bool expectedWarned)
    {
        var tally = GoodFaithViolationTally.Observed(count);

        tally.Warns.Should().Be(expectedWarned);
        AccountTypePolicy.WarnsForGoodFaithViolations(tally).Should().Be(expectedWarned);
    }

    // =====================================================================================
    // 2. 「0 件」と「未供給」の区別（#424 の表示規約と同じ区別・IADR-0148 決定1 の規律）
    // =====================================================================================

    // **否定形（最重要）**: 未供給は止める。0 件は止めない。**この 2 つを同じ形で表すと統制が消える。**
    [Fact]
    public void 未供給は止め_0件は止めない()
    {
        AccountTypePolicy.BlocksForGoodFaithViolations(null).Should().BeTrue();
        AccountTypePolicy.BlocksForGoodFaithViolations(GoodFaithViolationTally.Observed(0)).Should().BeFalse();
    }

    // **未供給は警告しない**（警告は「実際に 2 件起きた」ことを伝えるものであり、供給の不在は停止側で表す）。
    [Fact]
    public void 未供給は警告しない() =>
        AccountTypePolicy.WarnsForGoodFaithViolations(null).Should().BeFalse();

    // 0 件は「数えた結果 0 件だった」という**主張のある値**であり、未供給（null）とは別物である。
    [Fact]
    public void 数えた結果の0件は未供給と別物である()
    {
        GoodFaithViolationTally? unsupplied = null;
        var zero = GoodFaithViolationTally.Observed(0);

        zero.Should().NotBeNull();
        zero.Count.Should().Be(0);
        unsupplied.Should().BeNull();
    }

    // 負の件数は計数結果として成立しない（供給側の異常を黙って通さない）。
    [Fact]
    public void 負の件数は受け付けない()
    {
        var act = () => GoodFaithViolationTally.Observed(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // =====================================================================================
    // 3. 計数の対象事象（＝統制2 が拒否しようとする事象と同じであること）
    // =====================================================================================

    // FR-19, ADR-0025 決定2: **計数の対象は統制2 が拒否しようとする事象と同じ**である。
    // 述語（AccountTypePolicy.ExceedsSettledCash）を単一情報源にしていることの表明——
    // 片方だけ直すと「拒否はするが数えない」「数えるが拒否しない」がすり抜ける。
    [Theory]
    [InlineData(null, 1_000, true)]    // **未供給は「説明できない買付」**（fail-closed）
    [InlineData(1_000, 999, false)]
    [InlineData(1_000, 1_000, false)]  // ちょうどは超過していない
    [InlineData(1_000, 1_001, true)]
    public void 決済済み資金を超える買付の判定は単一の述語である(
        // [InlineData] は decimal? を運べないため int? で受けて変換する（値の意味は変わらない）。
        int? settledCash, int amount, bool expected) =>
        AccountTypePolicy.ExceedsSettledCash((decimal?)settledCash, amount).Should().Be(expected);

    // 検出（事後）の条件表。**現金口座 × 新規建ての買付 × 決済済み資金で説明できない**の 3 つがそろったときだけ数える。
    [Theory]
    // --- 数える（＝ガードをすり抜けた買付） ---
    [InlineData(AccountType.Cash, null, TradeSide.Buy, PositionEffect.Open, 1_000, true)]
    [InlineData(AccountType.Cash, 999, TradeSide.Buy, PositionEffect.Open, 1_000, true)]
    // --- 数えない ---
    [InlineData(AccountType.Cash, 1_000, TradeSide.Buy, PositionEffect.Open, 1_000, false)] // 資金の範囲内
    [InlineData(AccountType.Cash, null, TradeSide.Sell, PositionEffect.Open, 1_000, false)] // 売却は GFV を起こさない
    [InlineData(AccountType.Cash, null, TradeSide.Buy, PositionEffect.Close, 1_000, false)] // 手仕舞いの買戻し
    [InlineData(AccountType.Margin, null, TradeSide.Buy, PositionEffect.Open, 1_000, false)] // 信用口座
    public void 未決済資金による買付だけをGFV発生として数える(
        AccountType accountType,
        // [InlineData] は decimal? を運べないため int? で受けて変換する（値の意味は変わらない）。
        int? settledCash,
        TradeSide side,
        PositionEffect effect,
        int amount,
        bool expected) =>
        GoodFaithViolationDetection
            .CountsAsViolation(new BrokerAccountState(accountType, (decimal?)settledCash), side, effect, amount)
            .Should().Be(expected);

    // **否定形**: 口座種別を確認できていない（観測 null）状態では数えない。
    // 不明を現金口座とみなして数えると、**信用口座の通常の回転売買が違反として積み上がり、
    // 2 件で恒久的に新規建てが止まる**（統制ではなく事故）。不明な口座の新規建ては
    // BrokerAccountTypeUnverified が既に止めている（IADR-0153 決定1）ので穴にならない。
    [Fact]
    public void 口座種別を確認できていなければGFV発生として数えない() =>
        GoodFaithViolationDetection
            .CountsAsViolation(null, TradeSide.Buy, PositionEffect.Open, 1_000m)
            .Should().BeFalse();

    // **ガードが正しく働いていれば計数は 0 のままである**（ADR-0025 決定2 の明文）。
    // ガードが拒否する事象（＝述語が真）と、計数が数える事象は**同一**でなければならない。
    [Theory]
    [InlineData(null, 1_000)]
    [InlineData(500, 1_000)]
    [InlineData(1_000, 1_000)]
    [InlineData(1_000, 1_001)]
    public void ガードが拒否する事象と計数が数える事象は一致する(int? settledCash, int amount)
    {
        var account = new BrokerAccountState(AccountType.Cash, (decimal?)settledCash);

        // 発注前のガード（統制2）: 述語が真なら CashAccountSettlementHold で拒否する。
        var rejected = RiskEvaluator.Evaluate(
                Entry(price: amount),
                SettingsFor(AccountType.Cash),
                Snapshot(account, goodFaithViolations: 0))
            .Reasons.Contains(RejectionReason.CashAccountSettlementHold);

        // 事後の計数: 同じ入力で同じ判定になる（＝すり抜けたときだけ 1 件になる）。
        var counted = GoodFaithViolationDetection
            .CountsAsViolation(account, TradeSide.Buy, PositionEffect.Open, amount);

        counted.Should().Be(rejected);
    }

    // =====================================================================================
    // 4. fail-closed（ADR-0025 決定2「計数値が供給されない状態では新規建てを拒否する」）
    // =====================================================================================

    // **否定形（最重要）**: 計数が供給されないとき、現金口座の新規建ては止まる。
    [Fact]
    public void 計数が未供給なら現金口座の新規建てを拒否する()
    {
        var snapshot = Snapshot(
            new BrokerAccountState(AccountType.Cash, 1_000_000m), goodFaithViolations: null);

        var result = RiskEvaluator.Evaluate(Entry(), SettingsFor(AccountType.Cash), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.GoodFaithViolationLimitReached);
    }

    // **否定形**: 未供給を `CashAccountSettlementHold` へ写像しない。**解除条件が違う**——
    // 前者は「違反記録の失効」、後者は「T+1 の決済」である（IADR-0153 決定5）。
    // 畳むと監査ログ（FR-11）で「なぜ止まっているのか」が実態と食い違う。
    [Fact]
    public void 計数の未供給を決済保留へ写像しない()
    {
        var snapshot = Snapshot(
            new BrokerAccountState(AccountType.Cash, 1_000_000m), goodFaithViolations: null);

        var result = RiskEvaluator.Evaluate(Entry(), SettingsFor(AccountType.Cash), snapshot);

        result.Reasons.Should().Contain(RejectionReason.GoodFaithViolationLimitReached);
        result.Reasons.Should().NotContain(RejectionReason.CashAccountSettlementHold);
    }

    // 計数が供給され 0 件なら、本統制では止めない（正の対照＝トートロジー防止）。
    [Fact]
    public void 計数が0件なら本統制では新規建てを止めない()
    {
        var snapshot = Snapshot(
            new BrokerAccountState(AccountType.Cash, 1_000_000m), goodFaithViolations: 0);

        var result = RiskEvaluator.Evaluate(Entry(), SettingsFor(AccountType.Cash), snapshot);

        result.Reasons.Should().NotContain(RejectionReason.GoodFaithViolationLimitReached);
        result.IsApproved.Should().BeTrue();
    }

    // **#425 の完了後も現金口座は使えないままである**（ADR-0025 決定3）。
    // 自前計数が供給されて統制3 が解けても、**統制2（決済済み資金）が未供給のままなので買付は止まる**。
    // IADR-0153 の fail-closed を解除していないことの表明であり、ここが緑でなくなったら
    // 「現金口座が選べるようになった」ことを意味する（PoC 項目 8 の成立が前提）。
    [Fact]
    public void 自前計数を供給しても決済済み資金が無い限り現金口座の買付は止まる()
    {
        var snapshot = Snapshot(
            new BrokerAccountState(AccountType.Cash, SettledCashInBase: null), goodFaithViolations: 0);

        var result = RiskEvaluator.Evaluate(Entry(), SettingsFor(AccountType.Cash), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.CashAccountSettlementHold);
    }

    // 手仕舞いは止めない（ADR-0009 の不変条件）。計数が未供給でも同じである。
    [Fact]
    public void 計数が未供給でも手仕舞いは止めない()
    {
        var snapshot = Snapshot(
            new BrokerAccountState(AccountType.Cash, 1_000_000m), goodFaithViolations: null);
        var close = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            1, 1_000m, PositionEffect.Close);

        var result = RiskEvaluator.Evaluate(close, SettingsFor(AccountType.Cash), snapshot);

        result.IsApproved.Should().BeTrue();
    }

    // =====================================================================================
    // 5. 信用口座は本統制の影響を受けない（否定形）
    // =====================================================================================

    [Fact]
    public void 信用口座では計数が未供給でも新規建てを止めない()
    {
        var snapshot = Snapshot(new BrokerAccountState(AccountType.Margin), goodFaithViolations: null);

        var result = RiskEvaluator.Evaluate(Entry(), SettingsFor(AccountType.Margin), snapshot);

        result.Reasons.Should().NotContain(RejectionReason.GoodFaithViolationLimitReached);
        result.IsApproved.Should().BeTrue();
    }

    // 信用口座では、自前計数が停止基準に達していても本統制は働かない
    // （GFV は現金口座の制度であり、信用口座では売却代金を決済前に再利用できる）。
    [Fact]
    public void 信用口座では計数が停止基準に達していても新規建てを止めない()
    {
        var snapshot = Snapshot(new BrokerAccountState(AccountType.Margin), goodFaithViolations: 3);

        var result = RiskEvaluator.Evaluate(Entry(), SettingsFor(AccountType.Margin), snapshot);

        result.Reasons.Should().NotContain(RejectionReason.GoodFaithViolationLimitReached);
        result.IsApproved.Should().BeTrue();
    }

    // =====================================================================================
    // 6. 供給元の構造（#425「MaxCashBuy を構造で止める」の型レベルの担保）
    // =====================================================================================

    // **否定形**: ブローカー照会の型（BrokerAccountState）は **GFV 発生回数の欄を持たない**。
    // 欄が無いこと自体が「ブローカーの GFV カウンタの写しである」という取り違えを不可能にする
    // （IADR-0159 の ReportView が拒否理由由来の入力を**持たない**ことで取り違えを封じたのと同じ形）。
    // 欄を復活させると本テストが赤くなる。
    [Fact]
    public void ブローカー照会の型はGFV発生回数の欄を持たない()
    {
        var names = typeof(BrokerAccountState).GetProperties().Select(p => p.Name).ToList();

        names.Should().NotContain("GoodFaithViolationCount");
        names.Should().Contain("SettledCashInBase", "決済済み資金はブローカー由来の値であり本型に残す（PoC 項目 8）");
    }

    // **否定形**: 決済済み資金の入力は 1 つだけである。ADR-0025 が名指しで禁じた代替値
    // （MaxCashBuy / AvlWithdrawalCash / MaxWithdrawal）に対応する欄を本型に生やさせない
    // （生やした瞬間に「近い値があるから使えるのでは」が始まる）。
    // コード全体での参照の遮断は scripts/check-banned-settled-cash-sources.js が担う。
    [Fact]
    public void ブローカー照会の型は決済済み資金の代替値の欄を持たない()
    {
        var names = typeof(BrokerAccountState).GetProperties().Select(p => p.Name).ToList();

        names.Should().NotContain("MaxCashBuy",
            "現金買付余力は未決済の売却代金を含み得る。分母に据えると GFV 回避ガードが GFV を許可する（ADR-0025）");
        names.Should().NotContain("AvlWithdrawalCash", "出金可能額は決済済み資金とは別概念である（ADR-0025）");
        names.Should().NotContain("MaxWithdrawal", "出金可能額は決済済み資金とは別概念である（ADR-0025）");
    }
}
