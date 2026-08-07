using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-11, FR-06, UC-06, ADR-0016 決定4（2026-08-06 改訂）・決定15（2026-08-06 追記）, #419, IADR-0159:
// 建玉の消失を自らの決済指示（約定履歴・処理中の決済承認）と突合し、**強制買戻し（buy-in）と推定した**。
//
// **これは検知ではなく推定である。** イベント検知の供給元が無い（SIMULATE では原理的に発生せず、専用の通知 API も
// 見当たらない）ため、決定4 の改訂が定めた代替経路の出力である。記録先（監査ログ・Discord 通知・日報・月報）は
// いずれも「**強制買戻しと推定**」と明示し、確定した事実として扱わない。
//
// **本イベントの件数が、ADR-0016 決定15 が求める「強制買戻しの発生有無・発生回数」の集計元である。**
// `RejectionReason.BuyInBanned`（拒否理由）の件数を集計元にしてはならない——同理由は禁止期間中の発注拒否であり、
// **1 回の強制買戻しに対して 30 日のあいだ何度でも発生し得る**（実際より大きな数字が月報に載る）。
public record BuyInInferred(
    Guid EventId,
    string Symbol,
    Market Market,
    // 台帳（＝自らの約定履歴の射影）が示す空売り建玉の数量（正の値）。
    int LedgerShortQuantity,
    // ブローカが示す空売り建玉の数量（正の値）。応答に現れない銘柄は 0（＝全量消失）として扱う。
    int BrokerShortQuantity,
    // 自らの決済指示のうち、まだ約定が台帳へ届いていない数量（承認済みの決済＝「処理中の決済」）。
    int InFlightCloseQuantity,
    // 自らの決済指示で説明できない消失の**累計**（＝これまでに強制買戻しへ帰属させた数量）。
    int UnexplainedQuantity,
    // 今回**新たに**推定した消失数量（累計から前回までの帰属分を引いた増分）。
    int NewlyInferredQuantity,
    // 突合した自らの決済約定（推定の根拠・FR-11）。当該建玉を建ててから現在までの決済約定である。
    IReadOnlyList<BuyInCoveringFill> CoveringFills,
    // 30 日の空売り禁止の解除日（ADR-0016 決定4）。
    DateOnly BanUntil,
    // 突合に用いたブローカ建玉の観測時刻。
    DateTimeOffset ObservedAt,
    // 推定日時（FR-11 が求める根拠の 1 つ）。
    DateTimeOffset InferredAt);
