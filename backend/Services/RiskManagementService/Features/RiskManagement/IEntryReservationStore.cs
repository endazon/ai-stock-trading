using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// 🔴 FR-10, FR-20, ADR-0016 決定9, #1319, IADR-0529: 新規建ての審査の直列化と、承認した新規建ての**予約**。
//
// 定時サイクルの判断 N 件はリスク管理へ同時に届く（Wolverine はハンドラの成功後に発行をまとめて送る）。承認行
// （approved_orders）は OrderApproved が RabbitMQ を往復した後にしか書かれないため、審査が台帳だけを読むと
// N 件すべてが同じ古い snapshot で保有建玉数・日次発注累計・段階資金を通る（IADR-0346 の残余リスクの評価が誤りだった）。
//
// - 審査は新規建てに限り本ポートの <see cref="RunSerializedAsync{T}"/> の中で行う（読み取り → 判定 → 予約が 1 つの臨界区間）。
// - 承認した新規建ては、同じ臨界区間（EF ではトランザクション）の中で予約を書く。予約は**承認行とは別の行**である
//   ——承認行は IADR-0407 の「発行済み」の印であり、発行の前に書くと、発行に失敗したときに発注が永久に出なくなる。
// - 予約の算入は注文源（IWorkingEntryOrderSource）が行う。承認行が来たら承認行だけで数える（二重に数えない）。
public interface IEntryReservationStore
{
    /// <summary>
    /// 新規建ての審査を直列化して <paramref name="body"/> を実行する。<paramref name="body"/> が例外で終われば、
    /// その中で書いた予約（と同じ DB への書き込み）は残らない。
    /// </summary>
    Task<T> RunSerializedAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken);

    /// <summary>判断 ID の予約（無ければ null）。</summary>
    EntryReservation? Find(Guid decisionId);

    /// <summary>予約を書く。<see cref="RunSerializedAsync{T}"/> の中から呼ぶ。同じ判断 ID が既にあれば何もしない（冪等）。</summary>
    void Reserve(EntryReservation reservation);
}

/// <summary>
/// FR-10, #1319, IADR-0529: 審査が承認した新規建ての予約。注文源へ載せる値（承認行と同じ数量・価格・レート・承認時刻）と、
/// 発行に失敗して判断が再配送されたときに<b>同じ承認</b>を組み直すための値（承認数量・承認時刻・損切りの実行機構）を持つ。
/// </summary>
public sealed record EntryReservation(
    Guid DecisionId,
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal Price,
    decimal FxRateToBase,
    int ApprovedQuantity,
    DateTimeOffset ApprovedAt,
    StopLossExecutionMethod StopLossMethod)
{
    /// <summary>注文源へ載せる形（承認行の <see cref="WorkingEntryOrder"/> と同じ定義）。</summary>
    public WorkingEntryOrder ToWorkingEntryOrder() =>
        new(DecisionId, Symbol, Market, Side, Quantity, Price, ApprovedAt, FxRateToBase);
}
