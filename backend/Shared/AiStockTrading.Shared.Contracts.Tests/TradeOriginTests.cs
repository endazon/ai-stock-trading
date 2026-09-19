using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-11, ADR-0041 決定 1, #870, IADR-0360 決定 1: 取引記録の**由来**（誰が約定させたか）の軸を固定する。
//
// 🔴 **経費区分（TradeExpenseCategory・何の費用か）とは別の軸である。** 混ぜない。
// 本テストは 2 つの列挙が**別の語彙**であり続けることを、メンバ名の重なりが 1 つも無いことで留める。
public class TradeOriginTests
{
    // 🔴 序数は HTTP 経路で往来する。**既存メンバの間へ挿入しない**（過去の記録の意味が変わる）。
    private static readonly IReadOnlyDictionary<TradeOrigin, int> Ordinals = new Dictionary<TradeOrigin, int>
    {
        { TradeOrigin.System, 0 },
        { TradeOrigin.ManualAdoption, 1 },
    };

    [Fact]
    public void 由来の序数を表で固定する()
    {
        foreach (var (origin, ordinal) in Ordinals)
            ((int)origin).Should().Be(ordinal, $"{origin} の序数は永続・wire を跨ぐため動かさない");
    }

    [Fact]
    public void 由来は宣言した_2_つで全部である()
    {
        // 計画の用語集「由来（取引記録の）」が名指しする値は「システムが約定させた取引」と「手動売買による取り込み」。
        Enum.GetValues<TradeOrigin>().Should().BeEquivalentTo(Ordinals.Keys);
    }

    [Fact]
    public void 既定はシステムである()
    {
        // 既定（0）が「手動売買」に倒れると、書き忘れた記録が「利用者が売った」ことになる。
        default(TradeOrigin).Should().Be(TradeOrigin.System);
    }

    [Fact]
    public void 由来と経費区分は別の軸であり_語彙が重ならない()
    {
        var origins = Enum.GetNames<TradeOrigin>();
        var expenseCategories = Enum.GetNames<TradeExpenseCategory>();

        // 🔴 **「手動売買」という経費区分を作らない。** 区分は「何の費用か」、由来は「誰が約定させたか」である。
        origins.Should().NotIntersectWith(expenseCategories);
        expenseCategories.Should().NotContain(nameof(TradeOrigin.ManualAdoption));
    }
}
