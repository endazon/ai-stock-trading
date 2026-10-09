using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Logging;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-08, UC-03, #1300, IADR-0526 決定 2〜4: 承認待ちの報告書の写し（ドラフト）を基盤の文書台帳に 1 件だけ持つ。
//
// 写しの文書 ID はプロセス内に覚える（再起動・別の複製では覚えていない）。覚えていなければ一覧から属性
// （project・periodKey・kind・reportState=draft）で探す。DB に列を足さない —— 一覧は承認待ちへ移るときと確定のときだけ引く。
//
// 🔴 属性の更新（PATCH /documents/{id}/metadata）は使わない。基盤の PATCH は属性の**全置換**で、露出のキーを送り忘れると
// ドラフトが黙って検索・RAG に出る。版は本文の先頭に書き、属性は作成のときの 1 回だけ（ReportKnowledgeMapper.DraftAttributesOf）。
//
// 🔴 一覧を引けないときは作らない（既にある写しが見えないまま作ると 2 件になる）。作成の結果が不明（タイムアウト・5xx）なら
// 覚えない —— 次の回が一覧で見つける。
public sealed class CatalogReportDraftKnowledgeCopy(
    IKnowledgeDocumentCatalog catalog,
    ReportDraftKnowledgeOptions options,
    ILogger<CatalogReportDraftKnowledgeCopy> logger) : IReportDraftKnowledgeCopy
{
    private readonly ConcurrentDictionary<(ReportKind Kind, string PeriodKey), Guid> _known = new();

    public async Task PublishAsync(TradingReport report, int version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!options.Enabled)
            return;

        try
        {
            await PublishCoreAsync(report, version, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "承認待ちの報告書の写しを KB へ保存できませんでした（PeriodKey={PeriodKey}・版={Version}。報告書は継続）。",
                LogSanitizer.Sanitize(report.PeriodKey), version);
        }
    }

    public async Task RemoveAsync(ReportKind kind, string periodKey, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(periodKey))
            return;

        try
        {
            await RemoveCoreAsync(kind, periodKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "確定した報告書のドラフトの写しを KB から消せませんでした（PeriodKey={PeriodKey}。索引されない写しが残ります）。",
                LogSanitizer.Sanitize(periodKey));
        }
    }

    private async Task PublishCoreAsync(TradingReport report, int version, CancellationToken cancellationToken)
    {
        var key = (report.Kind, report.PeriodKey);
        var body = ReportKnowledgeMapper.DraftBodyOf(report, version);

        // 基盤は 1 MB 超を 413 で拒否する。本文の無い写しを作らない（読めない写しは役に立たない）。
        if (KnowledgeBodyLimits.Exceeds(body))
        {
            logger.LogWarning("承認待ちの報告書の本文が上限（{MaxBytes} バイト）を超えるため KB へ写しません（PeriodKey={PeriodKey}・版={Version}）。",
                KnowledgeBodyLimits.MaxBytes, LogSanitizer.Sanitize(report.PeriodKey), version);
            return;
        }

        // 1. 覚えている写しの本文を差し替える。
        if (_known.TryGetValue(key, out var knownId))
        {
            var put = await catalog.PutBodyAsync(knownId, body, cancellationToken).ConfigureAwait(false);
            if (put.Outcome == KnowledgeCatalogOutcome.Succeeded)
                return;
            if (!put.IsNotFoundOrNotOwner)
            {
                LogWriteFailure("本文の差し替え", report.PeriodKey, version, put);
                return;
            }

            // 404＝消された（管理者・確定の後の再提示は起きないが念のため）。一覧から探し直す。
            _known.TryRemove(key, out _);
        }

        // 2. 一覧から探す。引けなければ作らない（重複を作らない）。
        var listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        if (listing.Outcome != KnowledgeCatalogOutcome.Succeeded)
        {
            logger.LogWarning("KB の文書一覧を引けないため、承認待ちの報告書の写しを保存しませんでした（PeriodKey={PeriodKey}・版={Version}）: {Reason}",
                LogSanitizer.Sanitize(report.PeriodKey), version, listing.Reason);
            return;
        }

        var existing = listing.Entries
            .Where(e => ReportKnowledgeMapper.IsDraftCopyOf(e, report.Kind, report.PeriodKey))
            .OrderByDescending(e => e.UpdatedAt)
            .ToList();

        if (existing.Count == 0)
        {
            // 3. 無ければ作る。
            var created = await catalog.CreateAsync(ReportKnowledgeMapper.ToDraftDocument(report, version), cancellationToken).ConfigureAwait(false);
            if (created is { Outcome: KnowledgeCatalogOutcome.Succeeded, DocumentId: { } createdId })
                _known[key] = createdId;
            else
                LogWriteFailure("作成", report.PeriodKey, version, created);
            return;
        }

        // 4. あれば一番新しい写しの本文を差し替え、残り（結果不明の作成で増えた写し）は消して 1 件にする。
        var target = existing[0];
        var replaced = await catalog.PutBodyAsync(target.DocumentId, body, cancellationToken).ConfigureAwait(false);
        if (replaced.Outcome == KnowledgeCatalogOutcome.Succeeded)
            _known[key] = target.DocumentId;
        else
            LogWriteFailure("本文の差し替え", report.PeriodKey, version, replaced);

        foreach (var extra in existing.Skip(1))
            await DeleteBestEffortAsync(extra.DocumentId, report.PeriodKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveCoreAsync(ReportKind kind, string periodKey, CancellationToken cancellationToken)
    {
        var key = (kind, periodKey);
        var targets = new HashSet<Guid>();
        if (_known.TryRemove(key, out var knownId))
            targets.Add(knownId);

        var listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        if (listing.Outcome == KnowledgeCatalogOutcome.Succeeded)
        {
            foreach (var entry in listing.Entries.Where(e => ReportKnowledgeMapper.IsDraftCopyOf(e, kind, periodKey)))
                targets.Add(entry.DocumentId);
        }
        else
        {
            logger.LogWarning("KB の文書一覧を引けないため、覚えている写しだけを消します（PeriodKey={PeriodKey}）: {Reason}",
                LogSanitizer.Sanitize(periodKey), listing.Reason);
        }

        foreach (var id in targets)
            await DeleteBestEffortAsync(id, periodKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteBestEffortAsync(Guid documentId, string periodKey, CancellationToken cancellationToken)
    {
        var deleted = await catalog.DeleteAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (deleted.Outcome == KnowledgeCatalogOutcome.Succeeded || deleted.IsNotFoundOrNotOwner)
            return; // 404 は既に無い（または別の主体のもので消せない）。どちらも打つ手は無い。

        logger.LogWarning("報告書のドラフトの写し {DocumentId} を KB から消せませんでした（PeriodKey={PeriodKey}・結果={Outcome}。索引されない写しが残ります）: {Reason}",
            documentId, LogSanitizer.Sanitize(periodKey), deleted.Outcome, deleted.Reason);
    }

    private void LogWriteFailure(string operation, string periodKey, int version, KnowledgeCatalogWriteResult result) =>
        logger.LogWarning("承認待ちの報告書の写しの{Operation}に失敗しました（PeriodKey={PeriodKey}・版={Version}・結果={Outcome}。報告書は継続）: {Reason}",
            operation, LogSanitizer.Sanitize(periodKey), version, result.Outcome, result.Reason);
}
