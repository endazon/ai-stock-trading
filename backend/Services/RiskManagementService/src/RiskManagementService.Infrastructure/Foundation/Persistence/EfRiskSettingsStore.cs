using RiskManagementService.Application.Ports;
using RiskManagementService.Domain;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, FR-17, IADR-0012: 設定ストアの EF 実装。単一行 JSON＋Version 列で楽観的排他制御する。
// 未設定時は TradingDefaults をシードして返す。DbContext は scoped のため本ストアも scoped。
internal sealed class EfRiskSettingsStore(RiskManagementDbContext db) : IRiskSettingsStore
{
    public RiskManagementSettings GetCurrent()
    {
        var row = db.RiskSettings.Find(SingletonKeys.Id);
        if (row is not null)
        {
            return RiskSettingsSerialization.Deserialize(row.Json);
        }

        // 未設定: 既定値をシードする。同時初回リクエストが競合して一意制約違反になり得るため、
        // 失敗時は他リクエストがシード済みとみなして読み直す（冪等・レース窓を 500 にしない。#58 の是正を踏襲）。
        var defaults = TradingDefaults.CreateSettings();
        db.RiskSettings.Add(new RiskSettingsRow
        {
            Id = SingletonKeys.Id,
            Json = RiskSettingsSerialization.Serialize(defaults),
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            db.SaveChanges();
            return defaults;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var seeded = db.RiskSettings.Find(SingletonKeys.Id);
            return seeded is not null ? RiskSettingsSerialization.Deserialize(seeded.Json) : defaults;
        }
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
