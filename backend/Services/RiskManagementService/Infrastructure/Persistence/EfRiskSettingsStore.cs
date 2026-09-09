using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, FR-17, IADR-0012: 設定ストアの EF 実装。単一行 JSON＋Version 列で楽観的排他制御する。
// 未設定時は TradingDefaults をシードして返す。DbContext は scoped のため本ストアも scoped。
public sealed class EfRiskSettingsStore(RiskManagementDbContext db) : IRiskSettingsStore
{
    public RiskManagementSettings GetCurrent()
    {
        var row = db.RiskSettings.Find(SingletonKeys.Id);
        if (row is not null)
        {
            return RiskSettingsSerialization.Deserialize(row.Json);
        }

        // 未設定: 既定値をシードする。同時初回リクエストが競合して一意制約違反になり得るため、
        // 失敗時は行を読み直し、**行があれば**他リクエストがシード済みとみなして返す
        // （冪等・レース窓を 500 にしない。#58 の是正を踏襲）。**行が無ければ再送出する**（下記 catch）。
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
        // FR-10, FR-17, #714, IADR-0317, IADR-0319: **競合の判定は例外の型ではなく「行が実在するか」で行う。**
        // 一意キー違反の例外型はプロバイダごとに違う（relational は DbUpdateException、EF Core の
        // InMemory は ArgumentException「An item with the same key has already been added」）。
        // 型を列挙すると取りこぼした側だけが素通りし、エンドポイントの例外フィルタで
        // ArgumentException が 400（＝利用者の要求が悪い）へ写像されるという遠い形で壊れる（#707 の実測）。
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
        {
            db.ChangeTracker.Clear();
            var seeded = db.RiskSettings.Find(SingletonKeys.Id);
            if (seeded is null)
            {
                // 行が生まれていない＝競合ではなく本物の保存失敗である。**握り潰さない**
                // （未永続の既定値を返すと「保存できていないのに既定値でリスク統制が動く」状態を黙って作る）。
                throw;
            }

            return RiskSettingsSerialization.Deserialize(seeded.Json);
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
