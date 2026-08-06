using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Ports;

// FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153: ブローカーへ口座種別を照会するポート。
//
// ADR-0021 決定3 は「**ブローカー（moomoo）へ照会して得た口座種別を正とする**」と定める。設定値だけに委ねると
// 「現金口座なのに GFV 回避ガードが無効のまま回転させる → 90 日の口座制限」という**気づけない事故**が起こる
// （同 ADR 79 行）。照会と設定の二重化は冗長に見えるが、**食い違いの検知そのものが目的**である。
//
// 本ポートを実装しないアダプタ（内蔵 paper）では口座種別の観測が発行されない。**外部へ一度も発注しない擬似約定
// にはブローカー口座が存在しない**ためであり、判定側はその場合に口座種別を要求しない（IADR-0153 決定2）。
public interface IBrokerAccountSource
{
    /// <summary>
    /// いまの口座の状態を照会する。
    ///
    /// 契約（fail-safe の要）:
    ///   - 照会が成功し種別が判明した → その <see cref="BrokerAccountState"/>
    ///   - 到達不能・応答異常・**種別が不明**（<c>TrdAccType_Unknown</c> 等）→ <c>null</c>（**例外を投げない**）
    ///   - キャンセル（停止要求）は <see cref="OperationCanceledException"/> をそのまま伝播してよい
    ///
    /// <b>迷ったら null。</b> null は「口座種別が不明」＝新規建てを止める側であり、
    /// 誤って値を返すことだけが統制を無効化する。**とりわけ「不明なら信用口座」を返してはならない。**
    /// </summary>
    Task<BrokerAccountState?> GetAccountStateAsync(CancellationToken cancellationToken = default);
}
