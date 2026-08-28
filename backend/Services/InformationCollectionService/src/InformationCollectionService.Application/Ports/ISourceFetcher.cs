using AiStockTrading.InformationCollection.Application.State;

namespace AiStockTrading.InformationCollection.Application.Ports;

// FR-01, ADR-0020 決定3: 有効化された情報源をまとめて 1 巡回ぶん取得し、**ソース単位の成否**を返すポート。
//
// 🔴 IInformationSource（1 ソース）とは責務が違う。**欠測の判定はソース単位の成否を要する**ため、
// アイテムを平坦に連結して返すだけの合成では足りない（IADR-0064 の合成が持っていた限界）。
public interface ISourceFetcher
{
    Task<SourceFetchResult> FetchAllAsync(CancellationToken cancellationToken = default);
}
