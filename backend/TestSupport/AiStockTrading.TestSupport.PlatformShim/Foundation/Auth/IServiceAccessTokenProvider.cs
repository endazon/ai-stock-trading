namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

// IADR-0051 決定 1/2: サービス間同期照会へ付与するアクセストークンの供給。取得不能時は null を返す
// （＝ヘッダ無しで送信 → 提供側 401 → 呼び出し側の既存フェイルセーフ）。実装は例外を送出しない。
public interface IServiceAccessTokenProvider
{
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);
}
