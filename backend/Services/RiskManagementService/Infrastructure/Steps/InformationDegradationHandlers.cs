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

// #564, IADR-0267: 収集サービスの**現況観測**（毎巡回 1 件）。停止カテゴリの集合を全量で置き換え、
// **観測の鮮度を更新する**。
//
// 🔴 **本ハンドラが「再起動しても停止が復元される」の実現手段である。** 遷移は状態が変わったときにしか
// 来ないため、縮退が続く静かな区間に本サービスが再起動すると停止が届かなかった（#564 の fail-open）。
// 現況観測は遷移の有無にかかわらず届くため、**再起動から 1 巡回で必ず復元される**。
// 復元されるまでの間は、ストアが「未観測＝不明」として**止める側**に倒す。
public sealed class InformationSourceStateObservedRiskHandler(
    IInformationDegradationStore store,
    ILogger<InformationSourceStateObservedRiskHandler> logger)
{
    public void Handle(InformationSourceStateObserved message)
    {
        ArgumentNullException.ThrowIfNull(message);

        store.ApplyObservation(message.BlockingCategories, message.ValidFor, message.ObservedAt);
        logger.LogInformation(
            "情報収集の現況を観測: 新規建て停止カテゴリ={Categories} 有効期間={ValidFor} 観測時刻={ObservedAt}。"
                + "手仕舞い・損切りは止めない（ADR-0020）。",
            message.BlockingCategories.Count == 0 ? "（なし）" : string.Join(",", message.BlockingCategories),
            message.ValidFor,
            message.ObservedAt);
    }
}

// #337, IADR-0249: 回復の遷移。該当カテゴリを外し、残が無ければ新規建て停止が解ける
// （**ただし有効な現況観測がある場合に限る**。遷移は鮮度を与えない・#564）。
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
