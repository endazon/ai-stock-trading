using Moomoo.OpenApi;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// #13, #1117, IADR-0464: moomoo SDK（MMAPI4Net）のプロセス全体の初期化（MMAPI.Init）を 1 回だけ呼ぶ口。
// 発注の接続（MMApiMoomooTradeClient）と、K 線の検証口の相場の接続（MMApiMoomooKLineProbeClient）が共有する。
// 以前は MMApiMoomooTradeClient が同じ処理（静的なフラグ＋ロック）を自分で持っていた。挙動は変えていない。
public static class MoomooApi
{
    private static readonly object InitGate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (InitGate)
        {
            if (_initialized)
                return;
            MMAPI.Init();
            _initialized = true;
        }
    }
}
