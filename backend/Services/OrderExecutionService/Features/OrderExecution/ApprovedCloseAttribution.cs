using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace OrderExecutionService.Features.OrderExecution;

// 🔴 FR-10, ADR-0040 決定1（S1）, #1330, IADR-0534 決定1・決定2: 保護の突き合わせ（ProtectiveStopNetting.Confirm）が確定した減少を、
// **承認済みの決済（取引判断の利確・損切り・利用者の手仕舞い・維持率割れの自動縮小）の約定で説明できる分**と
// **説明できない分（外部要因）**に分ける。数量（帳簿の減らし方）には一切関わらない——分類と通知の文面だけを決める。
//
// 窓の両端で判定する（規則 11。仕様書 20261013_1330 の表で 3 つの形を実測して選んだ）:
//   - 前の端: 決済を**送った**時刻（予約の ReservedAt）が、その減少を**最初に観測した**巡回の時刻以前であること。
//     観測より後に送った決済は、観測した減少の原因になり得ない（形 (a)「後の端だけ」はこれを誤って数える＝P−）。
//   - 後の端: 約定が**確定の巡回までに**発注の記録へ載っていること（未帰属の約定 = FilledQuantity − ProtectionReleasedQuantity）。
//     moomoo では建玉照会が先に約定を映し、約定追跡が記録を Filled へ直すのは最初の観測の後になり得る
//     （形 (b)「前の端だけ」はこれを取りこぼす＝P+）。記録の ExecutedAt は約定追跡が**照会した時刻**で上書きするので使わない。
//
// 🔴 **迷ったら外部要因へ倒す。** 外部要因を「承認済みの決済」と誤って呼ぶと人手で確かめるべき減少を隠すが、
// 逆は確認の手間で済む。予約ストアが無い・記録を読めない・予約が見つからない・出どころの無い（旧い・保護の機構の）決済は数えない。
internal sealed class ApprovedCloseAttribution(
    string symbol,
    Market market,
    TradeSide entrySide,
    IExecutedOrderStore store,
    IOrderReservationStore? reservations,
    ILogger? logger)
{
    /// <summary>1 回の確定で読む承認済みの決済の上限（古い順）。同じ銘柄・方向に未帰属の約定がこれほど溜まることは想定しない。</summary>
    public const int CandidateScanLimit = 50;

    private List<Candidate>? _candidates;
    private string? _unavailableReason;

    /// <summary>
    /// 確定する減少 <paramref name="reduction"/> 株のうち、承認済みの決済で説明できる分を<b>見積もる</b>（まだ帰属させない）。
    /// 帳簿を書けたら <see cref="Commit"/> で確定する。<paramref name="observedAt"/> はその減少を最初に観測した時刻（前の端）。
    /// </summary>
    public ApprovedCloseClassification Classify(ProtectiveStopOrder row, int reduction, DateTimeOffset observedAt)
    {
        if (reduction <= 0)
            return new ApprovedCloseClassification([], 0, "減少なし");

        var candidates = Load();
        if (candidates is null)
            return new ApprovedCloseClassification([], reduction, _unavailableReason!);

        var withFills = candidates.Where(c => c.Available > 0).ToList();
        if (withFills.Count == 0)
        {
            return new ApprovedCloseClassification(
                [], reduction, "外部要因: 未帰属の約定を持つ承認済みの決済が無い（取引判断・利用者の手仕舞い・自動縮小のいずれの約定でも説明できない）");
        }

        // 前の端: 送ったのが最初の観測以前。行の作成（エントリーの武装）より前に送った決済はこの建玉の決済ではない。
        var eligible = withFills
            .Where(c => c.ReservedAt is { } at && at <= observedAt && at >= row.CreatedAt)
            .OrderBy(c => c.ReservedAt)
            .ThenBy(c => c.Record.OrderId, StringComparer.Ordinal)
            .ToList();
        if (eligible.Count == 0)
        {
            var missing = withFills.Count(c => c.ReservedAt is null);
            return new ApprovedCloseClassification(
                [], reduction,
                $"外部要因: 未帰属の約定を持つ承認済みの決済 {withFills.Count} 件はいずれも、この減少を最初に観測した時刻"
                + $"（{observedAt:O}）より後に送られたか、この保護記録の作成より前に送られたもの"
                + (missing > 0 ? $"（うち {missing} 件は予約が見つからず送った時刻が分からない）" : string.Empty));
        }

        var parts = new List<ApprovedClosePart>();
        var remaining = reduction;
        foreach (var candidate in eligible)
        {
            if (remaining <= 0)
                break;

            var take = Math.Min(remaining, candidate.Available);
            parts.Add(new ApprovedClosePart(candidate.Record, take));
            remaining -= take;
        }

        var explained = reduction - remaining;
        var reason = remaining == 0
            ? $"承認済みの決済: 減少 {reduction} 株はすべて、この減少を最初に観測した時刻（{observedAt:O}）以前に送った決済の約定で説明できる"
            : $"一部が承認済みの決済: 減少 {reduction} 株のうち {explained} 株は、この減少を最初に観測した時刻（{observedAt:O}）以前に送った決済の"
                + $"約定で説明でき、残り {remaining} 株は説明できない（外部要因）";
        return new ApprovedCloseClassification(parts, remaining, reason);
    }

    /// <summary>
    /// 帳簿を書けた確定の帰属を確定する: 同じ呼び出しの中の残りを減らし、記録へ印（帰属させた株数）を付ける。
    /// 印を書けなくても帳簿は既に正しい（次の確定が同じ約定を証拠に使い得る＝文面だけの誤り）。
    /// </summary>
    public void Commit(ApprovedCloseClassification classification)
    {
        if (_candidates is null)
            return;

        foreach (var part in classification.Parts)
        {
            var candidate = _candidates.First(c => c.Record.OrderId == part.Close.OrderId);
            candidate.Released += part.Quantity;
            try
            {
                if (!store.MarkProtectionReleased(candidate.Record.OrderId, candidate.Released))
                {
                    logger?.LogWarning(
                        "承認済みの決済の約定を保護記録の縮小へ帰属させた印を書けませんでした（記録が無い・既に進んでいる）。"
                        + "保護の数量は正しく、通知の分類だけが次の確定で誤り得ます: DecisionId={DecisionId} OrderId={OrderId} 帰属させた株数={Released}",
                        candidate.Record.DecisionId, candidate.Record.OrderId, candidate.Released);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex,
                    "承認済みの決済の約定を保護記録の縮小へ帰属させた印を書けませんでした。保護の数量は正しく、通知の分類だけが次の確定で誤り得ます: "
                    + "DecisionId={DecisionId} OrderId={OrderId}",
                    candidate.Record.DecisionId, candidate.Record.OrderId);
            }
        }
    }

    // 承認済みの決済（決済の方向）を 1 回だけ読む。読めなければ null（理由を残す）。
    private List<Candidate>? Load()
    {
        if (_candidates is not null || _unavailableReason is not null)
            return _candidates;

        if (reservations is null)
        {
            _unavailableReason = "外部要因: 承認済みの決済を送った時刻を引けない構成（予約ストアが渡されていない）";
            return null;
        }

        var closeSide = entrySide == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
        try
        {
            _candidates = store.FindApprovedClosesWithUnreleasedFills(symbol, market, closeSide, CandidateScanLimit)
                .Select(r => new Candidate(r, reservations.Find(r.DecisionId)?.ReservedAt))
                .ToList();
            return _candidates;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex,
                "承認済みの決済の記録を読めませんでした（確定した減少は外部要因として知らせます）: 銘柄={Symbol} 市場={Market}",
                symbol, market);
            _unavailableReason = "外部要因: 承認済みの決済の記録を読めなかった（例外）";
            return null;
        }
    }

    private sealed class Candidate(ExecutionRecord record, DateTimeOffset? reservedAt)
    {
        public ExecutionRecord Record { get; } = record;

        public DateTimeOffset? ReservedAt { get; } = reservedAt;

        // 帰属させた株数（記録の印 ＋ この呼び出しで帰属させた分）。
        public int Released { get; set; } = Math.Max(0, record.ProtectionReleasedQuantity);

        public int Available => Math.Max(0, Record.FilledQuantity - Released);
    }
}

/// <summary>🔴 #1330, IADR-0534: 確定した減少の分類（承認済みの決済ごとの内訳・外部要因の株数・理由）。</summary>
internal sealed record ApprovedCloseClassification(
    IReadOnlyList<ApprovedClosePart> Parts, int External, string Reason);

/// <summary>🔴 #1330, IADR-0534: 承認済みの決済 1 件へ帰属させた株数。</summary>
internal sealed record ApprovedClosePart(ExecutionRecord Close, int Quantity);
