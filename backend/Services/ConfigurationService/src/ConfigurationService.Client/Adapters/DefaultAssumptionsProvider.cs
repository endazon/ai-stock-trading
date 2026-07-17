using AiStockTrading.Configuration.Client.Ports;
using AiStockTrading.Configuration.Domain;

namespace AiStockTrading.Configuration.Client.Adapters;

// FR-17, IADR-0063 決定 6: 設定サービスの BaseUrl が未設定/不正のときの既定プロバイダ。HTTP を一切発生させず、
// 前提条件の既定値（Version=0＝未解決）だけを返す。既定ビルド/CI・ローカル単体実行を外部接続なしで成立させる。
internal sealed class DefaultAssumptionsProvider : IAssumptionsProvider, IAssumptionsCacheInvalidator
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
