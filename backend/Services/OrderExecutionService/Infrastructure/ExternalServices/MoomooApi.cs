using Moomoo.OpenApi;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// #13, #1117, IADR-0464: moomoo SDK（MMAPI4Net）のプロセス全体の初期化（MMAPI.Init）を 1 回だけ呼ぶ口。
// 発注の接続（MMApiMoomooTradeClient）と、K 線の検証口の相場の接続（MMApiMoomooKLineProbeClient）が共有する。
// 以前は MMApiMoomooTradeClient が同じ処理（静的なフラグ＋ロック）を自分で持っていた。挙動は変えていない。
public static class MoomooApi
{
    private static readonly OnceInitializer Initializer = new(MMAPI.Init);

    public static void EnsureInitialized() => Initializer.Ensure();
}

// 二重化防止の本体（#1117・PR #1119 の監査 F3）。MMAPI.Init はプロセス全体の状態を触るため試験から呼び数えられない。
// 初期化の処理を差し込める最小の internal 型に分け、「何度呼んでも初期化は 1 回」を試験で固定する（MoomooApi は委ねるだけ）。
internal sealed class OnceInitializer(Action initialize)
{
    private readonly object _gate = new();
    private bool _initialized;

    public void Ensure()
    {
        lock (_gate)
        {
            if (_initialized)
                return;
            initialize();
            _initialized = true;
        }
    }
}
