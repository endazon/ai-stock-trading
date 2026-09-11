namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

// NFR-05, IADR-0051, IADR-0323 決定 1, IADR-0328 決定 2, IADR-0332 決定 6, #746:
// **トークンを出さない**供給元。資格情報が未整備のときの安全既定である。
//
// 🔴 「トークンを付けない」は縮退であって成功ではない。`IServiceAccessTokenProvider` の契約
//（取得不能時は `null` を返す・例外は投げない）どおりに `null` を返し、
// **メタデータを付けずに送る → 提供側が `UNAUTHENTICATED` → 呼び出し元の既存 fail-safe** へ倒す。
// REST 側で「ハンドラを付けない → 401」に倒すのと同じ向きであり、輸送を変えても縮退の向きを変えない。
//
// null 実装を呼び出し側ごとに書かせない（書かせると「未整備のとき例外」を書く実装が混ざる）。
public sealed class NoServiceAccessTokenProvider : IServiceAccessTokenProvider
{
    public static readonly NoServiceAccessTokenProvider Instance = new();

    private NoServiceAccessTokenProvider()
    {
    }

    public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
