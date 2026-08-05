using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, FR-19, FR-20, UC-06, ADR-0003, ADR-0007, ADR-0008: リスク管理設定（ガード・上限・段階）の変更。利用者のみ（アクター・理由必須）。
// 変更は前後値つきで履歴に記録する（ガード設定は ADR-0007「変更は利用者のみ・変更履歴を記録」。上限は FR-10、段階は ADR-0008 が同じ規律を課す）。生成AI・自動処理は本サービスを
// 呼ばない（呼び出し側の権限はホスト層の Keycloak 認可で担保・Slice B）。
public sealed class RiskSettingsService(
    IRiskSettingsStore store,
    ISettingsChangeLog changeLog,
    IClock clock)
{
    public RiskManagementSettings GetCurrent() => store.GetCurrent();

    public void UpdateGuard(TradingGuardSettings guard, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(guard);
        RequireActorAndReason(actor, reason);

        var current = store.GetCurrent();
        Save(current with { Guard = guard }, current.Guard, guard, SettingsChangeType.Guard, actor, reason);
    }

    /// <summary>
    /// FR-10, SC-02, UC-06, #362, IADR-0151 決定2: リスク上限を変更する。
    /// <para>
    /// <b>値域は <see cref="RiskLimitBounds"/> が単独で決める</b>（画面とサーバで規則を二重に書かない）。
    /// 範囲外なら <see cref="ArgumentException"/> を投げ、<b>設定を一切変更せず履歴も残さない</b>——
    /// 拒否された要求を履歴に積むと、実際には起きていない変更が監査上の事実になる（IADR-0141 と同じ規律）。
    /// エンドポイントは事前に <see cref="RiskLimitBounds.Validate"/> を呼んで 400 の details を組み立てるが、
    /// ここでも検査するのは<b>呼び出し口が将来増えても不変条件を保つため</b>である。
    /// </para>
    /// </summary>
    public void UpdateLimits(RiskLimitSettings limits, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(limits);
        RequireActorAndReason(actor, reason);
        RiskLimitBounds.ThrowIfOutOfRange(limits);

        var current = store.GetCurrent();
        Save(current with { Limits = limits }, current.Limits, limits, SettingsChangeType.Limits, actor, reason);
    }

    public void UpdateStage(StageSettings stage, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(stage);
        RequireActorAndReason(actor, reason);

        var current = store.GetCurrent();
        // FR-20, #334, IADR-0140: 段階だけを差し替える。**発注先（BrokerProvider）には触れない**——
        // 2 軸は独立であり、段階の変更が発注先を自動で動かしてはならない（`with` が他プロパティを保つ）。
        Save(current with { Stage = stage }, current.Stage, stage, SettingsChangeType.Stage, actor, reason);
    }

    /// <summary>
    /// FR-20, FR-13, SC-02, #334, IADR-0140 / IADR-0141: 発注先（Broker Provider）を変更する。
    /// <para>
    /// 受理条件は <see cref="BrokerProviderChange.Evaluate"/> が単独で決める（画面とサーバで規則を二重に
    /// 書かない）。<b>受理しない場合は設定を一切変更せず、履歴も残さない</b>——拒否された要求を履歴に
    /// 積むと、実際には起きていない変更が監査上の事実になる。
    /// </para>
    /// <para>
    /// <b>段階には触れない。</b>発注先の変更が段階を自動で動かしてはならない（2 軸の独立）。
    /// </para>
    /// </summary>
    /// <returns>判定結果（受理可否・拒否理由の全件・実弾切替か・段階ゲートを飛ばすか）。</returns>
    public BrokerProviderChangeAssessment UpdateBrokerProvider(BrokerProviderChangeRequest request, string actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var current = store.GetCurrent();
        var assessment = BrokerProviderChange.Evaluate(request, current.Stage);
        if (!assessment.Accepted)
        {
            return assessment;
        }

        Save(
            current with { BrokerProvider = request.Target },
            current.BrokerProvider,
            request.Target,
            SettingsChangeType.BrokerProviderChanged,
            actor,
            request.Reason);
        return assessment;
    }

    private void Save(
        RiskManagementSettings updated,
        object before,
        object after,
        SettingsChangeType changeType,
        string actor,
        string reason)
    {
        var now = clock.UtcNow;
        store.Save(updated);
        changeLog.Record(new SettingsChangeEntry(
            actor, changeType, reason, now,
            Before: before.ToString(), After: after.ToString()));
    }

    private static void RequireActorAndReason(string actor, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    }
}
