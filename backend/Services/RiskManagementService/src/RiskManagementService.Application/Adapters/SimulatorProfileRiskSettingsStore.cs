using RiskManagementService.Application.Ports;
using RiskManagementService.Domain;

namespace RiskManagementService.Application.Adapters;

// FR-10, FR-12, FR-20, #257, IADR-0108 決定3: SIMULATE 限定のリスク上限プロファイルを**読み取り時**に適用する
// デコレータ。内側のストア（永続化の権威・IADR-0012）はそのままで、返す設定を必要なだけ差し替える。
// 比率系・保有建玉数・取引ガードは内側の設定をそのまま通す（利用者が SC-02 で行った変更を握りつぶさない）。
//
// #329, IADR-0130 決定6: 金額系の上限は equity 比で保持されるようになったため、**上限そのものの差し替えは
// 不要**になった（基準資金＝SimulatorTradingDefaults.InitialCapital をホストが注入すれば、上限額は
// 比例して自動的に上がる）。旧実装が持っていた金額系 2 項目の上書きは削除した。
//
// #333, IADR-0136: 残っていた**ペーパー段階の資金上限の差し替え**も不要になった。段階の発注可能額が
// 総資金比（StageSettings.CapitalCapRatio）になり、比率はスケール不変だからである。**本デコレータが
// 差し替える項目はもう無く、素通しのみが残る。** 型としては残置する——プロファイルの適用点が
// 将来また必要になったときの単一の場所であり、消すと次に必要になったとき配線ごと復元する羽目になる。
// 実弾段階（BrokerProvider.MoomooReal）の上限を検証用フラグで動かさないという不変条件（IADR-0108 決定4）は、
// 差し替え対象が存在しないことで**構造的に**成立する。
//
// シード時ではなく読み取り時に上書きするのは、本番既定が既にシードされた検証用 DB でもリセット無しで
// 上限が効くようにするため。DB の設定行は書き換えないため、プロファイルを外せば即座に本番既定へ戻る。
public sealed class SimulatorProfileRiskSettingsStore(IRiskSettingsStore inner) : IRiskSettingsStore
{
    public RiskManagementSettings GetCurrent() => inner.GetCurrent();

    // 永続化は素通しする（プロファイルはメモリ上の上書きに留め、DB を汚さない）。
    public void Save(RiskManagementSettings settings) => inner.Save(settings);
}
