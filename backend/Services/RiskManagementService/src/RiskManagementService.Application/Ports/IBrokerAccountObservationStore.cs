using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153: ブローカーへ照会して得た口座の状態（最新 1 件）の保持。
//
// **保持するのは最新の 1 件だけである**（ADR-0021 決定2「同時に有効な口座種別は 1 つ」）。履歴は持たない。
//
// **観測には有効期限がある。** 定期 probe が止まった状態で古い観測を使い続けると、利用者が口座を切り替えた後も
// 旧種別の統制が掛かり続ける（現金口座なのに信用口座の緩い統制で回る）。ADR-0021 は照会の頻度・キャッシュを
// 定めていないため、実装の裁量として**発注執行側の probe が許す最大間隔（30 分）**を採る
// （IADR-0153 決定3。BrokerAvailabilityProbeOptions のクランプ上限と同値であり、健全な probe は必ず更新できる）。
public interface IBrokerAccountObservationStore
{
    /// <summary>観測を記録する（最新で上書きする）。<b>逆行する観測（より古い時刻）は無視する。</b></summary>
    void Record(BrokerAccountState account, DateTimeOffset observedAt);

    /// <summary>
    /// 現在有効な観測を返す。
    /// <para>
    /// <b>観測が無い、または失効している場合は <c>null</c> を返す。</b> 呼び出し側（判定コア）は
    /// <c>null</c> を「口座種別を確認できていない」として新規建てを止める。
    /// </para>
    /// </summary>
    BrokerAccountState? GetCurrent();
}
