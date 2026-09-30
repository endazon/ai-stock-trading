using System.Globalization;
using OrderExecutionService.Features.OrderExecution.ProbeKLineQuota;
using OrderExecutionService.Features.OrderExecution.QueryDailyBars;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-04, FR-15, ADR-0048 決定 2・3, ADR-0023 決定 5, #1118, IADR-0467 決定 1: 日足の照会（IDailyKLineSource）の moomoo 実装。
// 相場だけのクライアント（IADR-0464 の MMApiMoomooKLineProbeClient＝IKLineQuotaQuery。発注の接続を作らず口座も選ばない）を
// **遅延生成して使い回し**、例外（接続失敗・切断・打ち切り）で捨てて次の要求で作り直す。
//
// 🔴 相場だけのクライアントは切断後に撃たない（IADR-0464 決定 3。一発撃ちの検証口のための性質）。常駐で使うため、作り直しはこの包みで行う。
// 🔴 **前復権に固定する**（IDailyKLineSource の注記）。**日足の要求は 1 呼び出しにつき 1 回**。続きの鍵が返れば不完全として失敗にする
// （判断は 45 暦日＋α を求めるので 1 ページ＝最大 1,000 本に収まる。続きがあるのは想定外であり、途中までの足で比を作らない）。
// 🔴 空白足（取引の無い日）と、欄の欠けた足・0 以下の価格・負の出来高の足は捨てる（0 を値として渡さない）。
// 取得の直後に枠を照会する（詳細なし。枠は減らない）。照会の失敗は枠を不明（null）にするだけで、取得の結果は変えない。
// 呼び出しは DailyBarsQueryService が直列化する。念のためここでも 1 本ずつに絞る。
public sealed class MoomooDailyKLineSource(Func<IKLineQuotaQuery> connect) : IDailyKLineSource, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IKLineQuotaQuery? _client;
    private bool _disposed;

    public async Task<DailyKLineFetch> FetchForwardAdjustedAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var client = _client ??= connect();

            DailyKLineReading reading;
            try
            {
                reading = await client
                    .RequestDailyKLinesAsync(new DailyKLineProbeRequest(symbol, KLineRehab.Forward, from, to), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 接続・切断・打ち切り・キャンセル: この接続は捨てる（応答待ちが取り残されたままの接続を使い回さない）。
                Reset();
                throw;
            }

            var (used, remaining) = await QueryQuotaSafeAsync(client, cancellationToken).ConfigureAwait(false);

            if (!reading.Succeeded)
                return new DailyKLineFetch(false, [], $"ret-{reading.RetType.ToString(CultureInfo.InvariantCulture)}", used, remaining);
            if (reading.HasMore)
                return new DailyKLineFetch(false, [], "has-more", used, remaining);

            return new DailyKLineFetch(true, MapBars(reading.KLines), null, used, remaining);
        }
        finally
        {
            _gate.Release();
        }
    }

    // 応答の足を日足へ写す。空白足・欄の欠け・0 以下の価格・負の出来高は捨てる。日付は OpenD の時刻文字列の先頭 10 文字（yyyy-MM-dd）。
    public static IReadOnlyList<DailyBarView> MapBars(IReadOnlyList<ProbeKLine> klines)
    {
        ArgumentNullException.ThrowIfNull(klines);
        var bars = new List<DailyBarView>(klines.Count);
        foreach (var k in klines)
        {
            if (k.IsBlank
                || k.Time is not { Length: >= 10 } time
                || !DateOnly.TryParseExact(time[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || k.Open is not { } open || k.High is not { } high || k.Low is not { } low || k.Close is not { } close
                || k.Volume is not { } volume
                || !(open > 0 && high > 0 && low > 0 && close > 0) || volume < 0
                || !double.IsFinite(open) || !double.IsFinite(high) || !double.IsFinite(low) || !double.IsFinite(close))
            {
                continue;
            }

            bars.Add(new DailyBarView(date, (decimal)open, (decimal)high, (decimal)low, (decimal)close, volume));
        }

        return bars;
    }

    private async Task<(int? Used, int? Remaining)> QueryQuotaSafeAsync(
        IKLineQuotaQuery client, CancellationToken cancellationToken)
    {
        try
        {
            var quota = await client.QueryQuotaAsync(includeDetail: false, cancellationToken).ConfigureAwait(false);
            return quota.Succeeded ? (quota.UsedQuota, quota.RemainQuota) : (null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 枠の照会の失敗で日足の取得の結果を覆さない（枠は不明として計器へ出さない）。接続は捨てて次で作り直す。
            Reset();
            return (null, null);
        }
    }

    private void Reset()
    {
        var client = _client;
        _client = null;
        (client as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Reset();
        _gate.Dispose();
    }
}
