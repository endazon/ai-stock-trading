namespace AiStockTrading.Shared.Contracts.Events;

// FR-01, FR-02, UC-01, IADR-0022: 情報収集サービスが 1 巡回の収集を完了した。取引サイクル（FR-02）の起点となる。
// ItemCount は正規化・KB 保存まで完了したアイテム数。
//
// FR-04, ADR-0020 決定2, #1081, IADR-0455: NewsStatus / NewsStatusValidFor は**追加のみの任意項目**（既定 null）。
// 欠測の明示を RAG（KB の collection-status 文書）に頼らず、取引判断のプロンプトへ直接届けるために運ぶ。
// 🔴 **null は「不明」である**（旧発行側・状態を判定できなかった巡回）。受け手は null を「取得済み」と読まない。
/// <param name="NewsStatus">この巡回のニュース系の状態（取得済み／欠測／未構成）。null＝不明。</param>
/// <param name="NewsStatusValidFor">
/// NewsStatus が有効な期間（CollectedAt から数える）。受け手は収集の巡回間隔を知らないため発行側が宣言する
/// （<c>InformationSourceStateObserved.ValidFor</c> と同じ作法）。受け手側で上下限へクランプする。
/// </param>
public record InformationCollected(
    Guid EventId,
    int ItemCount,
    DateTimeOffset CollectedAt,
    NewsCollectionStatus? NewsStatus = null,
    TimeSpan? NewsStatusValidFor = null);
