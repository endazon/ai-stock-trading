using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.State;

// FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）・決定15, #419, IADR-0159 決定2/決定3:
// 強制買戻しの事後推定を記録する**追記専用**の 1 行。
//
// 行は 2 種類ある。
//   - **推定行**（NewlyInferredQuantity > 0）: 自らの決済指示で説明できない消失を新たに推定した。
//     **ADR-0016 決定15 が求める「発生回数」の集計元はこの行の件数である**（`BuyInBanned` の拒否件数ではない）。
//   - **リセット行**（NewlyInferredQuantity == 0）: 乖離が解消し、帰属数量（UnexplainedQuantity）を 0 へ戻した。
//     **禁止期限は戻さない**（30 日は経過でしか解けない・ADR-0016 決定10 の「期間の経過で解除される禁止状態」）。
//     そのためリセット行の BanUntil は null であり、既存の推定行が持つ期限が引き続き効く。
public sealed record BuyInInferenceRecord(
    Guid Id,
    string Symbol,
    Market Market,
    // 台帳（＝自らの約定履歴の射影）が示す空売り建玉の数量（正の値）。
    int LedgerShortQuantity,
    // ブローカが示す空売り建玉の数量（正の値。応答に現れない銘柄は 0＝全量消失）。
    int BrokerShortQuantity,
    // 承認済みだが約定が台帳へ届いていない決済数量（＝処理中の自らの決済指示）。
    int InFlightCloseQuantity,
    // 自らの決済指示で説明できない消失の**累計**（＝現時点で強制買戻しへ帰属させている数量）。
    int UnexplainedQuantity,
    // 今回**新たに**推定した数量（0 はリセット行＝発生回数に数えない）。
    int NewlyInferredQuantity,
    // 30 日の空売り禁止の解除日。リセット行は null。
    DateOnly? BanUntil,
    // 推定した取引日（期間集計＝決定15 の発生回数の単位）。
    DateOnly InferredOn,
    DateTimeOffset ObservedAt,
    DateTimeOffset InferredAt);
