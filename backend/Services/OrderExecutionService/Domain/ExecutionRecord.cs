using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Domain;

// FR-05, FR-16: 発注結果の記録（注文実体＋スリッページ）。月報レビュー（FR-16）・ポジション/損益射影のデータ源。
// DecisionId で取引判断/損切りと相関する。SlippageRatio は「不利＝正」（SlippageCalculator）。
// 🔴 FR-10, ADR-0049 決定1, #1122, IADR-0486 決定5・決定6: StopFloorSource は新規建ての承認の発注意図が運んだ「損切り幅に下限を掛けて
// ラインを引いた」印（OrderIntent.StopFloorSource。Fallback2Pct / Atr14）。null＝分からない（#1122 より前の記録・決済・保護レグ）。
// 既存の S1 への下限の遡及（SoftwareStopFloorRetrofitter）が、印のある行を広げないために読む。突合で確定した記録は予約の行が残した印を使う。
// 🔴 FR-10, UC-06, ADR-0050 決定1, #1222, IADR-0515 決定2: ApprovalOrigin は承認の経路の発注が承認から写した出どころ（判断・利用者の手仕舞い・
// 維持率割れの自動縮小）。null＝分からない（#1222 より前の記録・保護の機構の記録・突合で確定した記録・出どころを持たない旧い承認）。
// S1 の決済の前の取消（SoftwareStopExecutor）が、利用者・自動縮小の決済を取り消さず差し引くために読む。null は判断の手仕舞いと同じく取り消す側。
// 🔴 FR-10, #1330, IADR-0534 決定2: ProtectionReleasedQuantity は承認経路の決済（ApprovalOrigin あり）の約定のうち、保護記録の縮小へ
// **既に帰属させた**株数（0..FilledQuantity・単調）。同じ約定を 2 回「承認済みの決済で減った」の証拠に使わないための吸収の印である。
// 書くのは保護の突き合わせ（ProtectiveStopNetting）だけで、IExecutedOrderStore.MarkProtectionReleased の 1 列だけを書く。
// 🔴 FR-10, FR-19, ADR-0067 決定1・2, #1339, IADR-0539: UnfilledCancelRequestedAt は判断由来の決済の指値を「N 分未約定なので系が取り消す」と
// 決めて**取消を送る前に**書いた時刻。null＝系はこの注文の取消を求めていない。同じ注文を二度取り消さない印であり、
// 1 建玉 1 日の回数の上限の数え（市場の現地取引日）にも使う。書くのは DecisionCloseUnfilledCanceller だけ（MarkUnfilledCancelRequested）。
// 🔴 FR-10, FR-09, UC-06, #1395, IADR-0560: SoftwareStopCancelRequestedAt はソフトウェア逆指値（S1）が損切りの成行の前に
// この決済（同じ建玉を売る判断の手仕舞い）を取り消すと決めて**取消を送る前に**書いた時刻。null＝S1 はこの注文の取消を求めていない。
// 約定追跡が終端（Cancelled）を観測したとき OrderExecuted.CancelledBySoftwareStop へ写す（取消の主体を通知の判定へ届ける唯一の運び手）。
// 書くのは判断の手仕舞いの取消の段 DecisionCloseYield だけ（MarkSoftwareStopCancelRequested）。#1448, IADR-0577: 段は S1 と保護逆指値ガードの
// 成行手仕舞いが共通に使う（ガードの取消でも同じ印＝「保護の機構が成行に置き換えるために取り消した」。列の名前は S1 のまま）。
// UnfilledCancelRequestedAt とは別の列——そちらは回数の上限に数える。
public record ExecutionRecord(
    Guid DecisionId,
    string OrderId,
    string Symbol,
    Market Market,
    TradeSide Side,
    ProductType ProductType,
    PositionEffect PositionEffect,
    int Quantity,
    decimal PlannedPrice,
    int FilledQuantity,
    decimal AveragePrice,
    OrderStatus Status,
    decimal SlippageRatio,
    DateTimeOffset ExecutedAt,
    StopWidthFloorSource? StopFloorSource = null,
    OrderApprovalOrigin? ApprovalOrigin = null,
    int ProtectionReleasedQuantity = 0,
    DateTimeOffset? UnfilledCancelRequestedAt = null,
    DateTimeOffset? SoftwareStopCancelRequestedAt = null);
