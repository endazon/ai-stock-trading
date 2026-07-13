namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

// IADR-0051: サービス間同期照会（OwnerOrService エンドポイント）へ付与する client_credentials トークンの構成。
// ClientId/ClientSecret が揃い、TokenEndpoint が解決できる時のみ有効（IsEnabled）。秘密はコミットせず config
// （user-secrets / .env）から供給する。
public sealed class ServiceAuthOptions
{
    public const string SectionName = "ServiceAuth";

    // token エンドポイント（明示指定）。未指定なら Auth:Authority から導出する。
    public string? TokenEndpoint { get; set; }

    // 機密クライアント（例: ai-stock-trading-svc）。
    public string? ClientId { get; set; }

    // 機密クライアントの秘密。値はコミットしない（IADR-0051 決定 4）。
    public string? ClientSecret { get; set; }

    // 任意のスコープ（省略可）。
    public string? Scope { get; set; }

    // expires_in からこの秒数を引いた時点で失効扱いにし、先行更新する（時計ずれ・往復遅延の余裕）。
    public int RefreshSkewSeconds { get; set; } = 30;

    // 有効化条件: 資格情報が揃い、token エンドポイントが解決済み。いずれか欠ければ no-op（安全側）。
    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(TokenEndpoint);
}
