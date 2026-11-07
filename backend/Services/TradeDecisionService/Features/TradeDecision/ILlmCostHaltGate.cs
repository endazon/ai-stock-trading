namespace TradeDecisionService.Features.TradeDecision;

// 🔴 NFR-13, FR-02, UC-01, UC-02, #1423 M-5, ADR-0074 決定1・2, IADR-0582: 月次 LLM 費用上限の 100% 到達（Halted）を読むポート。
//
// 計画（ADR-0074）は「上限の 100% に達したら、定時サイクルと価格変動トリガーの両方の判断を、LLM を呼ぶ前に見送る」と定める。
// 費用統制を読むのは従来は情報収集の巡回だけで（Halted なら収集をしない＝定時の判断が起きない）、価格変動トリガーの判断は
// 100% 到達後も LLM を呼び続けていた。判断の合流点（DecideAsync）がこの口を読み、Halted なら LLM の前で見送る。
//
// 実体は費用統制サービスの GET /costs/state（情報収集と同じ口・同じ s2s）。安全既定は NoOp（常に停止していない＝従来どおり）。
// 🔴 NFR-13, IADR-0639: 照会できないときの扱いは段（CostControl:HaltQuery:UnavailableMode）で決める。
//   Off（既定）: 「停止していない」へ倒す（IADR-0031 / IADR-0582 の従来の判断）。
//   Report    : 倒す向きは Off と同じ。最後に読めた時から猶予（CostControl:HaltQuery:GraceSeconds。既定 900 秒）を過ぎたら、Enforce なら
//               停止として扱うことをログに出す。
//   Enforce   : 猶予の間は最後に読めた値を使い、猶予を過ぎても読めなければ停止として扱う（fail-closed。LLM を呼ばない）。クラウドの既定。
public interface ILlmCostHaltGate
{
    /// <summary>月次 LLM 費用上限の 100% に達して停止中（Halted）か。照会できないときの扱いは段による（上の説明）。</summary>
    Task<bool> IsHaltedAsync(CancellationToken cancellationToken = default);
}

// NFR-13, IADR-0639: 費用統制の 1 回の照会の結果。読めれば停止かどうか、読めなければ null（失敗・タイムアウト・欠落・不正な本文）。
// 呼び出し元自身の取り消しは伝播する。
public interface ILlmCostHaltReader
{
    Task<bool?> ReadHaltedAsync(CancellationToken cancellationToken = default);
}
