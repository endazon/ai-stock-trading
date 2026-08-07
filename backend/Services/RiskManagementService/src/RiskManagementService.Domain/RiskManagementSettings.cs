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
    /// </para>
    /// <para>
    /// FR-20 (3), #422: <b>初期値が内蔵 <c>paper</c> であることは計画が 2026-08-07 に確定させた</b>
    /// （「発注先の初期値は内蔵 <c>paper</c> とする——外部へ一度も発注しない唯一の値であり、
    /// 初期段階が Stage 0 であることとも整合する。設定ストアの旧行〔本項目を持たない場合〕も
    /// 同じ既定へ落とす」）。IADR-0140 決定4 が「計画は初期値を述べていない」として選んだ既定を
    /// 裁定が追認したものである。永続行からの解決は <see cref="BrokerProviderResolution"/>（allow-list）
    /// を通す（IADR-0161）。
    /// </para>
    /// </summary>
    public BrokerProvider BrokerProvider { get; init; } = BrokerProviderResolution.Default;

    /// <summary>
    /// FR-20, FR-13, SC-02, #423, IADR-0165 決定4: **Stage 1 の最小取引件数**（06_daytrading-review §4.1 条件 3）。
    /// <para>
    /// 2026-08-07 の利用者裁定（質問票 第 13 回 Q6 の追加指示）により、条件 3 の件数は
    /// **SC-02 から変更できる設定値**になった。既定 100 件・値域 1〜1000
    /// （<see cref="Stage1TradeCountBounds"/>）。他のリスク設定と同様に変更理由必須・監査ログ記録・
    /// 版（楽観排他）の対象である。
    /// </para>
    /// <para>
    /// <b>変えられるのは条件 3 の件数だけである。</b> 条件 1（統制違反 0 件）・条件 2（60 営業日）・
    /// §4.3 の打ち切り規則（累計 120 営業日）には及ばない（裁定が明示）。本プロパティは
    /// <see cref="Stage1GateCriteria.MinimumTradeCount"/> にだけ重なる。
    /// </para>
    /// <para>
    /// 位置指定の引数にせず本体のプロパティに置くのは <see cref="BrokerProvider"/> と同じ理由である——
    /// **既定が計画の確定値（100）に固定され**、明示しない呼び出しや本項目を持たない旧い永続行が
    /// 「合格に近い小さな件数」を指してしまわないようにするため。
    /// </para>
    /// </summary>
    public int Stage1MinimumTradeCount { get; init; } = Stage1TradeCountBounds.Default;

    /// <summary>
    /// FR-10, ADR-0016, #329 第 2 段階: 空売りの有効・無効と専用統制値。既定は**無効**（現物のみ）。
    /// 位置指定の引数にせず本体のプロパティに置くのは、既定が安全側（無効）に固定されており、
    /// 明示しない呼び出し（既存の設定生成・テスト）が空売りを有効化してしまわないようにするためである。
    /// </summary>
    public ShortSellSettings ShortSell { get; init; } = TradingDefaults.CreateShortSellSettings();
}
