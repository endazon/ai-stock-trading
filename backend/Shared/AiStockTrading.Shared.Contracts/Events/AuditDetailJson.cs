using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-11, FR-06, #381, IADR-0019, IADR-0199 決定6: 監査台帳の `Detail`（イベント全量 JSON）の書式。
//
// 🔴 **書き手と読み手が同じ 1 つの定義を使う。**
// 書き手は監査サービス（`AuditSerialization`）、読み手は報告サービス（`HttpFxSourceStatusSource`）であり、
// **別々のアセンブリに在る**。ここを 2 箇所に書くと——
//
// **命名規約を片側だけ変えたとき、`JsonSerializer` は例外を投げない。**
// **既定値（0 / null）で埋めた record を黙って作る**——**金額や数量が 0 の行が報告書に載る**という、
// 最も気づきにくい壊れ方をする。`FxSourceCredits` を契約に置いたのと同じ理由である。
//
// **これは実装の詳細ではなく、台帳に書いた形をあとで読み戻せるという約束である。**
public static class AuditDetailJson
{
    /// <summary>
    /// 監査 <c>Detail</c> の読み書きに使う唯一の設定。
    /// <para>
    /// 列挙は<b>文字列</b>にする（人が台帳を読んで監査できるようにするため。IADR-0019）。
    /// </para>
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
