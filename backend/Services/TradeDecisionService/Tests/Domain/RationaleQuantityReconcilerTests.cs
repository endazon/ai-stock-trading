using TradeDecisionService.Domain;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-10, FR-11, ADR-0040 決定5, #822, IADR-0343: 根拠文の株数言及とシステムが決めた数量の突合（純関数）。
public class RationaleQuantityReconcilerTests
{
    // 2026-09-16〜17 の実測（#822）: 根拠文は「1株単位の新規買い」、サイジングは 849 株。
    private const string Observed = "押し目で反発の兆しがあり、リスク制約内で1株単位の新規買いが可能と判断";

    [Fact]
    public void 実測の根拠文とサイジング数量が食い違えば原文を保って注記を追記する()
    {
        var result = RationaleQuantityReconciler.Reconcile(Observed, quantity: 849);

        result.Mismatched.Should().BeTrue();
        result.Rationale.Should().StartWith(Observed, "LLM の文言は書き換えない（追記のみ）");
        result.Rationale.Should().Contain(RationaleQuantityReconciler.NotePrefix);
        result.Rationale.Should().Contain("849 株");
        result.Rationale.Should().Contain("1株", "どの言及が食い違ったかを残す");
    }

    [Theory]
    [InlineData("押し目", 849)]
    [InlineData("849株の買いが妥当", 849)]
    [InlineData("849 株を買う", 849)]
    [InlineData("1,000株を買う", 1000)]
    [InlineData("buy 20 shares", 20)]
    [InlineData("１株あたりの損切り幅は 30 ドル", 849)]
    [InlineData("1株当たり利益の上方修正", 849)]
    [InlineData("株価は 30 per share の値幅で推移", 849)]
    [InlineData("第一株主の買い増し", 849)]
    [InlineData("", 849)]
    public void 言及なし一致単価表現は原文のまま(string rationale, int quantity)
    {
        var result = RationaleQuantityReconciler.Reconcile(rationale, quantity);

        result.Mismatched.Should().BeFalse();
        result.Rationale.Should().Be(rationale);
    }

    [Theory]
    [InlineData("1株の買い", 1)]
    [InlineData("１株の買い", 1)]
    [InlineData("一株だけ試し買い", 1)]
    [InlineData("100 株を買う", 100)]
    [InlineData("１，０００株", 1000)]
    [InlineData("buy 1 share", 1)]
    [InlineData("buy one share", 1)]
    [InlineData("Buy 1,500 Shares now", 1500)]
    public void 株数の言及を検出する(string rationale, int expected)
    {
        RationaleQuantityReconciler.FindShareCounts(rationale)
            .Select(m => m.Count)
            .Should().Equal(expected);
    }

    [Fact]
    public void 言及が複数あり一つでも食い違えば注記する()
    {
        var result = RationaleQuantityReconciler.Reconcile("20株を検討したが最終的に1株", quantity: 20);

        result.Mismatched.Should().BeTrue();
    }

    [Fact]
    public void 注記は決定的で二重に付かない()
    {
        var once = RationaleQuantityReconciler.Reconcile(Observed, 849).Rationale;
        var again = RationaleQuantityReconciler.Reconcile(Observed, 849).Rationale;
        var twice = RationaleQuantityReconciler.Reconcile(once, 849);

        again.Should().Be(once);
        twice.Rationale.Should().Be(once);
    }

    [Fact]
    public void nullの根拠文は空文字として扱う()
    {
        var result = RationaleQuantityReconciler.Reconcile(null, 10);

        result.Mismatched.Should().BeFalse();
        result.Rationale.Should().BeEmpty();
    }
}
