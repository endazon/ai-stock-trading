using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-11, UC-07, ADR-0016 決定15, #339, IADR-0226: 取引記録の経費区分（7 種）の統制テスト。
//
// 統制系の 3 点セット（境界値テーブル・プロパティベース・否定形）で固定する。
// 🔴 本区分の中心にある要求は「**配当相当額の支払いを配当と混同しないこと**」であり、
// 混同していないことは肯定形では表明できない。**否定形の束**（下部）がその実体である。
public class TradeExpenseCategoryTests
{
    // ── 境界値テーブル: 7 区分と序数 ─────────────────────────────────────────
    //
    // 区分は永続（監査台帳の JSON）と HTTP 経路で往来し得るため、既存メンバの間へ挿入すると
    // **過去に記録した経費の意味が変わる**。追加は常に末尾へ行う（RejectionReason と同じ規律）。
    // **表に無いメンバがあれば `全区分が表に載っている` が失敗する。**
    public static TheoryData<TradeExpenseCategory, int> FixedOrdinals { get; } = new()
    {
        { TradeExpenseCategory.Realized, 0 },
        { TradeExpenseCategory.BorrowFee, 1 },
        { TradeExpenseCategory.MarginInterest, 2 },
        { TradeExpenseCategory.DividendInLieu, 3 },
        { TradeExpenseCategory.Commission, 4 },
        { TradeExpenseCategory.Fee, 5 },
        { TradeExpenseCategory.FxCost, 6 },
    };

    [Theory]
    [MemberData(nameof(FixedOrdinals))]
    public void 経費区分の序数は固定である(TradeExpenseCategory category, int expected) =>
        ((int)category).Should().Be(expected);

    [Fact]
    public void 全区分が表に載っている_区分を増やしたら表も増やす()
    {
        var tabulated = FixedOrdinals.Select(row => row.Data.Item1).ToArray();

        Enum.GetValues<TradeExpenseCategory>().Should().BeEquivalentTo(
            tabulated,
            "新しい区分を追加したら本表へ 1 行足す。表に無いメンバは序数の固定から漏れる");
    }

    // ADR-0016 決定15 / FR-11 が名指しした区分はちょうど 7 つである。増減はどちらも計画との乖離であり、
    // 新 ADR か planning への環流なしに起きてはならない。
    [Fact]
    public void 経費区分は計画が名指しした_7_種ちょうどである()
    {
        Enum.GetValues<TradeExpenseCategory>().Should().HaveCount(7);
        TradeExpenseClassification.All.Should().HaveCount(7);
    }

    [Fact]
    public void 区分の一覧は計画の列挙順である()
    {
        TradeExpenseClassification.All.Should().Equal(
            TradeExpenseCategory.Realized,
            TradeExpenseCategory.BorrowFee,
            TradeExpenseCategory.MarginInterest,
            TradeExpenseCategory.DividendInLieu,
            TradeExpenseCategory.Commission,
            TradeExpenseCategory.Fee,
            TradeExpenseCategory.FxCost);
    }

    // ── 境界値テーブル: 7 区分と性質 ─────────────────────────────────────────
    public static TheoryData<TradeExpenseCategory, TradeExpenseNature> FixedNatures { get; } = new()
    {
        { TradeExpenseCategory.Realized, TradeExpenseNature.RealizedProfitAndLoss },
        { TradeExpenseCategory.BorrowFee, TradeExpenseNature.TransferCost },
        { TradeExpenseCategory.MarginInterest, TradeExpenseNature.TransferCost },
        // 🔴 配当相当額の支払いは**費用**である（税務上は譲渡費用に近い扱い）。
        { TradeExpenseCategory.DividendInLieu, TradeExpenseNature.TransferCost },
        { TradeExpenseCategory.Commission, TradeExpenseNature.TransferCost },
        { TradeExpenseCategory.Fee, TradeExpenseNature.TransferCost },
        { TradeExpenseCategory.FxCost, TradeExpenseNature.TransferCost },
    };

    [Theory]
    [MemberData(nameof(FixedNatures))]
    public void 区分の性質は固定である(TradeExpenseCategory category, TradeExpenseNature expected) =>
        TradeExpenseClassification.NatureOf(category).Should().Be(expected);

    // ── プロパティベース ─────────────────────────────────────────────────────

    [Fact]
    public void 全区分に性質が定義されている_写像漏れは例外で落ちる()
    {
        foreach (var category in Enum.GetValues<TradeExpenseCategory>())
        {
            var act = () => TradeExpenseClassification.NatureOf(category);
            act.Should().NotThrow($"区分 {category} の性質が写像されていない");
        }
    }

    [Fact]
    public void 費用の集合は実現損益だけを除いた残り全部である()
    {
        TradeExpenseClassification.Expenses.Should().BeEquivalentTo(
            Enum.GetValues<TradeExpenseCategory>().Where(c => c != TradeExpenseCategory.Realized));
        TradeExpenseClassification.Expenses.Should().HaveCount(6);
    }

    [Fact]
    public void 実現損益だけが費用ではない()
    {
        foreach (var category in Enum.GetValues<TradeExpenseCategory>())
        {
            TradeExpenseClassification.IsExpense(category)
                .Should().Be(category != TradeExpenseCategory.Realized, $"区分 {category}");
        }
    }

    // ── 否定形 ───────────────────────────────────────────────────────────────

    // 🔴 ADR-0016 決定15 の要点。配当相当額の支払いを実現損益（＝収入側）へ寄せると、
    // 税務上の扱い（譲渡費用に近い）が失われ、**記録時に分けないと後から区別できない**。
    [Fact]
    public void 否定形_配当相当額は実現損益として扱われない()
    {
        TradeExpenseClassification.NatureOf(TradeExpenseCategory.DividendInLieu)
            .Should().NotBe(TradeExpenseNature.RealizedProfitAndLoss);
    }

    [Fact]
    public void 否定形_配当相当額は費用の集合から漏れない()
    {
        TradeExpenseClassification.Expenses.Should().Contain(TradeExpenseCategory.DividendInLieu);
        TradeExpenseClassification.IsExpense(TradeExpenseCategory.DividendInLieu).Should().BeTrue();
    }

    // 🔴 **収入側の性質は enum に存在しない。** 混同しようにも置き場が無い、という構造の固定である。
    // ここへ「受取配当」を表す値を足すと本テストが落ちる。
    [Fact]
    public void 否定形_性質に収入を表す値は無い()
    {
        Enum.GetValues<TradeExpenseNature>().Should().BeEquivalentTo(
            new[] { TradeExpenseNature.RealizedProfitAndLoss, TradeExpenseNature.TransferCost });
    }

    // 🔴 `Dividend` / `DividendIncome` のような**受取側の区分を後から足せない**ようにする。
    // 足した瞬間にここが落ち、「配当と混同しない」という要件へ立ち戻ることを強制する。
    [Fact]
    public void 否定形_配当を名に持つ区分は配当相当額の支払いただ_1_つである()
    {
        Enum.GetNames<TradeExpenseCategory>()
            .Where(n => n.Contains("Dividend", StringComparison.Ordinal))
            .Should().ContainSingle()
            .Which.Should().Be(nameof(TradeExpenseCategory.DividendInLieu));
    }

    // 未知の値（キャストで作れる）は既定値へ倒さず例外で落ちる。
    // 黙って「実現損益でも費用でもない何か」として集計から漏れるのが最悪の壊れ方である。
    [Fact]
    public void 否定形_未知の区分は既定値へ倒れず例外になる()
    {
        var unknown = (TradeExpenseCategory)999;

        var act = () => TradeExpenseClassification.NatureOf(unknown);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
