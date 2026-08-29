using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace RiskManagementService.Infrastructure.Steps;

// FR-01, FR-02, FR-10, ADR-0020 決定2/決定3, #337, IADR-0249: 情報収集の縮退遷移を購読し、
// 新規建て停止の状態（IInformationDegradationStore）へ畳む。
//
// 🔴 **本ハンドラは判定をしない。** 止めるかどうかは RiskEvaluator（isEntry × snapshot）が決める。
// ここはカテゴリ集合の出し入れだけを行う——**BlocksNewEntries=false の縮退（記録・通知のみ／
// 空売り限定）は登録しない**。イベント自身が「新規建てを止めるか」を宣言しており（#336・
// InformationSourceDegraded.BlocksNewEntries）、受け手が Behavior 文字列を再解釈して広げない。
//
// ADR-0013, IADR-0129 決定10: 再配送で同じ遷移が二度届いても集合の Add/Remove は冪等。
public sealed class InformationSourceDegradedRiskHandler(
    IInformationDegradationStore store,
    ILogger<InformationSourceDegradedRiskHandler> logger)
{
    public void Handle(InformationSourceDegraded message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!message.BlocksNewEntries)
        {
            // 記録・通知のみ／空売り限定の縮退は新規建て停止の対象ではない（登録しない）。
            logger.LogInformation(
                "情報源の縮退（新規建て停止なし）: category={Category} behavior={Behavior}",
                message.Category, message.Behavior);
            return;
        }

        store.MarkDegraded(message.Category);
        logger.LogWarning(
            "情報源の縮退により新規建てを停止する: category={Category} behavior={Behavior} missing={Missing}。"
                + "手仕舞い・損切りは止めない（ADR-0020）。",
            message.Category, message.Behavior, string.Join(",", message.MissingSources));
    }
}

// #337, IADR-0249: 回復の遷移。該当カテゴリを外し、残が無ければ新規建て停止が解ける。
public sealed class InformationSourceRecoveredRiskHandler(
    IInformationDegradationStore store,
    ILogger<InformationSourceRecoveredRiskHandler> logger)
{
    public void Handle(InformationSourceRecovered message)
    {
        ArgumentNullException.ThrowIfNull(message);

        store.MarkRecovered(message.Category);
        logger.LogInformation(
            "情報源の縮退から回復: category={Category} 継続サイクル={AffectedCycles} 残る新規建て停止={Remaining}",
            message.Category, message.AffectedCycles, store.BlocksNewEntries);
    }
}
