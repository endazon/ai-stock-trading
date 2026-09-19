using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 T-10-580, FR-09, FR-10, UC-06, #847, IADR-0357:
// **`MarkTerminal` の「初回だけ true」は、並行しても成立しなければ意味が無い。**
//
// 本 PR は戻り値を失効通知の冪等キーにした。その戻り値が並行で 2 回 true になると、利用者へ**同じ失効が
// 二度通知される**。発火条件は本 PR が作っている —— 取消の確認（`OrderCancelled`）と約定追跡の再観測
// （`OrderExecuted`）は Wolverine の**別キュー＝並行実行**であり（IADR-0129 決定 1「1 サービス内 1 イベント型
// = 1 キュー」）、同じ承認の終端を同時に運ぶのはまさに #847 のシナリオである。
//
// 🔴 **本ファイルが EF 実装側に在ることが要点である。** 冪等性の主張を `InMemoryPortfolioLedgerStore`
// （`ConcurrentDictionary.TryUpdate` の CAS）だけで測っていたため、**本番ストア（EF）に並行トークンが無い**
// ことが検出されていなかった（実測: トークン無しの EF は 200 試行中 63 試行で true が 2 回成立した）。
// 実装の差は**実装ごとに測って初めて見える**。
public class EfPortfolioLedgerMarkTerminalConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);

    private static DbContextOptions<RiskManagementDbContext> Options(string dbName) =>
        new DbContextOptionsBuilder<RiskManagementDbContext>().UseInMemoryDatabase(dbName).Options;

    private static Guid Approve(DbContextOptions<RiskManagementDbContext> options)
    {
        using var db = new RiskManagementDbContext(options);
        var decisionId = Guid.NewGuid();
        new EfPortfolioLedgerStore(db).AppendApproval(
            decisionId,
            new OrderIntent("SOXL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 3381, 334.09m, PositionEffect.Close),
            Now.AddMinutes(-5));
        return decisionId;
    }

    // 🔴 本 PR が守る不変条件そのもの: **並行して同じ終端を運んでも true はちょうど 1 回**。
    // 本番配線と同じく、競合する 2 経路はそれぞれ**別の DbContext**（Wolverine のメッセージごとのスコープ）を持つ。
    [Fact]
    public async Task 並行して終端が届いても初回を主張するのは一度だけ()
    {
        // 1 試行では偶然通ってしまう（トークン無しでも 200 試行中 137 試行は通った）。繰り返して確率的に暴く。
        const int trials = 200;
        var doubleClaims = 0;

        for (var i = 0; i < trials; i++)
        {
            var options = Options(Guid.NewGuid().ToString());
            var decisionId = Approve(options);

            using var barrier = new Barrier(2);

            async Task<bool> MarkAsync(OrderStatus status)
            {
                await Task.Yield();
                using var db = new RiskManagementDbContext(options);
                var store = new EfPortfolioLedgerStore(db);
                barrier.SignalAndWait();
                return store.MarkTerminal(decisionId, status, Now);
            }

            // 実運用で競合する 2 経路: 明示的な取消（OrderCancelled）と約定追跡の再観測（OrderExecuted）。
            var results = await Task.WhenAll(MarkAsync(OrderStatus.Cancelled), MarkAsync(OrderStatus.Expired));

            if (results.Count(r => r) != 1)
                doubleClaims++;
        }

        doubleClaims.Should().Be(
            0,
            "並行で 2 回 true になると同じ失効が二度通知される（{0}/{1} 試行で破れた）。"
                + "TerminalAt の並行トークンが外れていないか確認すること",
            doubleClaims,
            trials);
    }

    // 対（肯定形）: 並行しても**終端そのものは必ず記録される**（負けた側が消してしまわない）。
    [Fact]
    public async Task 並行しても終端は必ず記録され在庫は解放される()
    {
        var options = Options(Guid.NewGuid().ToString());
        var decisionId = Approve(options);

        async Task MarkAsync(OrderStatus status)
        {
            await Task.Yield();
            using var db = new RiskManagementDbContext(options);
            new EfPortfolioLedgerStore(db).MarkTerminal(decisionId, status, Now);
        }

        await Task.WhenAll(MarkAsync(OrderStatus.Cancelled), MarkAsync(OrderStatus.Expired));

        using var verify = new RiskManagementDbContext(options);
        new EfPortfolioLedgerStore(verify)
            .GetInFlightCloseQuantity("SOXL", Market.UnitedStates, Now.AddMinutes(-30))
            .Should().Be(0, "どちらが勝っても在庫の押さえは解ける（終端の意味は同じ）");
    }

    // 否定形: 負けた側の DbContext が汚れたまま残らない（以降の読み取りが保存済みの値を返す）。
    [Fact]
    public void 競合で負けた文脈でもその後の読み取りは保存済みの値を返す()
    {
        var options = Options(Guid.NewGuid().ToString());
        var decisionId = Approve(options);

        using var winner = new RiskManagementDbContext(options);
        using var loser = new RiskManagementDbContext(options);
        var loserStore = new EfPortfolioLedgerStore(loser);

        // 負ける側に先に行を読み込ませてから（TerminalAt = null を掴む）、勝者が終端を書く。
        loserStore.FindApprovedIntent(decisionId).Should().NotBeNull();
        new EfPortfolioLedgerStore(winner).MarkTerminal(decisionId, OrderStatus.Cancelled, Now).Should().BeTrue();

        loserStore.MarkTerminal(decisionId, OrderStatus.Expired, Now).Should().BeFalse();

        // 汚れた追跡状態を残していれば、ここで「処理中 3381」（＝自分が書きかけた値の裏返し）が見える。
        loserStore.GetInFlightCloseQuantity("SOXL", Market.UnitedStates, Now.AddMinutes(-30))
            .Should().Be(0);
    }
}
