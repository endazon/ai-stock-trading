namespace RiskManagementService.Application.Ports;

// FR-20, FR-09, ADR-0008, IADR-0085, #189: 撤退の非停止（Stage 1 ペーパー乖離）降格提案について、最後に通知した
// 「撤退提案シグネチャ」を永続化する重複排除ポート。停止経路（Stage 2/3・#166/IADR-0083）は kill switch 状態（DB 永続）を
// 冪等鍵にするが、非停止経路は kill switch を起動しないため別の durable な「通知済み」状態が要る。
// 巡回ごとの通知スパムを避け、プロセス再起動をまたいでも重複通知しないため、単一行で DB 永続する（in-memory 不可）。
public interface IWithdrawalNotificationStore
{
    /// <summary>最後に通知した撤退提案のシグネチャを返す。未通知（未記録）なら null。</summary>
    string? GetNotifiedSignature();

    /// <summary>撤退提案シグネチャを「通知済み」として保存する（単一行 upsert）。</summary>
    void SaveNotifiedSignature(string signature);

    /// <summary>通知済みシグネチャをクリアする（乖離が解消したとき。再乖離で再通知できるようにする）。</summary>
    void ClearNotifiedSignature();
}
