using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.CancelPositionClose;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 FR-05, FR-10, FR-11, UC-06, #847, #768, IADR-0357: 板に残った手仕舞いを利用者が取り消す要求の判定。
//
// 稼働環境（2026-09-18 23:13 JST）では、板に残った手仕舞いを消す手段が **moomoo アプリだけ**だった
// （発注執行に取消のエンドポイントが無い。自動の取消はシステム自身の判断でしか動かない）。
// アプリ操作に逃がさないことが #847 の受け入れ基準である。
public class PositionCloseCancellationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);
    private const string Actor = "owner-1";

    private static PositionCloseCancellationService Create(IPortfolioLedgerStore ledger) =>
        new(ledger, new FakeClock(Now, new DateOnly(2026, 9, 19)));

    private static Guid AppendApproval(
        InMemoryPortfolioLedgerStore ledger, PositionEffect effect = PositionEffect.Close)
    {
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(
                "SOXL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
                3381, 334.09m, effect),
            Now.AddMinutes(-5));
        return decisionId;
    }

    [Fact]
    public void 未約定の手仕舞いの取消要求は受理され監査イベントを返す()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = AppendApproval(ledger);

        var outcome = Create(ledger).Request(
            new PositionCloseCancellationCommand(decisionId, "指値が置いていかれたので成行で出し直す"), Actor);

        outcome.Accepted.Should().BeTrue();
        outcome.Requested!.DecisionId.Should().Be(decisionId);
        // FR-11: 「誰が・なぜ」が監査へ残る。これが無いと手仕舞いの取消が台帳から辿れない。
        outcome.Requested.Actor.Should().Be(Actor);
        outcome.Requested.Reason.Should().Be("指値が置いていかれたので成行で出し直す");
        outcome.Requested.RequestedAt.Should().Be(Now);
        outcome.Requested.Symbol.Should().Be("SOXL");
    }

    [Fact]
    public void 台帳に無い判断IDは拒否する()
    {
        Create(new InMemoryPortfolioLedgerStore())
            .Request(new PositionCloseCancellationCommand(Guid.NewGuid(), "理由"), Actor)
            .Rejection.Should().Be(PositionCloseCancellationRejection.OrderNotFound);
    }

    // エントリー注文の取消はこの口の役目ではない（保護逆指値が張れないときの取消は発注執行が自分で行う）。
    [Fact]
    public void 手仕舞い以外の注文は拒否する()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = AppendApproval(ledger, PositionEffect.Open);

        Create(ledger).Request(new PositionCloseCancellationCommand(decisionId, "理由"), Actor)
            .Rejection.Should().Be(PositionCloseCancellationRejection.NotACloseOrder);
    }

    // 「手仕舞いを止めない」と同じ構造的保証: 取消も統制ストアを依存に持たない。
    [Fact]
    public void 取消サービスは統制ストアに依存しない()
    {
        var dependencies = typeof(PositionCloseCancellationService)
            .GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        dependencies.Should().NotContain(typeof(IKillSwitchStore));
        dependencies.Should().NotContain(typeof(ILockoutStore));
        dependencies.Should().NotContain(typeof(IPauseStore));
    }
}
