using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Ports;

// FR-05, FR-19, ADR-0002, IADR-0067: 発注済み注文の訂正・取消を行うポート（注文履歴テレメトリ #154）。
//
// <see cref="IBrokerAdapter"/> とは意図的に分けている。訂正・取消を <see cref="IBrokerAdapter"/> に生やすと
// 全実装者（moomoo を含む）に口が生え、実 OpenD への TrdModifyOrder 配線がスコープ外である現段階では
// moomoo 側が実行時例外を投げる地雷になる。本ポートをペーパー（PaperBrokerAdapter）だけが実装することで、
// 実ブローカー選択時は訂正・取消の口が型として存在しない状態を作り、実弾の誤訂正・誤取消をコンパイル時に防ぐ。
// 実ブローカーの訂正・取消配線は後続・実コンテナ E2E（#82 系）で扱う。
public interface IOrderAmendmentBroker
{
    /// <summary>
    /// 未約定の注文を訂正する（数量・価格）。終端状態・未知の ID・不正な訂正内容（数量/価格が 0 以下）は
    /// <see cref="InvalidOperationException"/>。訂正後の注文を返す。
    /// </summary>
    Task<BrokerOrder> ModifyOrderAsync(
        string orderId, int quantity, decimal price, CancellationToken cancellationToken = default);

    /// <summary>未約定の注文を取り消す。終端状態・未知の ID は <see cref="InvalidOperationException"/>。</summary>
    Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default);
}
