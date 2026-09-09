using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Backtest;

// FR-04, FR-15, ADR-0033 決定2/決定4, IADR-0318: Stage 0 の**記録・再生方式**で受け渡す記録の契約。
//
// ADR-0033（2026-09-05 の利用者裁定）は Stage 0 の評価対象を「取引判断サービスの AI 判断そのもの」と定め、
// 記録（LLM を呼ぶ側）と再生（決定的なシミュレーション側）を分けた。本ファイルはその**境界の契約**である。
//   - 記録する側: TradeDecisionService（LLM 客・プロンプト・費用計測が既にそこにある）
//   - 再生する側: BacktestService（`IBacktestStrategy` は純関数。IADR-0043 の契約を覆さない）
// 両サービスは互いを参照できないため、契約は共有プロジェクトに置く（サービス間の直接参照は禁止）。

/// <summary>
/// FR-04, ADR-0033 決定1: 記録に残す判断の行動。
/// <para>
/// <c>TradeDecisionService.Domain.TradeAction</c> と同じ 3 値だが、**契約側で独立に定義する** ——
/// 契約プロジェクトはサービスの Domain へ依存できず、依存させれば再生側（BacktestService）が
/// 取引判断サービスの Domain を引くことになる。
/// </para>
/// </summary>
public enum Stage0DecisionAction
{
    Hold,
    Buy,
    Sell,
}

/// <summary>
/// FR-04, FR-11, ADR-0003, ADR-0033 決定4: **1 回分の生の判断**。
/// <para>
/// 🔴 ADR-0033 決定4 は「記録には多数決の結果だけでなく**各回の生の判断も残す**」と定める
/// （ADR-0003 の全量ログの規律）。判断の割れ方は Stage 0 の成績のばらつきを説明する一次情報であり、
/// 多数決結果だけを残すと、成績が LLM の非決定性で揺れたのか記録の質で揺れたのかを事後に切り分けられない。
/// </para>
/// </summary>
/// <param name="Attempt">1 始まりの試行番号（多数決の何回目か）。</param>
/// <param name="Unparseable">
/// 構造化出力を解析できなかったか（#290 / IADR-0248 の区別）。true のとき <see cref="Action"/> は
/// 安全既定の Hold であり、**「LLM が見送りを選んだ」のではない**。
/// </param>
public sealed record Stage0RawDecision(
    int Attempt,
    Stage0DecisionAction Action,
    string Rationale,
    decimal ReferencePrice,
    decimal StopLossDistancePerShare,
    int InputTokens,
    int OutputTokens,
    bool Unparseable);

/// <summary>
/// FR-04, FR-15, ADR-0033 決定2: **1 判断時点分**の記録（銘柄 × AsOf）。
/// </summary>
/// <param name="AsOf">判断時点（その日の終値までの情報だけで判断した日）。再生はこの日付で記録を引く。</param>
/// <param name="InputFingerprint">
/// 入力の指紋（プロンプト全文の SHA-256）。**プロンプト本文そのものは載せない** ——
/// 保有ポジション・資金残枠等の機微を含み、記録は再生のために配布されるためである
/// （全量の本文は FR-11 の LLM ログが <c>LlmGateway:LogPrompts</c> で担う。IADR-0061 決定1）。
/// 同じ入力で記録し直したかを突き合わせる用途に限る。
/// </param>
/// <param name="SignedQuantity">
/// 多数決結果に対応する目標注文数量（+ 買い / − 売り / 0 は見送り）。再生はこの値をそのまま
/// <c>BacktestOrder</c> へ写す（再生側でサイジングを再計算しない＝決定性を保つ）。
/// </param>
/// <param name="CostJpy">この判断時点で実際に発生した LLM 費用（円）。多数決の全回分の合計。</param>
public sealed record Stage0DecisionRecord(
    string Symbol,
    Market Market,
    DateOnly AsOf,
    string InputFingerprint,
    string ModelId,
    int VoteCount,
    IReadOnlyList<Stage0RawDecision> RawDecisions,
    Stage0DecisionAction MajorityAction,
    string MajorityRationale,
    int SignedQuantity,
    decimal CostJpy,
    int InputTokens,
    int OutputTokens);

/// <summary>記録集合が対象とした銘柄。</summary>
public sealed record Stage0RecordedSymbol(string Symbol, Market Market);

/// <summary>
/// FR-15, FR-20, ADR-0033 決定2/決定3, IADR-0281: **記録集合**。再生側はこの単位で受け取り、
/// 期間・銘柄・カットオフ日が評価の構成と整合するかを検査してから評価へ進む。
/// </summary>
/// <param name="LlmTrainingCutoff">
/// 記録に用いた LLM 学習カットオフ日（ADR-0033 決定3）。**再生側の構成値と一致しなければ評価しない** ——
/// 別のカットオフ前提で採った記録を、いま構成されているカットオフの検証結果として使えば、
/// 検証条件①（汚染対策）が確認されていないのに満たしたことになる。
/// </param>
/// <param name="StrategyId">
/// 戦略の同一性（IADR-0281 決定3 の「戦略の変更」を機械判定する鍵）。
/// <see cref="Stage0StrategyIdentity"/> が記録の**内容から**導出する。
/// </param>
public sealed record Stage0DecisionRecordSet(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<Stage0RecordedSymbol> Symbols,
    DateOnly LlmTrainingCutoff,
    DateTimeOffset CreatedAt,
    string ModelId,
    string StrategyId,
    IReadOnlyList<Stage0DecisionRecord> Records);
