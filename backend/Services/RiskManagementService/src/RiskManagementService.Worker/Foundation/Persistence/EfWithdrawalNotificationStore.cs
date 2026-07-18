using AiStockTrading.RiskManagement.Application.Ports;

namespace AiStockTrading.RiskManagement.Worker.Foundation.Persistence;

// FR-20, FR-09, IADR-0085, #189: 撤退の非停止（ペーパー乖離）降格提案の通知重複排除ストアの EF 実装（単一行 upsert）。
// 最後に通知した撤退提案シグネチャを DB 永続し、プロセス再起動をまたいでも重複通知しない（durable 冪等）。
// 未記録／Signature=null は「未通知」＝fail-safe。DbContext は scoped のため本ストアも scoped。
internal sealed class EfWithdrawalNotificationStore(RiskManagementDbContext db) : IWithdrawalNotificationStore
{
    public string? GetNotifiedSignature() =>
        db.WithdrawalNotifications.Find(SingletonKeys.Id)?.Signature;

    public void SaveNotifiedSignature(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        Upsert(signature);
    }

    public void ClearNotifiedSignature()
    {
        var row = db.WithdrawalNotifications.Find(SingletonKeys.Id);
        // 未記録なら既に「未通知」＝書き込み不要（無駄な巡回書き込みを避ける）。
        if (row is null || row.Signature is null)
            return;

        row.Signature = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
    }

    private void Upsert(string? signature)
    {
        var row = db.WithdrawalNotifications.Find(SingletonKeys.Id);
        if (row is null)
        {
            db.WithdrawalNotifications.Add(new WithdrawalNotificationRow
            {
                Id = SingletonKeys.Id,
                Signature = signature,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.Signature = signature;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        db.SaveChanges();
    }
}
