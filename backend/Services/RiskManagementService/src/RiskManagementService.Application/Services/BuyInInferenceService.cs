using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159:
// ブローカ建玉の観測 1 回ぶんについて、強制買戻しの事後推定を実行し、30 日の空売り禁止を登録する。
//
// 判定そのものは純関数 <see cref="BuyInInference"/> が持ち、本サービスは「入力の収集 → 判定 → 追記 → 発行するイベント」の
// 束ねに徹する（判定の単一情報源をドメイン側に置く。PositionDriftTracker と同じ規律）。
public sealed class BuyInInferenceService(
    IPortfolioLedgerStore ledger,
    IBuyInInferenceStore store,
    IRiskSettingsStore settingsStore,
    IClock clock,
    TimeSpan? inFlightCloseWindow = null)
{
    /// <summary>
    /// 「処理中の決済」とみなす承認の遡り窓。<c>PositionCloseService.DefaultInFlightWindow</c> と同じ 30 分である。
    /// <para>
    /// **向きの確認**: 窓を広げるほど「自らの決済指示」で説明できる量が増える＝**推定が減る（過少側）**。
    /// 狭めるほど推定が増える（過剰側）。ADR-0016 決定4 の目的からは過剰側が安全だが、**約定が台帳へ届くまでの
    /// 通常運行の遅れ**（数秒〜数分）を覆う程度には広く取る必要がある——覆わないと、正常な手仕舞いのたびに
    /// 強制買戻しと推定してしまい、「自らの決済指示に対応する消失を誤認しない」という決定4 の要求に反する。
    /// 永久に約定しない滞留承認が推定を恒久的に塞がないよう、窓で切る。構成キーは持たせない
    /// （運用で触る値ではない。PositionCloseService と同じ判断）。
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultInFlightCloseWindow = TimeSpan.FromMinutes(30);

    private readonly TimeSpan _inFlightCloseWindow = inFlightCloseWindow ?? DefaultInFlightCloseWindow;

    /// <summary>
    /// ブローカ建玉の観測を突合し、推定した強制買戻しを発行対象として返す（記録は本メソッド内で追記する）。
    /// <para>
    /// **照会できなかった場合は本メソッドを呼ばないこと。** 発注執行サービスは照会不能のとき観測自体を発行しない
    /// （IADR-0118 の fail-safe）。空列を「全建玉が消失した」と読むと全銘柄が 30 日禁止になる（IADR-0159 決定4）。
    /// </para>
    /// </summary>
    public IReadOnlyList<BuyInInferred> Observe(
        IReadOnlyList<BrokerPositionSnapshot> brokerPositions, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(brokerPositions);

        var fills = ledger.GetFills();
        var ledgerPositions = PortfolioProjection.ProjectOpenPositions(fills);

        // 「処理中の決済」＝承認済みだが約定が台帳へ届いていない決済数量。**自らの決済指示そのもの**であり、
        // 未説明の消失から差し引く（IADR-0159 決定1）。
        var since = observedAt - _inFlightCloseWindow;
        var inFlight = ledgerPositions
            .Where(p => p.Side == TradeSide.Sell)
            .ToDictionary(
                p => (p.Symbol, p.Market),
                p => ledger.GetInFlightCloseQuantity(p.Symbol, p.Market, since));

        var outcomes = BuyInInference.Infer(
            ledgerPositions, brokerPositions, fills, inFlight, store.GetLatestPerSymbol());
        if (outcomes.Count == 0)
        {
            return [];
        }

        var limits = settingsStore.GetCurrent().ShortSell.Limits;
        var today = clock.Today;
        var inferredAt = clock.UtcNow;
        var events = new List<BuyInInferred>();

        foreach (var outcome in outcomes)
        {
            // 推定行のみ禁止期限を持つ。リセット行は null（**既存の禁止は戻さない**——30 日は経過でしか解けない）。
            var banUntil = outcome.NewlyInferredQuantity > 0 ? limits.BuyInBanUntil(today) : (DateOnly?)null;
            var id = Guid.NewGuid();

            store.Append(new BuyInInferenceRecord(
                id,
                outcome.Symbol,
                outcome.Market,
                outcome.LedgerShortQuantity,
                outcome.BrokerShortQuantity,
                outcome.InFlightCloseQuantity,
                outcome.UnexplainedQuantity,
                outcome.NewlyInferredQuantity,
                banUntil,
                today,
                observedAt,
                inferredAt));

            if (banUntil is not { } until)
            {
                // リセットは統制の発動ではないため通知・報告しない（監査の必要は追記済みの行が満たす）。
                continue;
            }

            events.Add(new BuyInInferred(
                id,
                outcome.Symbol,
                outcome.Market,
                outcome.LedgerShortQuantity,
                outcome.BrokerShortQuantity,
                outcome.InFlightCloseQuantity,
                outcome.UnexplainedQuantity,
                outcome.NewlyInferredQuantity,
                outcome.CoveringFills,
                until,
                observedAt,
                inferredAt));
        }

        return events;
    }
}
