namespace TradeDecisionService.Features.TradeDecision;

// 🔴 NFR-13, FR-02, UC-01, UC-02, #1423 M-5, ADR-0074 決定1・2, IADR-0582: 月次 LLM 費用上限の 100% 到達（Halted）を読むポート。
//
// 計画（ADR-0074）は「上限の 100% に達したら、定時サイクルと価格変動トリガーの両方の判断を、LLM を呼ぶ前に見送る」と定める。
// 費用統制を読むのは従来は情報収集の巡回だけで（Halted なら収集をしない＝定時の判断が起きない）、価格変動トリガーの判断は
// 100% 到達後も LLM を呼び続けていた。判断の合流点（DecideAsync）がこの口を読み、Halted なら LLM の前で見送る。
//
// 実体は費用統制サービスの GET /costs/state（情報収集と同じ口・同じ s2s）。安全既定は NoOp（常に停止していない＝従来どおり）。
// 照会の失敗・不正応答も「停止していない」へ倒す（IADR-0031 と同じ判断: 費用統制の一時障害で取引判断全体を止めるのは過大。
// 月次予算は緩変で短時間の不達では超過しにくい）。
public interface ILlmCostHaltGate
{
    /// <summary>月次 LLM 費用上限の 100% に達して停止中（Halted）か。照会できないときは false。</summary>
    Task<bool> IsHaltedAsync(CancellationToken cancellationToken = default);
}
