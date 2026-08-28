namespace RiskManagementService.Infrastructure;

// FR-10, FR-12, #257, IADR-0108 決定2/5: SIMULATE 限定リスク上限プロファイルの構成（セクション "Risk:SimulatorProfile"）。
// 有効/無効だけを構成で選ばせ、**上限値そのものは構成から与えない**（SimulatorTradingDefaults の Domain 定数が単一情報源）。
// 統制上限に「構成から任意の金額を注入できる口」を作らないための意図的な制限である。
//
// 既定 false＝本番既定（TradingDefaults）＝現行挙動。設定点は appsettings.Development.json と
// values-local.yaml（経路B）に限り、本番 values.yaml には置かない（既定描画をバイト等価に保つ）。
internal sealed class SimulatorProfileOptions
{
    public const string SectionName = "Risk:SimulatorProfile";

    /// <summary>SIMULATE 限定プロファイルを適用するか（既定 false＝本番既定のまま）。</summary>
    public bool Enabled { get; set; }
}
