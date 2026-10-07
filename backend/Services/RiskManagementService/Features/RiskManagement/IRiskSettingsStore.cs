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
    /// FR-19, FR-20, ADR-0034 決定5 契機2, #1220, IADR-0511: 現在の**商品種別設定の改訂番号**。
    /// 空売り実弾解禁の verdict が発行時に写し取り、評価時に突き合わせる。設定行が無い・番号を持たない旧行は
    /// <see cref="ProductTypeSettingsRevision.Initial"/>。
    /// </summary>
    long GetProductTypesRevision();
}
