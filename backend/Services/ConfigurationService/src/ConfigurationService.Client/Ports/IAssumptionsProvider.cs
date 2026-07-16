using AiStockTrading.Configuration.Domain;

namespace AiStockTrading.Configuration.Client.Ports;

// FR-17, IADR-0021/0064: バージョン付き全体前提条件の解決口。消費側サービス（費用統制 #139・損益集計・AI 判断・
// リスク統制）はこのポートだけを見る（HTTP・キャッシュ・fail-safe は実装側の関心）。
// 取得不可でも例外を出さず必ず値を返す（IADR-0064 決定 5）。解決できたかは VersionedAssumptions.IsResolved で判る。
public interface IAssumptionsProvider
{
    ValueTask<VersionedAssumptions> GetCurrentAsync(CancellationToken cancellationToken = default);
}

// FR-17, IADR-0064 決定 4: キャッシュ無効化の合図。AssumptionsChanged 購読（AssumptionsChangedConsumer）から呼ばれる。
public interface IAssumptionsCacheInvalidator
{
    void Invalidate();
}

// FR-17, IADR-0064 決定 1: 前提条件の取得元（GET /assumptions）。取得できなければ null を返し、例外は出さない。
public interface IAssumptionsSource
{
    Task<VersionedAssumptions?> FetchAsync(CancellationToken cancellationToken = default);
}
