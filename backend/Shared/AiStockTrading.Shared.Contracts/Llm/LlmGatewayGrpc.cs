namespace AiStockTrading.Shared.Contracts.Llm;

// NFR, FR-04, FR-06, MSP:ADR-0029, IADR-0284, IADR-0328, IADR-0332 決定 2, #746:
// LLM ゲートウェイを east-west gRPC で呼ぶための設定点。**この 1 キーの有無だけで輸送が決まる。**
//
// 🔴 **既定は REST である。** キーが無ければ何も変わらない（helm の既定描画も 1 バイト変わらない）。
// 呼び出す 2 サービス（TradeDecisionService / ReportService）が同じ文字列を別々に書くと、
// 片方のタイプミスが「REST のままだった」＝**静かな未移行**になって現れる。両者が引く 1 箇所に置く
// （`LlmGatewayAuth` と同じ理由）。
public static class LlmGatewayGrpc
{
    /// <summary>
    /// h2c の宛先（例 <c>http://llmgateway-service.microservices-platform:8081</c>）。
    /// env では <c>LlmGateway__Grpc</c>。REST の <c>LlmGateway:BaseUrl</c>（8080）とは**別のポート**である
    /// （メッシュ内は平文で ALPN が無いため、プロトコルの選択は切替ではなく分離で決める。IADR-0328 決定 3）。
    /// </summary>
    public const string AddressKey = "LlmGateway:Grpc";

    /// <summary>
    /// 構成値を宛先へ解決する。未設定・空白・<c>http</c>/<c>https</c> 以外は <c>null</c>（＝gRPC を使わない）。
    /// </summary>
    /// <remarks>
    /// 🔴 **不正な値で例外にしない。** ここで落とすと、綴り誤りがサービスの起動不能に化ける ——
    /// LLM は本システムの安全既定（Hold / プレースホルダ散文）が効く経路であり、
    /// 「gRPC にならなかった」は縮退で足りる。`Grpc:Port`（受け側のリスナ）が構成誤りで落とすのとは
    /// 立場が逆である（あちらは「立てたつもりで立っていない」が沈黙するため落とす）。
    ///
    /// 🔴 **scheme まで見る。** `Uri.TryCreate(..., Absolute)` は <c>llmgateway-service:8081</c> のような
    /// 「ホスト:ポート」を**scheme が `llmgateway-service` の絶対 URI として受理する**（実測）。
    /// 素通しすると、`http://` を書き忘れた値でチャネル生成が起動時に落ちる ——
    /// 「gRPC にならなかった」で済むはずの誤設定が、サービスの起動不能に化ける。
    /// 想定は <c>http</c>（h2c。メッシュ内の TLS はサイドカーが終端するのでアプリから見た線は平文。
    /// IADR-0328 決定 1）で、<c>https</c> も受ける（チャネル側が扱う）。
    /// </remarks>
    public static Uri? ResolveAddress(string? configured) =>
        !string.IsNullOrWhiteSpace(configured)
        && Uri.TryCreate(configured, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
}
