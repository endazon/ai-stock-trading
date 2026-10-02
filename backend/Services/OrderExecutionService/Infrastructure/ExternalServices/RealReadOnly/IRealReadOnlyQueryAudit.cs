using AiStockTrading.Shared.Contracts.Events;
using Wolverine;
using Wolverine.Runtime;

namespace OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly;

// FR-10, FR-11, #1000, IADR-0482 決定4: 実弾口座のヘッダで照会したことを監査へ残す口。
// 🔴 **記録に失敗したら例外を投げる。** 照会の側はそれを照会の失敗として扱い、答えを使わない
// （「実弾のヘッダで照会したのに台帳に無い」答えで審査を進めない）。
public interface IRealReadOnlyQueryAudit
{
    Task RecordAsync(RealAccountReadOnlyQueried entry);
}

// 本番実装。BackgroundService と同じく singleton から発行するため、IWolverineRuntime から MessageBus を作る
// （IMessageBus は scoped で singleton へ注入できない。BrokerAvailabilityProbeService と同じ作法・IADR-0129）。
public sealed class WolverineRealReadOnlyQueryAudit(IWolverineRuntime runtime) : IRealReadOnlyQueryAudit
{
    public Task RecordAsync(RealAccountReadOnlyQueried entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new MessageBus(runtime).PublishAsync(entry).AsTask();
    }
}
