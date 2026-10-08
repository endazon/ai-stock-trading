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

        // FR-19, FR-20, ADR-0034 決定5 契機2, #1220, IADR-0511: **商品種別設定の改訂番号はストアが進める。**
        // 保存の直前の行と集合を比べ、違えば +1 して同じ JSON 行へ書く。
        // 呼び出し側の設定値は番号を持たないため、`with` で運んだ古い値が番号を巻き戻すことは無い。
        // 読み込んだ版と DB の版が食い違えば並行トークンが保存ごと止めるため、番号の加算も失われない。
        //
        // 🔴 **行が無い・番号のキーが無い行（番号を知らない版が書いた行）は、新しい行の版（Version）を番号にする**
        // （IADR-0511 決定2 の 2026-10-08 改訂）。番号は常に「番号 ≦ 行の版」を保つため（版は書き込みのたびに 1 進み、
        // 番号は高々 1 進む）、キーを落とした書き込み（切り戻した旧版の保存）の後の版は、それまでに verdict が
        // 写し取ったどの番号よりも大きい。**キーの無い行が、記録済みの番号と一致することは決して無い。**
        var newVersion = row is null ? 1 : row.Version + 1;
        var previousRevision = row is null ? null : RiskSettingsSerialization.ReadProductTypesRevision(row.Json);
        var revision = previousRevision is { } known
            ? ProductTypeSettingsRevision.Next(
                RiskSettingsSerialization.Deserialize(row!.Json).Guard.EnabledProductTypes,
                settings.Guard.EnabledProductTypes,
                known)
            : ProductTypeSettingsRevision.Fresh(newVersion);

        if (row is null)
        {
            db.RiskSettings.Add(new RiskSettingsRow
            {
                Id = SingletonKeys.Id,
                Json = RiskSettingsSerialization.Serialize(settings, revision),
                Version = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            // IADR-0012: Version をインクリメントする。EF の並行トークン（IsConcurrencyToken）により、
            // 読み込んだ版と DB の現在版が一致しない場合は SaveChanges が DbUpdateConcurrencyException を投げ、
            // ロストアップデートを防ぐ（Slice A レビュー指摘への対応）。
            row.Json = RiskSettingsSerialization.Serialize(settings, revision);
            row.Version = newVersion;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        db.SaveChanges();
    }

    // FR-19, FR-20, #1220, IADR-0511: 判定用の読み取り。**書き込まない。**
    // 行が無い・キーの無い行は null（＝判定材料なし。verdict は ProductTypesUnknown へ倒れる）。
    // キーの無い行を固定値（0 や 1）と読むと、切り戻した旧版がキーを落とした行が、その固定値で発行した verdict と
    // 一致してしまう（2026-10-08 の監査 F1）。
    public long? GetProductTypesRevision()
    {
        var row = db.RiskSettings.Find(SingletonKeys.Id);
        return row is null ? null : RiskSettingsSerialization.ReadProductTypesRevision(row.Json);
    }

    // FR-19, FR-20, #1220, IADR-0511: verdict の発行用。番号が無ければ**同じ行へ刻んでから**返す。
    // 行が無ければ既定値をシードし（GetCurrent。版 1・番号 1）、キーの無い行なら版を 1 進めてその版を番号として書く
    // （Save と同じ「番号 ≦ 版」の規律）。デプロイ直後でも設定を手で保存せずに verdict を発行できる。
    public long EnsureProductTypesRevision()
    {
        GetCurrent();
        var row = db.RiskSettings.Find(SingletonKeys.Id)
            ?? throw new InvalidOperationException("リスク管理設定の行をシードできませんでした。");
        if (RiskSettingsSerialization.ReadProductTypesRevision(row.Json) is { } known)
        {
            return known;
        }

        var newVersion = row.Version + 1;
        var revision = ProductTypeSettingsRevision.Fresh(newVersion);
        row.Json = RiskSettingsSerialization.Serialize(RiskSettingsSerialization.Deserialize(row.Json), revision);
        row.Version = newVersion;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
        return revision;
    }
}
