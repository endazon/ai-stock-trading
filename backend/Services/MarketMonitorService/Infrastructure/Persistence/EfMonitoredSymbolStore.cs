using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketMonitorService.Infrastructure.Persistence;

// FR-03, FR-13, IADR-0012 踏襲: 監視設定ストアの EF 実装。単一行 JSON＋Version 楽観排他。
// 未設定時は MonitorDefaults をシードして返す。
// #286, IADR-0281: 「未設定」は構成（Monitor:SeedSymbols・seedOptions）でシードする。空既定（seedOptions
// 未指定 or SeedSymbols 未投入）は従来どおり空でシードし現行挙動を保つ。「利用者が明示的に全削除した」状態
// （行の ClearedByUserAt）は区別し、構成シードで巻き戻さない（IADR-0095 の設計思想を踏襲）。
public sealed class EfMonitoredSymbolStore(MarketMonitorDbContext db, MonitorSeedOptions? seedOptions = null)
    : IMonitoredSymbolStore
{
    public MarketMonitorSettings GetSettings()
    {
        var row = db.MonitorSettings.Find(SingletonKeys.Id);
        if (row is null)
        {
            // 真の未設定: 構成シードを適用する。同時初回リクエストが競合して一意制約違反になり得るため、
            // 失敗時は他リクエストがシード済みとみなして読み直す（冪等・レース窓を 500 にしない）。
            var seeded = MonitorDefaults.CreateSettings(ResolveSeedSymbols());
            var now = DateTimeOffset.UtcNow;
            db.MonitorSettings.Add(new MonitorSettingsRow
            {
                Id = SingletonKeys.Id,
                Json = MonitorSettingsSerialization.Serialize(seeded),
                Version = 1,
                UpdatedAt = now,
                SeededAt = now,
                ClearedByUserAt = null,
            });
            try
            {
                db.SaveChanges();
                return seeded;
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                var raced = db.MonitorSettings.Find(SingletonKeys.Id);
                return raced is not null ? MonitorSettingsSerialization.Deserialize(raced.Json) : seeded;
            }
        }

        var settings = MonitorSettingsSerialization.Deserialize(row.Json);
        if (settings.MonitoredSymbols.Count > 0 || row.ClearedByUserAt is not null)
        {
            // 既存の監視銘柄がある、または利用者が明示的に全削除した記録がある＝利用者の意思が確定済み。
            // 触らずそのまま返す。
            return settings;
        }

        // 空だが ClearedByUserAt が無い＝「未設定」と同視する（本機能導入前に空で作られた既存行の後方互換を
        // 兼ねる）。構成シードが空ならホットパスで無意味な書き込みをしない（現行挙動のまま空を返す）。
        var seedSymbols = ResolveSeedSymbols();
        if (seedSymbols.Count == 0)
        {
            return settings;
        }

        var reseeded = MonitorDefaults.CreateSettings(seedSymbols);
        row.Json = MonitorSettingsSerialization.Serialize(reseeded);
        row.Version += 1;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.SeededAt = DateTimeOffset.UtcNow;
        try
        {
            db.SaveChanges();
            return reseeded;
        }
        catch (DbUpdateConcurrencyException)
        {
            // 同時に他リクエスト（定時ポーリング・別 HTTP 呼び出し）が同じ再シードや Add/Remove を
            // 先に確定させた場合の Version 競合。row is null 分岐と同じ規律で「他方が確定させた最新を
            // 読み直す」（冪等・呼び出し側へ 500 を波及させない）。
            db.ChangeTracker.Clear();
            var raced = db.MonitorSettings.Find(SingletonKeys.Id);
            return raced is not null ? MonitorSettingsSerialization.Deserialize(raced.Json) : settings;
        }
    }

    public void Save(MarketMonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var row = db.MonitorSettings.Find(SingletonKeys.Id);
        var now = DateTimeOffset.UtcNow;
        var nowEmpty = settings.MonitoredSymbols.Count == 0;
        if (row is null)
        {
            db.MonitorSettings.Add(new MonitorSettingsRow
            {
                Id = SingletonKeys.Id,
                Json = MonitorSettingsSerialization.Serialize(settings),
                Version = 1,
                UpdatedAt = now,
                // GetSettings を経ずに直接 Save が最初に呼ばれた場合も、空での保存は利用者の意思とみなす
                // （安全側。実運用では GetSettings が先に行を作るため通常はここへ到達しない）。
                ClearedByUserAt = nowEmpty ? now : null,
            });
        }
        else
        {
            var previousCount = MonitorSettingsSerialization.Deserialize(row.Json).MonitoredSymbols.Count;
            // IADR-0012: Version をインクリメントし、EF の並行トークンでロストアップデートを防ぐ。
            row.Json = MonitorSettingsSerialization.Serialize(settings);
            row.Version += 1;
            row.UpdatedAt = now;

            // #286, IADR-0281: 「利用者が今まさに全削除した」遷移（非空→空）だけを記録する。既に空のまま
            // 空で保存し直す場合（変動閾値・クールダウンの部分更新等）はタイムスタンプを上書きしない。
            // 1 件でも監視銘柄があれば「削除された」状態ではないので解除する（再追加で復活）。
            if (nowEmpty && previousCount > 0)
            {
                row.ClearedByUserAt = now;
            }
            else if (!nowEmpty)
            {
                row.ClearedByUserAt = null;
            }
        }

        db.SaveChanges();
    }

    private IReadOnlyCollection<MonitoredSymbol> ResolveSeedSymbols() =>
        (seedOptions ?? new MonitorSeedOptions()).ToMonitoredSymbols();
}
