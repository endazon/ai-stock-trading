namespace AiStockTrading.Shared.Infrastructure.Composable;

// NFR, FR-01, FR-10, #1532, IADR-0614: 外部（情報源・市場データ・為替・過去の日足）を取る名前付き HttpClient の応答の本文の上限の唯一の置き場所。
//
// HttpClient の既定（MaxResponseContentBufferSize = int.MaxValue ≒ 2 GiB）のままだと、相手が巨大な本文を返したとき
// 1 要求でサービスの容器（512Mi）のメモリを使い切る。上限は既知の最大の応答（SEC の銘柄と CIK の一覧・EDINET の日次の書類一覧・
// 過去の日足の CSV。いずれも数 MiB 以下）に桁の余裕を取った値である。超えた応答は HttpRequestException になり、
// 各アダプタの既存の「取得できない」の経路（その銘柄・その回の情報源を飛ばす）へ倒れる。
// 🔴 本文をバッファする読み方（SendAsync / GetAsync の既定 = ResponseContentRead）にだけ効く。ResponseHeadersRead で流し読みする
// アダプタを足すときは、読み手の側で上限を持つこと。
public static class ExternalHttpResponseLimits
{
    /// <summary>外部の HTTP の応答の本文の上限（16 MiB）。</summary>
    public const long MaxResponseBytes = 16L * 1024 * 1024;
}
