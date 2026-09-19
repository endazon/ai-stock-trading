using RiskManagementService.Common.Abstractions;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.CancelPositionClose;

// FR-05, FR-10, FR-11, UC-06, ADR-0003, #847, #768, IADR-0357:
// 利用者（owner）による「板に残った手仕舞いの取消」。
//
// 稼働環境（2026-09-18 23:13 JST）では、板に残った手仕舞い（3,381 株・指値 334.09）を消す手段が
// **moomoo アプリだけ**だった。発注執行に取消の口が無く、自動の取消はシステム自身の判断でしか動かないためである。
// **アプリ操作に逃がさない**ことが #847 の受け入れ基準であり、本サービスがその入口である。
//
// 手仕舞い（PositionCloseService）と同じ形に揃える —— 同じ層・同じ権限（OwnerOnly）・理由必須・
// 判定は台帳だけを見る・発行は呼び出し側。発注前スクリーニング（RiskEvaluator）は通さない
//（手仕舞いを止めない規律に、その取消も従う。統制ストアを依存に持たないことが構造的な保証である）。
//
// 🔴 本サービスは「取り消したい」という要求を作るだけであり、**在庫の押さえには一切触れない**。
// 押さえが解けるのは、発注執行が**確実に取り消せたと確認できた**ときに出す OrderCancelled か、
// 約定追跡がブローカーから引き直す OrderExecuted(Cancelled) だけである（IADR-0117 改定 1/4）。
public sealed class PositionCloseCancellationService(IPortfolioLedgerStore ledger, IClock clock)
{
    public PositionCloseCancellationOutcome Request(PositionCloseCancellationCommand command, string actor)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        // 未知の DecisionId は取り消せない（台帳の語彙に無い注文へ取消を送らない）。
        if (ledger.FindApprovedIntent(command.DecisionId) is not { } intent)
            return PositionCloseCancellationOutcome.Reject(PositionCloseCancellationRejection.OrderNotFound);

        if (intent.PositionEffect != PositionEffect.Close)
            return PositionCloseCancellationOutcome.Reject(PositionCloseCancellationRejection.NotACloseOrder);

        // 🔴 「既に終端かどうか」はここでは判定しない。台帳の終端は**確認できたものだけ**が立っており
        // （IADR-0117 改定 1/3）、立っていない＝生きているとは限らない。生死の権威はブローカーであり、
        // 終端済みの注文への取消はブローカーが拒否する（発注執行がその失敗を記録・ログする）。
        // ここで先回りして弾くと、台帳へ終端が届いていないだけの注文を「取り消せない」ことにしてしまう
        // ——それは #847 が直そうとしている「手仕舞えない」の別の形である。
        return new PositionCloseCancellationOutcome(
            PositionCloseCancellationRejection.None,
            new PositionCloseCancellationRequested(
                command.DecisionId, intent.Symbol, intent.Market, actor, command.Reason, clock.UtcNow));
    }
}
