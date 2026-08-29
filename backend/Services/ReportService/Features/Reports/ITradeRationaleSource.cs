namespace ReportService.Features.Reports;

// FR-16, FR-11, #563, IADR-0268: 日報 §2「判断根拠（要約）」に載せる**記録済みの判断根拠**を供給するポート。
//
// 🔴 **報告書生成時に文章を作らない。** 権威源は監査台帳（`TradeDecisionMade` をイベント全量 JSON で 7 年保持）
// であり、判断した時点で記録された `Rationale` を `DecisionId` で引いて**そのまま**明細へ載せる
//（FR-16・IADR-0251。LLM に書かせてよいのは散文だけである）。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。空の辞書（＝記録はあるが 1 件も無い）と混ぜない。**
// 同居する `IPeriodFillSource`（不達＝空列）とは**向きが逆**であり、`IBuyInInferenceRecordSource` と同じ向きである。
// 根拠が引けなかったことを「根拠が無かった」と書けば、判断の説明責任が果たされていない状態が正常に見える。
public interface ITradeRationaleSource
{
    /// <summary>
    /// 期間 [fromInclusive, toInclusive]（JST 取引日）に記録された判断根拠を <c>DecisionId</c> 引きで返す。
    /// 取得不能なら <c>null</c>（例外を投げない）。
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>?> GetRationalesAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default);
}
