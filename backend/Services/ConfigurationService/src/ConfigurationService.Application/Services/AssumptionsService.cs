using AiStockTrading.Configuration.Application.Ports;
using AiStockTrading.Configuration.Application.State;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Kernel.Trading;

namespace AiStockTrading.Configuration.Application.Services;

// FR-17, UC-06: 全体前提条件の照会・変更。変更は利用者のみ（アクター・理由必須）、Version 増分、前後値つき履歴記録。
// 生成AI・自動処理は本サービスを呼ばない（呼び出し側の権限はホスト層の Keycloak 認可で担保）。変更通知イベントの発行は
// ホスト（エンドポイント）が担う（Application を MassTransit から独立させる）。
public sealed class AssumptionsService(
    IAssumptionsStore store,
    IAssumptionsChangeLog changeLog,
    IClock clock)
{
    public VersionedAssumptions GetCurrent() => store.GetCurrent();

    public IReadOnlyList<AssumptionsChangeEntry> GetHistory() => changeLog.GetHistory();

    /// <summary>前提条件を更新し、確定後の Version を返す。expectedVersion 不一致は競合として弾かれる（楽観排他）。</summary>
    public int Update(TradingAssumptions assumptions, int expectedVersion, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(assumptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var before = store.GetCurrent().Assumptions;
        var newVersion = store.Save(assumptions, expectedVersion);

        changeLog.Record(new AssumptionsChangeEntry(
            actor, reason, clock.UtcNow, newVersion,
            Before: before.ToString(), After: assumptions.ToString()));

        return newVersion;
    }
}
