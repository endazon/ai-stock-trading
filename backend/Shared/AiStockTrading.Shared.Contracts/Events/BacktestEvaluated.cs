namespace AiStockTrading.Shared.Contracts.Events;

// FR-15, FR-20, FR-11, UC-06, ADR-0008, IADR-0089: バックテスト（Stage 0 合格判定・BacktestService/#16）の verdict。
// Risk が購読して段階別実績ストア（IStagePerformanceStore）へ射影し、Stage 0→1 昇格ゲートの入力
// （BacktestPassed / BacktestMaxDrawdownRatio）を解錠する（#164）。監査サービスも購読して中央監査台帳へ集約する（FR-11）。
// verdict・メトリクスは BacktestService の純ドメイン（Stage0Decision）で判定・算出され、本契約には primitive で渡す。
// 段階/enum に依存しないよう BacktestService.Domain / RiskManagementService.Domain へ依存させない（依存逆転を避ける・IADR-0082 と同型）。
//
// FR-20, ADR-0016 決定14, #388, IADR-0281 決定3: **空売り実弾解禁の判定入力**として 2 項目を足した。
//   IncludesShortSelling — 「空売りを**含む**戦略」で合格したか。決定14 は空売りを含む戦略での再充足を求めており、
//                          含まない戦略の合格では解禁できない（Passed だけでは表現できない）。
//   StrategyId           — 戦略の同一性を名乗る識別子。verdict の無効化契機「戦略の変更」を機械判定する唯一の鍵。
// いずれも primitive であり、段階/enum への依存は増やさない。
//
// FR-15, ADR-0039 決定1, #777, IADR-0337 決定4: **PBO を測ったかどうかを運ぶ 2 項目を足した。**
//   PboEvaluated           — PBO を実際に算出したか。🔴 **false のとき ProbabilityOfBacktestOverfitting の値は
//                            意味を持たない**（探索を持たない戦略では PBO が測る対象そのものが存在しない）。
//                            読み手は必ず本項目を先に見ること。計画 ADR-0039 は「**『PBO は 0 だった』と
//                            書かない**」と定めた —— 測っていないことと差が無かったことを読み分ける。
//   PboNotEvaluableReason  — false のときの理由（FailedChecks と同じく enum 名を運ぶ。評価済みなら空文字）。
// 🔴 **ProbabilityOfBacktestOverfitting の型は変えない。** Double → Double? は
// EventBackwardCompatibilityTests が破壊的変更（型変更）として赤にする。「追加のみ許可」の規律に従い、
// 意味づけを追加項目で与える。
// 🔴 **既定値を置かない。** 省略できる口を作ると、書き忘れが「PBO を測った」と名乗る。旧メッセージが
// JSON から復元されたときは PboEvaluated=false へ倒れる＝**数値を名乗らない側**であり fail-safe である。
public record BacktestEvaluated(
    bool Passed,
    decimal MaxDrawdownRatio,
    double DeflatedSharpe,
    double ProbabilityOfBacktestOverfitting,
    string FailedChecks,
    DateTimeOffset EvaluatedAt,
    bool IncludesShortSelling,
    string StrategyId,
    bool PboEvaluated,
    string PboNotEvaluableReason);
