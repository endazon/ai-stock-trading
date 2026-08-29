using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153: 口座種別の観測（最新 1 件）のインメモリ保持。
//
// **永続化しない。** 口座種別は「いまブローカーへ照会して得られる値」であり、プロセスをまたいで
// 引き継ぐべき事実ではない。再起動で観測が消えれば新規建ては止まり（フェイルクローズ）、
// 次の probe で復帰する。**永続化すると、ブローカーが応答しなくなった後も古い種別で回り続ける**。
public sealed class InMemoryBrokerAccountObservationStore(TimeProvider timeProvider)
    : IBrokerAccountObservationStore
{
    /// <summary>
    /// 観測の有効期間（30 分）。<b>ADR-0021 は照会の頻度・キャッシュを定めていない</b>ため実装の裁量である
    /// （IADR-0153 決定3）。発注執行側の probe が許す最大間隔（<c>BrokerAvailabilityProbeOptions</c> の
    /// クランプ上限 1800 秒）と同値にしてあり、健全な probe は必ずこの窓の内に更新できる。
    /// 逆に probe が止まれば観測は失効し、新規建ては止まる。
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    private readonly Lock _gate = new();
    private BrokerAccountState? _account;
    private DateTimeOffset _observedAt;

    public void Record(BrokerAccountState account, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(account);

        lock (_gate)
        {
            // 逆行する観測（再配送・順序の入れ替わり）は無視する。古い種別で新しい観測を上書きしない。
            if (_account is not null && observedAt <= _observedAt)
            {
                return;
            }

            _account = account;
            _observedAt = observedAt;
        }
    }

    public BrokerAccountState? GetCurrent()
    {
        lock (_gate)
        {
            if (_account is null)
            {
                return null;
            }

            // 失効した観測は「無い」と同じに扱う（フェイルクローズ）。
            return timeProvider.GetUtcNow() - _observedAt > MaxAge ? null : _account;
        }
    }
}
