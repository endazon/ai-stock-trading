using ReportService.Common.Exceptions;
using ReportService.Features.Reports;
using ReportService.Domain;
using Microsoft.EntityFrameworkCore;

namespace ReportService.Infrastructure.Persistence;

// FR-06/07, IADR-0012/0024: 報告書ストアの EF 実装。PeriodKey ごとに 1 行＋Version 楽観排他。確定済みは不変。
// ADR-0068 決定2, #1373, IADR-0548: 確定した版の写しを別の表（report_confirmed_versions）に確定のたびに足し、最新の確定版はそこから選ぶ。
public sealed class EfReportStore(ReportDbContext db) : IReportStore
{
    public VersionedReport? Get(string periodKey)
    {
        var row = db.Reports.Find(periodKey);
        return row is null ? null : new VersionedReport(ToReport(row), row.Version);
    }

    public IReadOnlyList<TradingReport> List() =>
        [.. db.Reports.OrderByDescending(r => r.PeriodStart).Select(r => ToReport(r))];

    // FR-14, #843 項目1, IADR-0418: **SELECT で 2 列だけを射影する**（本文の列を読まない）。
    public IReadOnlyList<ReportPeriodKeyItem> ListPeriodKeys() =>
        [.. db.Reports
            .OrderByDescending(r => r.PeriodStart)
            .ThenByDescending(r => r.PeriodKey)
            .Select(r => new ReportPeriodKeyItem(r.PeriodKey, r.PeriodStart))];

    public int UpsertDraft(TradingReport report, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(report);

        var row = db.Reports.Find(report.PeriodKey);
        if (row is null)
        {
            if (expectedVersion != 0)
                throw new ReportConcurrencyException(report.PeriodKey, expectedVersion, 0);

            db.Reports.Add(new ReportRow
            {
                PeriodKey = report.PeriodKey,
                Kind = report.Kind,
                PeriodStart = report.PeriodStart,
                State = ReportState.Draft,
                // 新規/改訂ドラフトはレビュー局面 Drafting から始まる（IADR-0071 決定5）。
                ReviewState = ReviewState.Drafting,
                BasedOn = report.BasedOn,
                AssumptionsVersion = report.AssumptionsVersion,
                PolicySummary = report.PolicySummary,
                // FR-06, IADR-0115: 本文（Markdown）は自動生成経路のみが持つ。空なら NULL のまま。
                Body = string.IsNullOrEmpty(report.Body) ? null : report.Body,
                // #840, IADR-0352 決定 5: 欠けた入力の記録。空なら NULL のまま。
                UnsuppliedInputs = ReportInputs.Serialize(report.UnsuppliedInputs),
                ConfirmedAt = null,
                Version = 1,
            });
            try
            {
                db.SaveChanges();
                return 1;
            }
            // FR-06, FR-07, #714, IADR-0317, IADR-0319: **競合の判定は例外の型ではなく「行が実在するか」で行う。**
            // 一意キー違反の例外型はプロバイダごとに違う（relational は DbUpdateException、InMemory は
            // ArgumentException）ため、型を列挙すると取りこぼした側だけが素通りする。
            catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
            {
                db.ChangeTracker.Clear();
                var current = db.Reports.Find(report.PeriodKey);
                if (current is null)
                {
                    // 行が生まれていない＝競合ではなく本物の保存失敗である。**握り潰さない**
                    // （従来は current?.Version ?? 0 として「版 0 との競合」を発明し、本物の保存失敗を
                    //   409 相当の競合へ偽装していた）。
                    throw;
                }

                // 同一 PeriodKey の並行作成による一意制約違反。競合として扱う（他リクエストが作成済み）。
                throw new ReportConcurrencyException(report.PeriodKey, expectedVersion, current.Version);
            }
        }

        if (row.State == ReportState.Confirmed)
            throw new ReportAlreadyConfirmedException(report.PeriodKey);

        if (expectedVersion != row.Version)
            throw new ReportConcurrencyException(report.PeriodKey, expectedVersion, row.Version);

        row.Kind = report.Kind;
        row.PeriodStart = report.PeriodStart;
        row.BasedOn = report.BasedOn;
        row.AssumptionsVersion = report.AssumptionsVersion;
        row.PolicySummary = report.PolicySummary;
        // FR-06, IADR-0115: 本文は明示的に非空を渡されたときだけ差し替える。手動 upsert（PUT /reports/{periodKey}）は
        // 本文を持たないため、空で上書きして生成済みの Markdown を消さない。
        // #840, IADR-0352 決定 5: **未供給の記録は本文に従う**（本文を差し替えない改訂では記録も残す）。
        if (!string.IsNullOrEmpty(report.Body))
        {
            row.Body = report.Body;
            row.UnsuppliedInputs = ReportInputs.Serialize(report.UnsuppliedInputs);
        }

        row.State = ReportState.Draft;
        // 改訂（新ドラフト）はレビュー局面を Drafting へ戻す（対話的確定の Revise・IADR-0071 決定5）。
        row.ReviewState = ReviewState.Drafting;
        row.ConfirmedAt = null;
        row.Version += 1;
        try
        {
            db.SaveChanges();
        }
        catch
        {
            // FR-14, #1024, IADR-0432（PR #1026 の監査 5・再監査 F2、#1029 の追記・#1026 の差分監査 N1）: 保存に失敗したら、
            // **例外の種類を問わず**（並行更新・その他の DbUpdateException・接続の失敗で上がる NpgsqlException 等）変更を追跡したまま残さない
            // （台帳の SaveOrDetach と同じ規律）。Npgsql は接続を開くときの失敗を DbUpdateException に包まないため、型で絞るとその経路だけ
            // Modified の行が残る。残すと、同じ DbContext を共有する後続の書き込み（方針の改訂の台帳の SaveFailed 等）が
            // **失敗したはずの下書きを保存してしまう**か、この失敗した行をもう一度保存しようとして同じ例外で落ちる。
            db.ChangeTracker.Clear();
            throw;
        }

        return row.Version;
    }

