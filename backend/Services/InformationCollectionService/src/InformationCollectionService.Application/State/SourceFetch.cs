using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Domain;

namespace AiStockTrading.InformationCollection.Application.State;

// FR-01, ADR-0020: 情報源アダプタに**名前を持たせて**扱う。
//
// 🔴 **名前を実行時まで持ち回るのが要点である。** 従来はファクトリを出た時点で名前が消えており、
// 「どのソースが落ちたか」を呼び出し側が知る手段が無かった（区分ごとの欠測判定＝ADR-0020 決定3 が成立しない）。
// 名前はカタログ（InformationSourceCatalog）の見出しと一致させる。
public sealed record NamedInformationSource(string Name, IInformationSource Source);

// FR-01: 1 巡回のソース横断の取得結果。アイテムと**ソース単位の成否**を分けて返す。
public sealed record SourceFetchResult(
    IReadOnlyList<RawInformationItem> Items,
    IReadOnlyList<SourceOutcome> Outcomes)
{
    public static SourceFetchResult Empty { get; } = new([], []);
}
