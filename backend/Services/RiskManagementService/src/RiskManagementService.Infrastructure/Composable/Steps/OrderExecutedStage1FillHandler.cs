using RiskManagementService.Application.Ports;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace RiskManagementService.Infrastructure.Steps;

// FR-20, FR-12, #386, 06_daytrading-review §4.1 条件3 / §4.3, IADR-0149: 約定（OrderExecuted）を購読し、
// 段階ゲートの「最小取引件数 100 件」を数えるための観測を記録する。
//
// **本ハンドラが Stage 1 の合格証跡に何を積むかを決める。** 計画は「Stage 1 の合格判定は SIMULATE の約定のみで
// 集計し、内蔵 paper の約定を算入してはならない」と定める（FR-20）。算入の可否そのものは純関数
// （Stage1Aggregation.CountsAsTrade）が決め、本ハンドラは「何を観測として渡すか」だけを担う。
//
// 記録しない条件は 3 つあり、いずれも**算入しない側（fail-safe）**へ倒す:
//   1. 約定していない（FilledQuantity <= 0）— 受付・失注・約定 0 の取消は「取引」ではない
//   2. 承認台帳に相関が無い — 建玉効果が不明であり、不明を数えると paper の擬似約定が混入し得る
//   3. 発注先が算入対象でない・新規建てでない — 観測は残すが CountsTowardStage1=false として記録される
//
// ADR-0013, IADR-0129 決定10, #354: 本ハンドラは OrderExecutedLedgerHandler / OrderExecutedActivityHandler と
// 同一のハンドラチェーンで実行され、再試行では全部が再実行される。本ハンドラの書き込みは DecisionId で
// 先着優先の冪等であり、再実行で件数が増えない。
public sealed class OrderExecutedStage1FillHandler(
    IStage1FillObservationStore observations,
    IPortfolioLedgerStore ledger,
    ILogger<OrderExecutedStage1FillHandler> logger)
{
    public void Handle(OrderExecuted message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 約定していない結果（受付・失注・約定 0 の取消・拒否）は取引件数に数えない。
        // OrderExecutedLedgerHandler と同じ条件（IADR-0113: 全量約定ではなく「約定があること」）。
        if (message.FilledQuantity <= 0)
        {
            return;
        }

        // OrderExecuted は建玉効果を運ばない。承認台帳（DecisionId）から引く。
        // **引けなければ記録しない**——不明を「新規建て」と決め打つと、算入される側へ倒れる。
        var positionEffect = ledger.FindApprovedPositionEffect(message.DecisionId);
        if (positionEffect is null)
        {
            logger.LogWarning(
                "約定に相関する承認が台帳に無いため Stage 1 取引件数へ算入しません: DecisionId={DecisionId} OrderId={OrderId}",
                message.DecisionId, message.OrderId);
            return;
        }

        var observation = new Stage1FillObservation(
            message.DecisionId,
            EasternTradingDate.From(message.ExecutedAt),
            // **実際に発注したアダプタの発注先**（IADR-0149 決定1）。取引判断が運ぶ intent.Mode は
            // 段階が定める既定の発注先であり、現在の発注先ではない（IADR-0140 決定3）。
            message.Provider,
            positionEffect.Value);

        observations.Record(observation);

        logger.LogDebug(
            "Stage 1 約定観測を記録: DecisionId={DecisionId} 発注先={Provider} 効果={Effect} 算入={Counted}",
            message.DecisionId, message.Provider, positionEffect.Value, Stage1Aggregation.CountsAsTrade(observation));
    }
}