    // FR-07, ADR-0068 決定2, #1373, IADR-0548 決定2: 確定の保存の試行回数の上限（通し番号の衝突で読み直す回数を含む）。
    internal const int MaxConfirmAttempts = 3;

    // FR-07, FR-15, ADR-0068 決定2, #1373, IADR-0548 決定2: 確定は「行の確定」と「確定した版の写しの追加」を
    // **1 回の SaveChanges（1 トランザクション）**で行う。写しの鍵（種別・通し番号）が衝突したら＝同じ種別の確定が並行したら、
    // 追跡を外して読み直し、決め直す（読み直した行が確定済みなら冪等の答えになる）。
    // 🔴 PR #1374 の AI レビュー 🟡: 読み直すのは**衝突（通し番号・版）だけ**である（ReportConfirmConflict）。それ以外の保存の失敗は
    // 再試行せずにそのまま投げる（接続断などを 3 回やり直して原因を見えにくくしない）。
    public ConfirmResult? Confirm(string periodKey, int expectedVersion, DateTimeOffset confirmedAt)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ConfirmOnce(periodKey, expectedVersion, confirmedAt);
            }
            catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
            {
                // 保存に失敗した変更を、衝突かどうかを問わず追跡したまま残さない（UpsertDraft と同じ規律）。
                db.ChangeTracker.Clear();
                if (!ReportConfirmConflict.IsConflict(ex, db.Database.ProviderName) || attempt >= MaxConfirmAttempts)
                    throw;
            }
        }
    }

    private ConfirmResult? ConfirmOnce(string periodKey, int expectedVersion, DateTimeOffset confirmedAt)
    {
        var row = db.Reports.Find(periodKey);
        if (row is null)
            return null;

        // 既に確定済みなら冪等（状態変化なし・イベント発行なし）。
        if (row.State == ReportState.Confirmed)
            return new ConfirmResult(ToReport(row), Transitioned: false, row.Version);

        if (expectedVersion != row.Version)
            throw new ReportConcurrencyException(periodKey, expectedVersion, row.Version);

        row.State = ReportState.Confirmed;
        // 承認（対話的確定の Approve）でレビュー局面は Confirmed（終端）へ（IADR-0071 決定5）。
        row.ReviewState = ReviewState.Confirmed;
        row.ConfirmedAt = confirmedAt;
        // #338, #310, INDEX 決定29: **本文の状態表記も確定へ揃える**（InMemoryReportStore と同じ規則）。
        // 本文は生成時に一度だけ組み立てられるため、ここで揃えないと確定済みの報告書が
        // frontmatter と YAML ブロックで「未確定」を名乗り続ける（取引判断は YAML を読む）。
        // **散文は作り直さない**——確定とは「その本文でよい」と承認した行為である。
        row.Body = ReportBodyStatus.MarkConfirmed(row.Body, confirmedAt);
        row.Version += 1;

        // ADR-0068 決定2, IADR-0548 決定1: 確定した版の写し（書き換えない）。通し番号は種別ごとの最大＋1。
        var sequence = (db.ReportConfirmedVersions.Where(v => v.Kind == row.Kind).Max(v => (int?)v.Sequence) ?? 0) + 1;
        db.ReportConfirmedVersions.Add(new ReportConfirmedVersionRow
        {
            Kind = row.Kind,
            Sequence = sequence,
            PeriodKey = row.PeriodKey,
            PeriodStart = row.PeriodStart,
            Version = row.Version,
            ConfirmedAt = confirmedAt,
            BasedOn = row.BasedOn,
            AssumptionsVersion = row.AssumptionsVersion,
            PolicySummary = row.PolicySummary,
            Body = row.Body,
            UnsuppliedInputs = row.UnsuppliedInputs,
        });
        db.SaveChanges();
        return new ConfirmResult(ToReport(row), Transitioned: true, row.Version);
    }

    public ReportReview? GetReview(string periodKey)
    {
        var row = db.Reports.Find(periodKey);
        return row is null ? null : new ReportReview(periodKey, row.ReviewState, row.Version);
    }

    public ReviewDecision? ApplyReview(string periodKey, ReviewCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var row = db.Reports.Find(periodKey);
        if (row is null)
            return null;

        var decision = ReportReviewStateMachine.Decide(new ReportReview(periodKey, row.ReviewState, row.Version), command);
        // 提示・差し戻しは内容不変（Version 不変）。遷移が受理されたときのみレビュー局面を更新する。
        if (decision.Accepted && decision.Transitioned)
        {
            row.ReviewState = decision.Review.State;
            db.SaveChanges();
        }

        return decision;
    }

    // FR-07, ADR-0068 決定2, #1373, IADR-0548 決定3: 最新の確定済み報告書は確定した版の履歴から二段で選ぶ。
    public VersionedReport? GetLatestConfirmed(ReportKind kind) =>
        SelectLatest(db.ReportConfirmedVersions.Where(v => v.Kind == kind));

    // FR-15, FR-07, ADR-0033 決定2, ADR-0068 決定3, #1308, #1373, IADR-0528 決定3, IADR-0548 決定4:
    // at の時点で確定していた版（ConfirmedAt ≤ at）**だけ**を対象に、同じ二段で選ぶ。at より後に確定した版は使わない。
    // 🔴 Npgsql の timestamptz は UTC の値しか受けないため、比較の値を UTC へ寄せる（時刻そのものは変わらない）。
    public VersionedReport? GetLatestConfirmedAsOf(ReportKind kind, DateTimeOffset at)
    {
        var atUtc = at.ToUniversalTime();
        return SelectLatest(db.ReportConfirmedVersions.Where(v => v.Kind == kind && v.ConfirmedAt <= atUtc));
    }

    // ADR-0068 決定2, IADR-0548 決定3: 1 段目は開始日が最大（同じ開始日は会話キーの降順）の期間キー、2 段目はその中で
    // 確定時刻が最新、同時刻は版番号の大きい方。(開始日, 会話キー, 確定時刻, 版) の辞書順の先頭はこの二段の答えと一致する
    // ——先頭 2 つで期間キーが決まり、残り 2 つはその期間キーの中だけで比べられる（確定時刻で期間キーをまたいで並べない）。
    private static VersionedReport? SelectLatest(IQueryable<ReportConfirmedVersionRow> versions)
    {
        var v = versions
            .OrderByDescending(x => x.PeriodStart)
            .ThenByDescending(x => x.PeriodKey)
            .ThenByDescending(x => x.ConfirmedAt)
            .ThenByDescending(x => x.Version)
            .FirstOrDefault();
        return v is null ? null : new VersionedReport(ToReport(v), v.Version);
    }

    private static TradingReport ToReport(ReportConfirmedVersionRow v) => new()
    {
        PeriodKey = v.PeriodKey,
        Kind = v.Kind,
        PeriodStart = v.PeriodStart,
        State = ReportState.Confirmed,
        BasedOn = v.BasedOn,
        AssumptionsVersion = v.AssumptionsVersion,
        PolicySummary = v.PolicySummary,
        Body = v.Body ?? string.Empty,
        UnsuppliedInputs = ReportInputs.Parse(v.UnsuppliedInputs),
        ConfirmedAt = v.ConfirmedAt,
    };

    private static TradingReport ToReport(ReportRow r) => new()
    {
        PeriodKey = r.PeriodKey,
        Kind = r.Kind,
        PeriodStart = r.PeriodStart,
        State = r.State,
        BasedOn = r.BasedOn,
        AssumptionsVersion = r.AssumptionsVersion,
        PolicySummary = r.PolicySummary,
        Body = r.Body ?? string.Empty,
        UnsuppliedInputs = ReportInputs.Parse(r.UnsuppliedInputs),
        ConfirmedAt = r.ConfirmedAt,
    };
}
