using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Ports;

// FR-05, FR-19, ADR-0002, IADR-0067, IADR-0357: 発注済み注文の**訂正**を行うポート（注文履歴テレメトリ #154）。
//
// <see cref="IBrokerAdapter"/> とは意図的に分けている。訂正を <see cref="IBrokerAdapter"/> に生やすと
// 全実装者（moomoo を含む）に口が生え、実 OpenD への TrdModifyOrder 配線がスコープ外である現段階では
// moomoo 側が実行時例外を投げる地雷になる。本ポートをペーパー（PaperBrokerAdapter）だけが実装することで、
// 実ブローカー選択時は**訂正**の口が型として存在しない状態を作り、実弾の誤訂正をコンパイル時に防ぐ。
// 実ブローカーの訂正配線は後続・実コンテナ E2E（#82 系）で扱う。
//
// 🔴 #847, IADR-0357: **取消は本ポートの守備範囲から外れた。** 取消は当初から
// <see cref="IBrokerAdapter.CancelOrderAsync"/> に在って moomoo も実装しており（本ポートの
// <see cref="CancelOrderAsync"/> はペーパー実装が両方を満たすだけの重複だった）、
// 利用者が板に残った手仕舞いを取り消す経路は実ブローカーでも要る（稼働環境で詰んだ＝#847）。
// 取消の呼び出し側（OrderAmendmentService）は <see cref="IBrokerAdapter"/> を使う。
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
