using System.Globalization;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using ReportService.Features.Reports.WatchlistProposal;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;

namespace ReportService.Features.Reports;

// NFR, NFR-06, FR-07, FR-13, FR-14, MSP:ADR-0029, MSP:ADR-0075, ADR-0047 決定 1〜3, IADR-0284 決定 5（段 5）, IADR-0449 決定 2・3, #753:
// 報告書の**所有者限定の読み取り**の gRPC 面（呼び出し元は Discord ボットだけ）。REST の OwnerOnly の 3 本と**同じ**サービス・判定を呼ぶ:
//   - `GET /reports/{periodKey}/review` → ReportAppService.GetReviewView（無ければ NOT_FOUND）
//   - `GET /reports/period-keys` → ReportAppService.ListPeriodKeys
//   - `GET /reports/policy-revisions/watchlist-proposal` → WatchlistProposalEndpoints.Lookup（400 / 404 / 409 を INVALID_ARGUMENT /
//     NOT_FOUND / FAILED_PRECONDITION へ）
//
// 🔴 門は **`GrpcOwnerOnly`**（trading-owner ∧ azp がボットの機密クライアント）。REST の OwnerOnly と同じく s2s には開かない。
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記）。REST 面は変えていない（入れ替え案の判定は 1 つに切り出しただけ）。
[Authorize(Policy = AiStockTradingAuthPolicies.GrpcOwnerOnly)]
public sealed class ReportOwnerReadGrpcService(
    ReportAppService reports,
    IPolicyRevisionLedger ledger,
    IReportStore store,
    ILoggerFactory loggerFactory)
    : Proto.ReportOwnerRead.ReportOwnerReadBase
{
    public override Task<Proto.GetReportReviewResponse> GetReportReview(
        Proto.GetReportReviewRequest request, ServerCallContext context) =>
        Reply(() =>
        {
            // REST の経路引数は空になり得ない。gRPC では空を「対象が無い」ではなく入力の誤りとして返す。
            if (string.IsNullOrWhiteSpace(request.PeriodKey))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "会話キー（periodKey）が必要です。"));

            return reports.GetReviewView(request.PeriodKey) is { } review
                ? ReportOwnerReadWireMapping.ToProto(review)
                : throw new RpcException(new Status(StatusCode.NotFound, $"報告書 {request.PeriodKey} は見つかりません。"));
        });

    public override Task<Proto.ListReportPeriodKeysResponse> ListReportPeriodKeys(
        Proto.ListReportPeriodKeysRequest request, ServerCallContext context) =>
        Reply(() =>
        {
            var response = new Proto.ListReportPeriodKeysResponse();
            response.Items.AddRange(reports.ListPeriodKeys().Select(ReportOwnerReadWireMapping.ToProto));
            return response;
        });

    public override Task<Proto.GetWatchlistProposalResponse> GetWatchlistProposal(
        Proto.GetWatchlistProposalRequest request, ServerCallContext context) =>
        Reply(() =>
        {
            var lookup = WatchlistProposalEndpoints.Lookup(request.PeriodKey, request.Version, ledger, store);
            return lookup.Outcome switch
            {
                WatchlistProposalEndpoints.ProposalLookupOutcome.Invalid =>
                    throw new RpcException(new Status(StatusCode.InvalidArgument, lookup.Error!)),
                WatchlistProposalEndpoints.ProposalLookupOutcome.NotProposal =>
                    throw new RpcException(new Status(StatusCode.NotFound, lookup.Error!)),
                WatchlistProposalEndpoints.ProposalLookupOutcome.NotConfirmedAtVersion =>
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, lookup.Error!)),
                _ => ReportOwnerReadWireMapping.ToProto(lookup.Proposal!),
            };
        });

    // REST の群のフィルタ（ReportEndpoints）と同じ分類: ArgumentException は 400 ＝ INVALID_ARGUMENT。
    // NFR-06, IADR-0503, IADR-0509, #1206, #1230: detail へ載せる文言は利用者へ見せる印（ClientVisibleArgument）のあるものだけ（印の無いものは固定文言・元の例外はログ）。
    private Task<T> Reply<T>(Func<T> handler)
    {
        try
        {
            return Task.FromResult(handler());
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                ClientFacingErrors.MessageFor(
                    e, loggerFactory.CreateLogger<ReportOwnerReadGrpcService>())));
        }
    }
}

// NFR, IADR-0449 決定 3: 送り手の型 → 線上表現（提供側の写し）。C# の null は設定しない（受け手は欠落を REST の null と同じに読む）。
public static class ReportOwnerReadWireMapping
{
    public static Proto.GetReportReviewResponse ToProto(ReportReviewView review)
    {
        ArgumentNullException.ThrowIfNull(review);

        var inputs = new Proto.UnsuppliedInputList();
        inputs.Names.AddRange(review.UnsuppliedInputs);
        return new Proto.GetReportReviewResponse { Version = review.Version, UnsuppliedInputs = inputs };
    }

    public static Proto.ReportPeriodKeyRow ToProto(ReportPeriodKeyItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var row = new Proto.ReportPeriodKeyRow { PeriodStart = item.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        if (item.PeriodKey is not null)
            row.PeriodKey = item.PeriodKey;
        return row;
    }

    public static Proto.GetWatchlistProposalResponse ToProto(WatchlistProposalView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var response = new Proto.GetWatchlistProposalResponse
        {
            AttemptId = view.AttemptId.ToString(),
            ReportVersion = view.ReportVersion,
            ApplyRecorded = view.ApplyRecorded,
        };
        if (view.PeriodKey is not null)
            response.PeriodKey = view.PeriodKey;

        // 🔴 NFR, IADR-0450, #753（PR #1069 の監査）: 保存済みの JSON の null 要素は**空の行**として運ぶ（NRE → INTERNAL にしない）。
        // 受け手は REST と同じに読む —— 変更の空の行（操作・銘柄の欠落）は案ごと解釈できない、スナップショットの空の行は一覧ごと「分からない」。
        response.Changes.AddRange(view.Changes.Select(c =>
        {
            var row = new Proto.WatchlistChangeRow();
            if (c is null) return row;
            if (c.Action is not null) row.Action = c.Action;
            if (c.Symbol is not null) row.Symbol = c.Symbol;
            if (c.Reason is not null) row.Reason = c.Reason;
            return row;
        }));

        // 🔴 Snapshot が null（案を作った時点の監視銘柄が分からない）なら入れ物ごと設定しない（空の一覧と区別する）。
        if (view.Snapshot is { } snapshot)
        {
            var rows = new Proto.WatchlistSnapshotRows();
            rows.Items.AddRange(snapshot.Select(e =>
            {
                var row = new Proto.WatchlistSnapshotRow();
                if (e is null) return row;
                if (e.Symbol is not null) row.Symbol = e.Symbol;
                if (e.Market is not null) row.Market = e.Market;
                return row;
            }));
            response.Snapshot = rows;
        }
        return response;
    }
}
