using ConfigurationService.Common.Exceptions;
using ConfigurationService.Features.Assumptions;
using ConfigurationService.Common.Abstractions;
using AiStockTrading.Shared.Kernel.Trading;
using Microsoft.EntityFrameworkCore;

namespace ConfigurationService.Infrastructure.Persistence;

// FR-17, IADR-0012/0021: 全体前提条件ストアの EF 実装。単一行 JSON＋Version 楽観排他。未設定時は既定シード。
public sealed class EfAssumptionsStore(ConfigurationDbContext db) : IAssumptionsStore
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
        // FR-17, #714, IADR-0317, IADR-0319: **競合の判定は例外の型ではなく「行が実在するか」で行う。**
        // 一意キー違反の例外型はプロバイダごとに違う（relational は DbUpdateException、EF Core の
        // InMemory は ArgumentException「An item with the same key has already been added」）。
        // 型を列挙すると取りこぼした側だけが素通りし、例外フィルタで 400（＝利用者の要求が悪い）という
        // 嘘の説明になる（#707 の実測）。
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
        {
            db.ChangeTracker.Clear();
            var seeded = db.Assumptions.Find(SingletonKeys.Id);
            if (seeded is null)
            {
                // 行が生まれていない＝競合ではなく本物の保存失敗である。**握り潰さない**
                // （未永続の既定値を返すと「保存できていないのに既定の前提条件で動く」状態を黙って作る）。
                throw;
            }

            return new VersionedAssumptions(AssumptionsSerialization.Deserialize(seeded.Json), seeded.Version);
        }
    }

    public int Save(TradingAssumptions assumptions, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(assumptions);

        var row = db.Assumptions.Find(SingletonKeys.Id);
        if (row is null)
        {
            // 未シードなら既定を v1 でシードしてから排他チェックする。同時初回 PUT が競合して一意制約違反になり得るため、
            // 失敗時は他リクエストがシード済みとみなして読み直す（GetCurrent と同じ #58 是正パターン。500 にしない）。
            db.Assumptions.Add(new AssumptionsRow
            {
                Id = SingletonKeys.Id,
                Json = AssumptionsSerialization.Serialize(TradingAssumptionsDefaults.Create()),
                Version = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            try
            {
                db.SaveChanges();
            }
            // #714, IADR-0317, IADR-0319: GetCurrent と同じ規律 —— 判定は例外の型ではなく行の実在で行う。
            catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
            {
                db.ChangeTracker.Clear();
                if (db.Assumptions.Find(SingletonKeys.Id) is null)
                {
                    // 行が生まれていない＝競合ではなく本物の保存失敗である。**握り潰さない**
                    // （従来は握り潰した後に「前提条件のシードに失敗しました」という別の話へ化け、
                    //   原因〔何が保存を失敗させたのか〕が失われていた）。
                    throw;
                }
            }

            row = db.Assumptions.Find(SingletonKeys.Id)
                ?? throw new InvalidOperationException("前提条件のシードに失敗しました。");
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
