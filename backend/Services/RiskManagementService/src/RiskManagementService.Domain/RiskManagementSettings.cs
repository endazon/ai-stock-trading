using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10, FR-19, FR-20: リスク管理サービスが参照する設定の集約（設定ストア由来）
public record RiskManagementSettings(
    TradingGuardSettings Guard,
    RiskLimitSettings Limits,
    StageSettings Stage)
{
    /// <summary>
    /// FR-20, FR-12, FR-13, INDEX 決定 46, #334, IADR-0140 決定4: <b>現在の発注先</b>（Broker Provider）。
    /// <para>
    /// 運用段階（<see cref="Stage"/>）とは<b>独立した軸</b>である。段階を変更しても本値は自動で変わらず、
    /// 本値を変更しても段階は自動で変わらない。<see cref="StageSettings.Mode"/> は「その段階で通常選ぶ
    /// 既定の組み合わせ」にすぎず、現在値ではない。
    /// </para>
    /// <para>
    /// 位置指定の引数にせず本体のプロパティに置くのは <see cref="ShortSell"/> と同じ理由である——
    /// <b>既定が安全側（外部へ一度も発注しない内蔵 <c>paper</c>）に固定され</b>、明示しない呼び出しや
    /// 本プロパティを持たない旧い永続行が実弾を指してしまわないようにするため。
    /// <b>計画は初期値を述べていない</b>（IADR-0140 決定4）。
    /// </para>
    /// </summary>
    public BrokerProvider BrokerProvider { get; init; } = BrokerProvider.InternalPaper;

    /// <summary>
    /// FR-10, ADR-0016, #329 第 2 段階: 空売りの有効・無効と専用統制値。既定は**無効**（現物のみ）。
    /// 位置指定の引数にせず本体のプロパティに置くのは、既定が安全側（無効）に固定されており、
    /// 明示しない呼び出し（既存の設定生成・テスト）が空売りを有効化してしまわないようにするためである。
    /// </summary>
    public ShortSellSettings ShortSell { get; init; } = TradingDefaults.CreateShortSellSettings();
}
