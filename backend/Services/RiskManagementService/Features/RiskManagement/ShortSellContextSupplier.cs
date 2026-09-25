using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, UC-06, ADR-0016 決定2(a)・決定3・決定9, #967, IADR-0425 決定5: 新規の売り建て（空売り）の審査に渡す空売り文脈
// （ShortSellOrderContext）を組む。**組めないときは null を返す**——判定コアは null を「照会経路が無い」と読み、
// BorrowUnavailable で拒否する（ADR-0016 決定3・IADR-0131 決定2）。
//
// 🔴 **偽の文脈を組まない**（IADR-0159 決定5）。次のどちらかが「分からない」なら null である:
//   1. 借株可否（IShortSellBorrowSource）。照会の失敗・予算切れ・未構成・SIMULATE 口座での失敗（いまの常態）。
//   2. エクスポージャ（ShortExposureProjection）。保有建玉の現在値が 1 件でも無い。
// エクスポージャ 0・借株可否 false で埋めて「評価したことにする」と、10% / 50% の判定が起きていない観測の上で走る。
//
// 組めたときに載る値:
//   - ShortPermit: 観測どおり（不許可なら判定コアが一次ゲートで BorrowUnavailable を立てる。他の規則も評価して監査へ載る）。
//   - BorrowRateAnnual: **常に null**。`ShortFeeRate` は単位が未確定であり写像しない（IADR-0158 決定3・ADR-0016 決定3 の
//     2026-08-07 確定＝案 A）。したがって**文脈が組めても BorrowUnavailable が立ち、空売りは今も通らない。**
//     変わるのは、判定コアが打ち切らずに 10% / 50%・維持率・株価下限・逆指値必須を評価し、その理由が監査に載ることである。
//   - DividendRecordDate: **null**。権利確定日の供給元は無い。型の意味は「判明していれば」であり、不明と「無し」を区別しない
//     ——🔴 料率の供給を始める前に、この不明を拒否へ倒す手当てが要る（IADR-0425 の残余リスク。いまは料率の null が先に拒否する）。
//   - MarginSnapshot: 既存の維持率の供給（既定は供給なし＝null）をそのまま渡す（空売り建玉があれば判定コアが拒否へ倒す）。
//   - BuyInBanUntil: 推定台帳の禁止期限（審査が既に読んだ値）。
public sealed class ShortSellContextSupplier(
    IShortSellBorrowSource borrowSource,
    IPortfolioLedgerStore ledger,
    IWorkingEntryOrderSource workingEntryOrders,
    ICurrentPriceSource currentPrices,
    IMaintenanceMarginSnapshotSource marginSnapshots,
    IClock clock,
    ILogger<ShortSellContextSupplier> logger)
{
    // 未終端の新規建ての走査の下限（LedgerPortfolioStateProvider・WorkingEntryOrdersService と同じ幅）。当日の判定は
    // ProjectWorkingEntries が市場の現地取引日で行うため、下限は走査量の上限にすぎない（IADR-0346 決定2）。
    private static readonly TimeSpan WorkingEntryLookback = TimeSpan.FromDays(2);

    /// <param name="intent">新規の売り建て（<see cref="ShortSellEvaluator.IsShortEntry"/>）。それ以外で呼ばない。</param>
    /// <param name="today">判定日（注文の市場の現地取引日）。</param>
    /// <param name="buyInBanUntil">推定台帳の禁止期限（無ければ null）。</param>
    public async Task<ShortSellOrderContext?> SupplyAsync(
        OrderIntent intent, DateOnly today, DateOnly? buyInBanUntil, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var borrow = await borrowSource.GetAsync(intent.Symbol, intent.Market, cancellationToken).ConfigureAwait(false);
        if (borrow.ShortPermit is not { } shortPermit)
        {
            logger.LogWarning(
                "借株可否が分からないため空売り文脈を組みません（照会できないなら空売りしない）: Symbol={Symbol} 理由={Reason}",
                intent.Symbol, borrow.UnknownReason);
            return null;
        }

        var now = clock.UtcNow;
        var fills = ledger.GetFills();
        var held = PortfolioProjection.ProjectOpenPositions(fills);
        var working = PortfolioProjection.ProjectWorkingEntries(
            fills, now, workingEntryOrders.GetWorkingEntryOrders(now - WorkingEntryLookback));
        var exposure = ShortExposureProjection.Project(
            intent.Symbol, intent.Market, held, currentPrices.GetCurrentPrices(held), working);
        if (exposure is null)
        {
            logger.LogWarning(
                "保有建玉の現在値が揃わずエクスポージャが分からないため空売り文脈を組みません: Symbol={Symbol} 保有建玉={Count}",
                intent.Symbol, held.Count);
            return null;
        }

        return new ShortSellOrderContext
        {
            Today = today,
            ShortPermit = shortPermit,
            BorrowRateAnnual = null,
            BuyInBanUntil = buyInBanUntil,
            DividendRecordDate = null,
            MarginSnapshot = marginSnapshots.GetCurrent(),
            SymbolShortExposure = exposure.SymbolShortExposure,
            TotalShortExposure = exposure.TotalShortExposure,
            TotalExposure = exposure.TotalExposure,
        };
    }
}
