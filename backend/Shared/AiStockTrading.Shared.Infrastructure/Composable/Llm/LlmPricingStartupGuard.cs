namespace AiStockTrading.Shared.Infrastructure.Composable.Llm;

/// <summary>起動時の LLM 単価の判定結果。</summary>
public enum LlmPricingStartupVerdict
{
    /// <summary>LLM を呼ばない構成か、単価がある。何もしない。</summary>
    Ok,

    /// <summary>LLM を呼ぶのに単価が実質 0（配備でない環境）。警告して起動する。</summary>
    Warn,

    /// <summary>LLM を呼ぶのに単価が実質 0（配備＝Production）。起動しない。</summary>
    Refuse,
}

// NFR-13, FR-04, IADR-0499, #1197: LLM ゲートウェイが構成されているのに単価が実質 0（モデル別の表が空 かつ 従来キーも無い）なら、
// 配備（環境名 Production。chart の既定・IADR-0496）では**起動しない**。0 円計上のまま稼働すると月次費用上限（¥15,000）の
// 80% / 100% 判定（取引判断サイクルの費用）が構造的に発火せず、月報に載せる報告書生成の費用実績も 0 円になる。
// 配備でない環境（Development の docker-compose・試験の Testing）は従来どおり警告に留める（#817）。
// broker.tier は判定に使わない —— LLM の費用はブローカ階層に依らず実費である（paper でも同じ）。
// 計上時の解決（LlmPriceTable.Resolve）は変えない（IADR-0055: 計測は best-effort＝LLM 応答を壊さない）。止めるのは起動だけ。
public static class LlmPricingStartupGuard
{
    /// <summary>警告・拒否の文言の共通の目印（運用者がログを引く語）。</summary>
    public const string Marker = "LLM 単価が未設定";

    /// <summary>配備でない環境で出す警告（#817）。</summary>
    public const string WarningMessage =
        Marker + "のため、LLM 費用は全呼び出し 0 円で計上される（月次費用上限が発火しない）。" +
        "LlmPricing__PerModel__<model>__InputPer1kTokens / __OutputPer1kTokens を設定する" +
        "（env 名ではモデル ID の - を _ で書く。- を含む env 名は起動シェルが落とす・#817）。";

    /// <summary>配備（Production）で起動を止める理由。</summary>
    public const string RefusalMessage =
        Marker + "（モデル別の表が空 かつ 従来キーも無い）のに LLM ゲートウェイが構成されているため起動しない。" +
        "このまま稼働すると LLM 費用が全呼び出し 0 円で計上され、月次費用上限（¥15,000）の 80% / 100% 判定が発火せず、月報の費用実績も 0 円になる。" +
        "配備時の values（ArgoCD の valueFiles・helm の -f）で LlmPricing__PerModel__<model>__InputPer1kTokens / __OutputPer1kTokens を与える" +
        "（env 名ではモデル ID の - を _ で書く）。LLM を使わないなら LlmGateway__BaseUrl / LlmGateway__Grpc を外す（IADR-0499 / #1197）。";

    /// <summary>
    /// 未知モデルの計上単価（<c>LlmPricing:UnknownModel:*</c>）が解析できない・0 以下のときの起動時の警告（#1305, IADR-0553）。
    /// 起動は止めない（計上は既定値 $10 / $50 で続き、0 円にも表の最大にも倒れないため）。
    /// </summary>
    public const string UnknownModelSettingWarningMessage =
        "未知モデルの計上単価（LlmPricing__UnknownModel__InputPer1kTokens / __OutputPer1kTokens）が解析できない・0 以下のため、" +
        "その成分は既定値（入力 1.637・出力 8.186 円/1k＝$10 / $50）で計上する。値を円/1k トークンの正の数（小数点は .）で書き直す。";

    /// <summary>
    /// 起動時に単価の欠けをどう扱うかを決める。
    /// </summary>
    /// <param name="priceTable">各サービスが自前の構成から組み立てた単価表。</param>
    /// <param name="llmGatewayConfigured">LLM ゲートウェイ（REST の絶対 URI か gRPC のアドレス）が構成されているか。</param>
    /// <param name="isProduction">環境名が Production か（配備の既定。IADR-0496）。</param>
    public static LlmPricingStartupVerdict Evaluate(LlmPriceTable priceTable, bool llmGatewayConfigured, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(priceTable);
        if (!llmGatewayConfigured || !priceTable.IsEffectivelyZero)
            return LlmPricingStartupVerdict.Ok;

        return isProduction ? LlmPricingStartupVerdict.Refuse : LlmPricingStartupVerdict.Warn;
    }
}
