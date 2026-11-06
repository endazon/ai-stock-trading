using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-17, ADR-0003, ADR-0007, ADR-0008: 現行のリスク管理設定の取得・保存。実運用では PostgreSQL 設定ストア（バージョン管理・Slice B）。
// 変更は利用者のみ（RiskSettingsService 経由で履歴を記録する）。生成AI・自動処理は本ポートを直接呼ばない。
public interface IRiskSettingsStore
{
    RiskManagementSettings GetCurrent();

    /// <summary>
    /// 設定を保存する。FR-19, #1220, IADR-0511: **商品種別の集合が変わる保存では、実装が
    /// 商品種別設定の改訂番号を 1 進める**（<see cref="ProductTypeSettingsRevision.Next"/>）。
    /// 番号は呼び出し側から書けない。
    /// </summary>
    void Save(RiskManagementSettings settings);

    /// <summary>
    /// FR-19, FR-20, ADR-0034 決定5 契機2, #1220, IADR-0511: 現在の**商品種別設定の改訂番号**（判定用・書き込まない）。
    /// 設定行が無い・番号を知らない版が書いた行は <c>null</c>（判定は無効へ倒れる。固定値と読まない）。
    /// </summary>
    long? GetProductTypesRevision();

    /// <summary>
    /// FR-19, FR-20, #1220, IADR-0511: verdict の**発行用**の改訂番号。番号が無ければ同じ設定行へ新しい番号を刻んでから返す
    /// （<see cref="ProductTypeSettingsRevision.Fresh"/>）。デプロイ直後でも設定を手で保存せずに verdict を発行できる。
    /// </summary>
    long EnsureProductTypesRevision();

    /// <summary>
    /// 🔴 FR-20, #1533, IADR-0616 決定 3: <b>保存されている</b>設定（段階の正を台帳へ寄せる継ぎ目の内側の値）。段 2 の継ぎ目は
    /// <see cref="GetCurrent"/> の段階を台帳の定義で置き換えるため、段階の変更の Before はこちらから取る。既定は <see cref="GetCurrent"/>。
    /// </summary>
    RiskManagementSettings GetStored() => GetCurrent();

    /// <summary>
    /// 🔴 FR-20, #1533, IADR-0616 決定 3: <b>段階を書く</b>保存。段 2 の継ぎ目は <see cref="Save"/> では保存値の段階を動かさないため、
    /// 段階を変えてよい保存はこちらを通す。本番の呼び手は <c>RiskSettingsService.UpdateStage</c>（＝遷移の受理）だけ。既定は <see cref="Save"/>。
    /// </summary>
    void SaveWithStage(RiskManagementSettings settings) => Save(settings);
}
