using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Worker.Foundation.Persistence;

// FR-10, FR-17, IADR-0012: 設定ストアの EF 実装。単一行 JSON＋Version 列で楽観的排他制御する。
// 未設定時は TradingDefaults をシードして返す。DbContext は scoped のため本ストアも scoped。
internal sealed class EfRiskSettingsStore(RiskManagementDbContext db) : IRiskSettingsStore
{
    public RiskManagementSettings GetCurrent()
    {
        var row = db.RiskSettings.Find(SingletonKeys.Id);
        if (row is null)
        {
            var defaults = TradingDefaults.CreateSettings();
            db.RiskSettings.Add(new RiskSettingsRow
            {
                Id = SingletonKeys.Id,
                Json = RiskSettingsSerialization.Serialize(defaults),
                Version = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
            return defaults;
        }

        return RiskSettingsSerialization.Deserialize(row.Json);
    }

    public void Save(RiskManagementSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var row = db.RiskSettings.Find(SingletonKeys.Id);
        if (row is null)
        {
            db.RiskSettings.Add(new RiskSettingsRow
            {
                Id = SingletonKeys.Id,
                Json = RiskSettingsSerialization.Serialize(settings),
                Version = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            // IADR-0012: Version をインクリメントする。EF の並行トークン（IsConcurrencyToken）により、
            // 読み込んだ版と DB の現在版が一致しない場合は SaveChanges が DbUpdateConcurrencyException を投げ、
            // ロストアップデートを防ぐ（Slice A レビュー指摘への対応）。
            row.Json = RiskSettingsSerialization.Serialize(settings);
            row.Version += 1;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        db.SaveChanges();
    }
}
