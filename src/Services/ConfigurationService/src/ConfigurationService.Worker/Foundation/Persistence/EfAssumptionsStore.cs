using AiStockTrading.Configuration.Application;
using AiStockTrading.Configuration.Application.Ports;
using AiStockTrading.Configuration.Application.State;
using AiStockTrading.Configuration.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.Configuration.Worker.Foundation.Persistence;

// FR-17, IADR-0012/0021: 全体前提条件ストアの EF 実装。単一行 JSON＋Version 楽観排他。未設定時は既定シード。
internal sealed class EfAssumptionsStore(ConfigurationDbContext db) : IAssumptionsStore
{
    public VersionedAssumptions GetCurrent()
    {
        var row = db.Assumptions.Find(SingletonKeys.Id);
        if (row is not null)
            return new VersionedAssumptions(AssumptionsSerialization.Deserialize(row.Json), row.Version);

        // 未設定: 既定値をシードする。同時初回リクエストの一意制約違反は他リクエストのシード済みとみなし読み直す
        // （冪等・レース窓を 500 にしない。#58 の是正を踏襲）。
        var defaults = TradingAssumptionsDefaults.Create();
        db.Assumptions.Add(new AssumptionsRow
        {
            Id = SingletonKeys.Id,
            Json = AssumptionsSerialization.Serialize(defaults),
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            db.SaveChanges();
            return new VersionedAssumptions(defaults, 1);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var seeded = db.Assumptions.Find(SingletonKeys.Id);
            return seeded is not null
                ? new VersionedAssumptions(AssumptionsSerialization.Deserialize(seeded.Json), seeded.Version)
                : new VersionedAssumptions(defaults, 1);
        }
    }

    public int Save(TradingAssumptions assumptions, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(assumptions);

        var row = db.Assumptions.Find(SingletonKeys.Id);
        if (row is null)
        {
            // 未シードなら既定を v1 でシードしてから排他チェックする。
            row = new AssumptionsRow
            {
                Id = SingletonKeys.Id,
                Json = AssumptionsSerialization.Serialize(TradingAssumptionsDefaults.Create()),
                Version = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Assumptions.Add(row);
            db.SaveChanges();
        }

        if (expectedVersion != row.Version)
            throw new AssumptionsConcurrencyException(expectedVersion, row.Version);

        // Version をインクリメント。IsConcurrencyToken により並行更新（別コンテキスト）は DbUpdateConcurrencyException で弾く。
        row.Json = AssumptionsSerialization.Serialize(assumptions);
        row.Version += 1;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
        return row.Version;
    }
}
