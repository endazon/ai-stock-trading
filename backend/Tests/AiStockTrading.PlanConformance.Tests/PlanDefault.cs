namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// 計画書で確定した既定値 1 件（FR-10, FR-19, FR-20, ADR-0018）。
/// </summary>
/// <param name="Key">実装側スナップショット（<see cref="ActualDefaults"/>）と突き合わせるキー。</param>
/// <param name="Value">
/// 確定値の**正規化文字列**。単位・基準（equity 比か固定額か、USD か JPY か）を必ず含める。
/// 数値だけで比較すると「25% を 25 と持つか 0.25 と持つか」「円かドルか」という
/// 基準の取り違えを素通しするため（本システムで最も危険な誤りの型）。
/// </param>
/// <param name="Citation">計画書上の出典（節・ADR 番号）。</param>
public sealed record PlanDefault(string Key, string Value, string Citation);
