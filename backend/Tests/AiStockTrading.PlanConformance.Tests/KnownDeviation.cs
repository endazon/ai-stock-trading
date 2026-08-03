namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// 計画確定値からの**既知の逸脱** 1 件。全面再実装（#344）の途上で一時的に受容するものだけを登録する。
/// </summary>
/// <param name="Key">逸脱しているキー（<see cref="PlanDefault.Key"/> と同一名前空間）。</param>
/// <param name="ActualValue">
/// 現行実装の値（正規化文字列）。**実際の値と一致していなければテストが失敗する**。
/// 担当 issue が値を修正すると本欄が実際値と食い違い、登録簿の更新が強制される。
/// </param>
/// <param name="OwningIssue">逸脱を解消する担当 issue 番号（本リポジトリ）。</param>
/// <param name="Reason">なぜ現時点で受容するのか。</param>
public sealed record KnownDeviation(string Key, string ActualValue, int OwningIssue, string Reason);
