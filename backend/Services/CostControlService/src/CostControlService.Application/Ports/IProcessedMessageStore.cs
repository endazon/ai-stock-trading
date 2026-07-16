namespace AiStockTrading.CostControl.Application.Ports;

// NFR（費用）, IADR-0055 決定5: 消費済みメッセージ ID の重複排除ストア。
// 月次費用台帳（ICostLedger）とは別物で、MassTransit の at-least-once 再配信により LlmCostIncurred が
// 重複配信されても二重計上しないために用いる（費用は月次累計のため二重計上は統制判定を誤らせる）。
public interface IProcessedMessageStore
{
    /// <summary>未処理なら記録して true を返す。既に処理済みなら false（呼び出し側は no-op する）。</summary>
    bool TryMarkProcessed(Guid messageId, DateTimeOffset at);

    /// <summary>
    /// マークを取り消す。計上に失敗したときに呼び、再配信で再試行できるようにする
    /// （マークだけ残って計上が欠落するのを避ける）。
    /// </summary>
    void Unmark(Guid messageId);
}
