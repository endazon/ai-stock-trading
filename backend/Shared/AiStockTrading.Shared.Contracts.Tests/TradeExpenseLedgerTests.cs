using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-11, UC-07, ADR-0016 決定15, ADR-0027 決定2, #339, IADR-0226:
// 経費明細を**建玉単位**（(銘柄, 市場)）へ畳む集計の統制テスト。
//
// 「後から集計可能な粒度で記録する」（FR-11）が満たされていることは、
// **明細から建玉別・区分別の値が機械的に導出できる**ことで示す。
public class TradeExpenseLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 8, 28);

    private static TradeExpense Line(
        string symbol,
        Market market,
        TradeExpenseCategory category,
        decimal amountUsd,
        string sourceId = "SRC-1") =>
        new(symbol, market, category, amountUsd, Day, sourceId, Now);

    // ── 建玉への紐づけ ───────────────────────────────────────────────────────

    [Fact]
    public void 同一の銘柄と市場の明細は_1_つの建玉へまとまる()
    {
        IReadOnlyList<TradeExpense> lines =
        [
            Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Commission, 1.00m, "ORD-1"),
            Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Commission, 2.00m, "ORD-2"),
            Line("AAPL", Market.UnitedStates, TradeExpenseCategory.BorrowFee, 0.50m, "ACC-1"),
        ];

        var summaries = TradeExpenseLedger.SummarizeByPosition(lines);

        var position = summaries.Should().ContainSingle().Subject;
        position.Symbol.Should().Be("AAPL");
        position.Market.Should().Be(Market.UnitedStates);
        position.For(TradeExpenseCategory.Commission).AmountUsd.Should().Be(3.00m);
        position.For(TradeExpenseCategory.Commission).LineCount.Should().Be(2);
        position.For(TradeExpenseCategory.BorrowFee).AmountUsd.Should().Be(0.50m);
    }

    // 建玉の一次識別子は (銘柄, 市場) の組である（ADR-0027 決定2）。
    // **銘柄だけで畳むと、同じコードの別市場の費用が混ざる。**
    [Fact]
    public void 銘柄が同じでも市場が違えば別の建玉になる()
    {
        IReadOnlyList<TradeExpense> lines =
        [
            Line("0001", Market.UnitedStates, TradeExpenseCategory.Fee, 1.00m),
            Line("0001", Market.Japan, TradeExpenseCategory.Fee, 5.00m),
        ];

        var summaries = TradeExpenseLedger.SummarizeByPosition(lines);

        summaries.Should().HaveCount(2);
        summaries.Single(s => s.Market == Market.UnitedStates).For(TradeExpenseCategory.Fee).AmountUsd.Should().Be(1.00m);
        summaries.Single(s => s.Market == Market.Japan).For(TradeExpenseCategory.Fee).AmountUsd.Should().Be(5.00m);
    }

    [Fact]
    public void 建玉_1_件の集計は他の建玉の明細を含まない()
    {
        IReadOnlyList<TradeExpense> lines =
        [
            Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Commission, 1.00m),
            Line("MSFT", Market.UnitedStates, TradeExpenseCategory.Commission, 9.00m),
        ];

        var summary = TradeExpenseLedger.SummarizePosition(lines, "AAPL", Market.UnitedStates);

        summary.For(TradeExpenseCategory.Commission).AmountUsd.Should().Be(1.00m);
        summary.For(TradeExpenseCategory.Commission).LineCount.Should().Be(1);
    }

    // ── プロパティベース ─────────────────────────────────────────────────────

    // 明細のある区分だけを返すと、呼び出し側が存在しないキーを引いて**黙って 0 を得る**経路ができる。
    [Fact]
    public void 集計は常に_7_区分ぶんを返す()
    {
        var summary = TradeExpenseLedger.SummarizePosition(
            [Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Commission, 1.00m)],
            "AAPL",
            Market.UnitedStates);

        summary.Totals.Select(t => t.Category).Should().Equal(TradeExpenseClassification.All);
    }

    // 🔴 「0 円だった」と「1 件も計上されていない」の区別。借株料で同じ誤読を塞いだのと同じ構造である。
    [Fact]
    public void 明細が無い区分は金額_0_かつ件数_0_で未計上と読める()
    {
        var summary = TradeExpenseLedger.SummarizePosition(
            [Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Commission, 1.00m)],
            "AAPL",
            Market.UnitedStates);

        var borrowFee = summary.For(TradeExpenseCategory.BorrowFee);
        borrowFee.AmountUsd.Should().Be(0m);
        borrowFee.LineCount.Should().Be(0);
        borrowFee.HasLines.Should().BeFalse();

        // 金額 0 でも「実際に 0 円が計上された」なら件数は 1 になる（未計上と区別できる）。
        var zeroCharged = TradeExpenseLedger.SummarizePosition(
            [Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Fee, 0m)],
            "AAPL",
            Market.UnitedStates);
        zeroCharged.For(TradeExpenseCategory.Fee).AmountUsd.Should().Be(0m);
        zeroCharged.For(TradeExpenseCategory.Fee).HasLines.Should().BeTrue();
    }

    [Fact]
    public void 明細が_1_件も無くても_7_区分ぶんを未計上として返す()
    {
        var summary = TradeExpenseLedger.SummarizePosition([], "AAPL", Market.UnitedStates);

        summary.Totals.Should().HaveCount(7);
        summary.Totals.Should().AllSatisfy(t =>
        {
            t.LineCount.Should().Be(0);
            t.AmountUsd.Should().Be(0m);
        });
        summary.TotalExpensesUsd.Should().Be(0m);
    }

    [Fact]
    public void 集計は明細の並び順に依存しない()
    {
        var lines = new List<TradeExpense>
        {
            Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Commission, 1.00m, "A"),
            Line("AAPL", Market.UnitedStates, TradeExpenseCategory.FxCost, 0.25m, "B"),
            Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Realized, -3.00m, "C"),
        };

        var forward = TradeExpenseLedger.SummarizePosition(lines, "AAPL", Market.UnitedStates);
        lines.Reverse();
        var reversed = TradeExpenseLedger.SummarizePosition(lines, "AAPL", Market.UnitedStates);

        reversed.Should().BeEquivalentTo(forward);
    }

    [Fact]
    public void 建玉の並び順は銘柄と市場で決定的である()
    {
        IReadOnlyList<TradeExpense> lines =
        [
            Line("MSFT", Market.UnitedStates, TradeExpenseCategory.Fee, 1m),
            Line("AAPL", Market.Japan, TradeExpenseCategory.Fee, 1m),
            Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Fee, 1m),
        ];

        var summaries = TradeExpenseLedger.SummarizeByPosition(lines);

        summaries.Select(s => (s.Symbol, s.Market)).Should().Equal(
            ("AAPL", Market.Japan), ("AAPL", Market.UnitedStates), ("MSFT", Market.UnitedStates));
    }

    // 実現損益は符号付きで持つ（損失は負）。費用側は正で費用額を持つ。
    [Fact]
    public void 実現損益は符号付きで集計される()
    {
        var summary = TradeExpenseLedger.SummarizePosition(
            [
                Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Realized, 10.00m, "WIN"),
                Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Realized, -4.00m, "LOSS"),
            ],
            "AAPL",
            Market.UnitedStates);

        summary.RealizedUsd.Should().Be(6.00m);
    }

    // ── 否定形 ───────────────────────────────────────────────────────────────

    // 🔴 ADR-0016 決定15 の要点そのもの。配当相当額の支払いが実現損益へ流れ込むと、
    // **後から譲渡費用として区別できない**。
    [Fact]
    public void 否定形_配当相当額は実現損益の合計を動かさない()
    {
        var withoutDividend = TradeExpenseLedger.SummarizePosition(
            [Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Realized, 10.00m, "R")],
            "AAPL",
            Market.UnitedStates);

        var withDividend = TradeExpenseLedger.SummarizePosition(
            [
                Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Realized, 10.00m, "R"),
                Line("AAPL", Market.UnitedStates, TradeExpenseCategory.DividendInLieu, 2.50m, "DIV"),
            ],
            "AAPL",
            Market.UnitedStates);

        withDividend.RealizedUsd.Should().Be(withoutDividend.RealizedUsd);
        withDividend.RealizedUsd.Should().Be(10.00m);

        // 消えたのではなく、費用側へ計上されている。
        withDividend.For(TradeExpenseCategory.DividendInLieu).AmountUsd.Should().Be(2.50m);
        withDividend.TotalExpensesUsd.Should().Be(2.50m);
    }

    // 🔴 費用合計に実現損益が混ざると、費用が損益で相殺されて見え、実費が過小に出る。
    [Fact]
    public void 否定形_費用合計に実現損益は混ざらない()
    {
        var summary = TradeExpenseLedger.SummarizePosition(
            [
                Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Realized, 100.00m, "R"),
                Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Commission, 1.00m, "C"),
                Line("AAPL", Market.UnitedStates, TradeExpenseCategory.BorrowFee, 0.50m, "B"),
                Line("AAPL", Market.UnitedStates, TradeExpenseCategory.MarginInterest, 0.25m, "M"),
                Line("AAPL", Market.UnitedStates, TradeExpenseCategory.DividendInLieu, 2.00m, "D"),
                Line("AAPL", Market.UnitedStates, TradeExpenseCategory.Fee, 0.10m, "F"),
                Line("AAPL", Market.UnitedStates, TradeExpenseCategory.FxCost, 0.15m, "X"),
            ],
            "AAPL",
            Market.UnitedStates);

        // 6 区分の費用だけの合計。100.00 の実現損益は 1 円も入らない。
        summary.TotalExpensesUsd.Should().Be(4.00m);
        summary.RealizedUsd.Should().Be(100.00m);
    }

    // 集計に 7 区分が常にそろう以上、`For` が見つからないのは構造の破損である（黙って 0 を返さない）。
    [Fact]
    public void 否定形_未知の区分を引くと例外になる()
    {
        var summary = TradeExpenseLedger.SummarizePosition([], "AAPL", Market.UnitedStates);

        var act = () => summary.For((TradeExpenseCategory)999);

        act.Should().Throw<InvalidOperationException>();
    }

    // 経費台帳の 1 行は**イベントとして**監査台帳（7 年保持）へ入る。
    // 集計が読むのはそのイベントが運ぶ明細であり、**イベントが明細を丸ごと運ぶこと**が
    // 「後から集計可能な粒度」の前提である（IADR-0226 決定4）。
    [Fact]
    public void 経費イベントは明細を丸ごと運ぶ()
    {
        var line = Line("AAPL", Market.UnitedStates, TradeExpenseCategory.DividendInLieu, 2.50m, "DIV-1");

        var recorded = new TradeExpenseRecorded(line);

        recorded.Expense.Should().Be(line);
        TradeExpenseLedger.SummarizePosition([recorded.Expense], "AAPL", Market.UnitedStates)
            .For(TradeExpenseCategory.DividendInLieu).AmountUsd.Should().Be(2.50m);
    }
}
