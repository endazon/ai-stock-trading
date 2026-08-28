using AiStockTrading.Shared.Kernel.Trading;

namespace CostControlService.Infrastructure.ExternalServices;

// NFR, #526, IADR-0264 決定 1: 旧 ConfigurationService.Client（共有クライアント）から本サービスの
// Infrastructure/ExternalServices へ移した。計画は「キャッシュ・タイムアウト・fail-safe・DI 拡張は
// **呼び出し元**の Infrastructure に置く」と定めており（呼び出し先が固定すると合わない側が回避策を書く）、
// 呼び出し元ごとの複製は計画が承知のうえで選んだ形である。**移送時に中身は変えていない。**

// FR-17, IADR-0063 決定 6: 設定サービスの BaseUrl が未設定/不正のときの既定プロバイダ。HTTP を一切発生させず、
// 前提条件の既定値（Version=0＝未解決）だけを返す。既定ビルド/CI・ローカル単体実行を外部接続なしで成立させる。
public sealed class DefaultAssumptionsProvider : IAssumptionsProvider, IAssumptionsCacheInvalidator
{
    private static readonly VersionedAssumptions Defaults =
        new(TradingAssumptionsDefaults.Create(), VersionedAssumptions.UnresolvedVersion);

    public ValueTask<VersionedAssumptions> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Defaults);

    // 無効化するキャッシュを持たない（購読は消費側 Program で静的に登録されるため受け口だけ用意する）。
    public void Invalidate()
    {
    }
}
