using System.Globalization;
using System.Text.RegularExpressions;

namespace OrderExecutionService.Features.OrderExecution.ProbeKLineQuota;

// FR-02, FR-15, ADR-0048 決定 3, ADR-0023 決定 5, #1117, IADR-0464: 日足 K 線の取得枠と復権の扱いを実機で確かめる 1 回実行の検証口。
//
// 起動引数 `--probe-kline-quota [オプション]` で order-execution のイメージから Host を立てずに実行し、次を標準出力へ出して終わる。
//   1. 履歴 K 線の取得枠（used / remain / 詳細）を照会する（before）。
//   2. 指定銘柄ごとに前復権の日足を 1 回取り（直近 N 本を出す）、取得の後に枠を照会する（1 回の取得で枠がいくつ動くか）。
//   3. 分割のあった銘柄を無復権・前復権・後復権で 1 回ずつ取り、日付ごとに終値・出来高を並べる（各取得の後に枠を照会する）。
//   4. 枠を照会する（final・詳細つき）。取得ごとの枠の差から、枠の単位の読み（per-security / per-request / inconclusive）を出す。
// ADR-0048 決定 3 が K 線を判断へ流す前提とした 2 つの確認（取得枠の単位と回復周期・分割をまたいでも 20 日平均比が歪まないこと）を、
// 利用者が実機の OpenD で行うための道具であり、判断へ出来高を流すことはしない。
//
// 🔴 受け取るのは読み取り専用ポート IKLineQuotaQuery の**生成関数だけ**である。発注・訂正・取消を持つ型は受け取らない（試験で固定）。
// 🔴 各要求は 1 回だけ。再試行・ページングのループを置かない（失敗はそのまま出して次の段へ進む。接続の失敗では以後を撃たない）。
// 🔴 要求の間隔を MinRequestInterval（2.5 秒）以上あける（24 回/分。自制レート 30 回/分の内側）。
// 🔴 秘密を出さない。構成由来の値（接続先・RSA 鍵のパス）は出力の最終段で伏せる。口座は選ばない（口座番号は経路に無い）。
public static class KLineQuotaProbeCommand
{
    public const string Flag = "--probe-kline-quota";

    /// <summary>すべての要求が retType=0 で返った。</summary>
    public const int ExitAllSucceeded = 0;

    /// <summary>いずれかの要求が非成功（retType ≠ 0）・接続失敗・タイムアウト。</summary>
    public const int ExitQueryFailed = 1;

    /// <summary>引数不正・構成不正（照会口を組まない＝接続しない）。</summary>
    public const int ExitUsageOrConfiguration = 2;

    public const int DefaultCount = 25;
    public const int MaxCount = 100;
    public const int MaxSymbols = 5;
    public const int MaxSplitSpanDays = 120;

    /// <summary>既定の銘柄（少数に抑える。取得枠は稼働中のサービスと共有である）。</summary>
    public static readonly IReadOnlyList<string> DefaultSymbols = ["AAPL", "MSFT"];

    /// <summary>既定の分割の銘柄と期間: NVDA の 10:1 分割（2024-06-10 から分割後の価格で取引）をまたぐ期間。</summary>
    public const string DefaultSplitSymbol = "NVDA";
    public static readonly DateOnly DefaultSplitFrom = new(2024, 5, 28);
    public static readonly DateOnly DefaultSplitTo = new(2024, 6, 21);

    /// <summary>分割の比較で取る復権区分（この順に取る）。</summary>
    public static readonly IReadOnlyList<KLineRehab> SplitRehabs = [KLineRehab.None, KLineRehab.Forward, KLineRehab.Backward];

    /// <summary>OpenD への要求の最小間隔（60 秒 / 2.5 秒 = 24 回/分。自制レート 30 回/分の内側）。</summary>
    public static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(2500);

    /// <summary>全体の打ち切り。既定の往復 12 回 × 2.5 秒 ＋ 返信待ちを覆う。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    /// <summary>構成由来の伏せる値として扱う最短の長さ（注文費用照会の検証口と同じ）。</summary>
    public const int MinSensitiveLength = 4;

    /// <summary>伏せた箇所に置く文字列（注文費用照会の検証口と同じ）。</summary>
    public const string Masked = "<伏せ>";

    private const string SymbolsOption = "--symbols";
    private const string CountOption = "--count";
    private const string SplitSymbolOption = "--split-symbol";
    private const string SplitFromOption = "--split-from";
    private const string SplitToOption = "--split-to";

    private static readonly string[] KnownOptions = [SymbolsOption, CountOption, SplitSymbolOption, SplitFromOption, SplitToOption];

    // 米国株のコード（`BRK.B` を許す）。大文字へ揃えた後で照合する。
    private static readonly Regex SymbolPattern = new("^[A-Z][A-Z0-9]{0,9}(\\.[A-Z0-9]{1,3})?$", RegexOptions.CultureInvariant);

    /// <summary>引数が検証口の起動を求めているか。旗が在れば形を問わず true（誤った形で Host を起動させないため）。</summary>
    public static bool IsRequested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(a => a == Flag || a.StartsWith(Flag + "=", StringComparison.Ordinal));
    }

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<IKLineQuotaQuery> createQuery,
        TextWriter stdout,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? sensitiveValues = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(createQuery);
        ArgumentNullException.ThrowIfNull(stdout);

        var output = new ProbeOutput(stdout, sensitiveValues ?? []);
        var today = DateOnly.FromDateTime((timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
        if (!TryParse(args, today, out var options, out var usageError))
        {
            output.Line("result=usage-error");
            output.Line($"error.message={OneLine(usageError)}");
            output.Line($"usage=dotnet \"$SERVICE_DLL\" {Flag} [{SymbolsOption} AAPL,MSFT] [{CountOption} 25] "
                + $"[{SplitSymbolOption} NVDA {SplitFromOption} 2024-05-28 {SplitToOption} 2024-06-21]");
            output.Line($"exitCode={ExitUsageOrConfiguration}");
            return ExitUsageOrConfiguration;
        }

        output.Line($"probe=kline-quota market=US symbols={string.Join(',', options.Symbols)} count={options.Count} "
            + $"split={options.SplitSymbol}:{Date(options.SplitFrom)}..{Date(options.SplitTo)} "
            + $"rehabs={string.Join(',', SplitRehabs.Select(RehabName))} "
            + $"minIntervalMs={MinRequestInterval.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}");

        IKLineQuotaQuery query;
        try
        {
            query = createQuery();
        }
        catch (Exception ex)
        {
            // 構成不正（moomoo 以外・実弾階層・RSA 鍵の未マウント等）。接続はしていない。
            output.Line("result=config-error");
            WriteException(output, ex);
            output.Line("requests.sent=0");
            output.Line($"exitCode={ExitUsageOrConfiguration}");
            return ExitUsageOrConfiguration;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? DefaultTimeout);
            var run = new ProbeRun(query, output, delay ?? Task.Delay, cts.Token);
            try
            {
                await run.ExecuteAsync(options, today).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 接続失敗・返信待ちのタイムアウト等。🔴 撃ち直さない（以後の段も撃たない）。
                output.Line("result=error");
                WriteException(output, ex);
                output.Line($"requests.sent={run.RequestsSent}");
                output.Line($"exitCode={ExitQueryFailed}");
                return ExitQueryFailed;
            }

            output.Line($"requests.sent={run.RequestsSent} requests.failed={run.Failures}");
            var exitCode = run.Failures == 0 ? ExitAllSucceeded : ExitQueryFailed;
            output.Line(run.Failures == 0 ? "result=ok" : "result=partial");
            output.Line($"exitCode={exitCode}");
            return exitCode;
        }
        finally
        {
            (query as IDisposable)?.Dispose();
        }
    }

    // 取得枠の単位の読み。known は「取る前から枠の詳細一覧に在った、またはこの実行で既に取った」銘柄の取得。
    // 未知の銘柄の取得は「銘柄単位」でも「要求単位」でも +1 になるため、区別は既知の銘柄の取得の差で行う。
    public static string ReadQuotaUnit(IReadOnlyList<int> unknownDeltas, IReadOnlyList<int> knownDeltas)
    {
        ArgumentNullException.ThrowIfNull(unknownDeltas);
        ArgumentNullException.ThrowIfNull(knownDeltas);
        if (knownDeltas.Count == 0)
            return "inconclusive";
        if (knownDeltas.All(d => d == 0) && unknownDeltas.All(d => d == 1))
            return "per-security";
        if (knownDeltas.All(d => d == 1) && unknownDeltas.All(d => d == 1))
            return "per-request";
        return "inconclusive";
    }

    private sealed record ProbeOptions(
        IReadOnlyList<string> Symbols, int Count, string SplitSymbol, DateOnly SplitFrom, DateOnly SplitTo);

    // 手順の実行。要求の数・失敗の数・取得枠の前後を持つ。
    private sealed class ProbeRun(
        IKLineQuotaQuery query, ProbeOutput output, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        private readonly HashSet<string> _seenSecurities = new(StringComparer.Ordinal);
        private readonly List<(string Symbol, KLineRehab Rehab, bool Known, int Delta)> _steps = [];
        private int? _lastUsed;
        private int _quotaIndex;
        private int _klineIndex;

        public int RequestsSent { get; private set; }

        public int Failures { get; private set; }

        public async Task ExecuteAsync(ProbeOptions options, DateOnly today)
        {
            output.Line("section=quota-before");
            var before = await QuotaAsync("before", includeDetail: true).ConfigureAwait(false);
            int? usedBefore = before.Succeeded ? before.UsedQuota : null;
            foreach (var detail in before.Details)
            {
                if (detail.Security is { } security)
                    _seenSecurities.Add(security);
            }

            output.Line("section=recent-daily");
            var from = today.AddDays(-(options.Count * 2 + 14));
            foreach (var symbol in options.Symbols)
            {
                var reading = await KLineAsync(symbol, KLineRehab.Forward, from, today).ConfigureAwait(false);
                if (reading.Succeeded)
                    WriteRecentBars(reading, options.Count);
            }

            output.Line("section=split-compare");
            var byRehab = new Dictionary<KLineRehab, DailyKLineReading>();
            foreach (var rehab in SplitRehabs)
            {
                var reading = await KLineAsync(options.SplitSymbol, rehab, options.SplitFrom, options.SplitTo).ConfigureAwait(false);
                if (reading.Succeeded)
                    byRehab[rehab] = reading;
            }
            WriteSplitComparison(options.SplitSymbol, byRehab);

            output.Line("section=quota-final");
            var final = await QuotaAsync("final", includeDetail: true).ConfigureAwait(false);
            int? usedFinal = final.Succeeded ? final.UsedQuota : null;
            WriteQuotaSummary(usedBefore, usedFinal);
        }

        private async Task PaceAsync()
        {
            // 🔴 最初の要求の前には待たない。以後は毎回 MinRequestInterval 以上あける（自制レート）。
            if (RequestsSent > 0)
                await delay(MinRequestInterval, cancellationToken).ConfigureAwait(false);
            RequestsSent++;
        }

        private async Task<KLineQuotaReading> QuotaAsync(string label, bool includeDetail)
        {
            await PaceAsync().ConfigureAwait(false);
            var reading = await query.QueryQuotaAsync(includeDetail, cancellationToken).ConfigureAwait(false);
            var i = _quotaIndex++;
            var line = $"quota[{i}] label={label} retType={reading.RetType.ToString(CultureInfo.InvariantCulture)} "
                + $"used={Int(reading.UsedQuota)} remain={Int(reading.RemainQuota)}";
            if (reading.Succeeded && reading.UsedQuota is { } used && _lastUsed is { } last)
                line += $" delta.used={Signed(used - last)}";
            output.Line(line);
            if (!reading.Succeeded)
            {
                Failures++;
                output.Line($"quota[{i}].retMsg={OneLine(reading.RetMsg)}");
            }
            if (includeDetail)
            {
                output.Line($"quota[{i}].detail.count={reading.Details.Count.ToString(CultureInfo.InvariantCulture)}");
                for (var j = 0; j < reading.Details.Count; j++)
                {
                    var d = reading.Details[j];
                    output.Line($"quota[{i}].detail[{j}] security={OneLine(d.Security ?? "(なし)")} name={OneLine(d.Name ?? "(なし)")} "
                        + $"requestTime={OneLine(d.RequestTime ?? "(なし)")} requestTimeStamp={Long(d.RequestTimeStamp)}");
                }
            }
            _lastUsed = reading.Succeeded ? reading.UsedQuota : null;
            return reading;
        }

        private async Task<DailyKLineReading> KLineAsync(string symbol, KLineRehab rehab, DateOnly from, DateOnly to)
        {
            await PaceAsync().ConfigureAwait(false);
            var reading = await query.RequestDailyKLinesAsync(new DailyKLineProbeRequest(symbol, rehab, from, to), cancellationToken)
                .ConfigureAwait(false);
            var k = _klineIndex++;
            var bars = reading.KLines.Count(b => !b.IsBlank);
            output.Line($"kline[{k}] symbol={symbol} rehab={RehabName(rehab)} from={Date(from)} to={Date(to)} "
                + $"retType={reading.RetType.ToString(CultureInfo.InvariantCulture)} bars={bars.ToString(CultureInfo.InvariantCulture)} "
                + $"blank={(reading.KLines.Count - bars).ToString(CultureInfo.InvariantCulture)} hasMore={(reading.HasMore ? "yes" : "no")}");
            if (!reading.Succeeded)
            {
                Failures++;
                output.Line($"kline[{k}].retMsg={OneLine(reading.RetMsg)}");
            }

            // 🔴 取得の直後に枠を照会する（1 回の取得で枠がいくつ動いたかを出すため。外すと単位が読めない）。
            var usedBeforeFetch = _lastUsed;
            var security = "US." + symbol;
            var known = _seenSecurities.Contains(security);
            var after = await QuotaAsync($"after-kline[{k}]", includeDetail: false).ConfigureAwait(false);
            if (reading.Succeeded && usedBeforeFetch is { } b && after.Succeeded && after.UsedQuota is { } a)
                _steps.Add((symbol, rehab, known, a - b));
            if (reading.Succeeded)
                _seenSecurities.Add(security);
            return reading;
        }

        private void WriteRecentBars(DailyKLineReading reading, int count)
        {
            var k = _klineIndex - 1;
            var bars = reading.KLines.Where(b => !b.IsBlank).ToList();
            var shown = bars.Skip(Math.Max(0, bars.Count - count)).ToList();
            output.Line($"kline[{k}].bars.shown={shown.Count.ToString(CultureInfo.InvariantCulture)}");
            for (var j = 0; j < shown.Count; j++)
            {
                var bar = shown[j];
                output.Line($"kline[{k}].bar[{j}] time={OneLine(bar.Time ?? "(なし)")} open={Number(bar.Open)} high={Number(bar.High)} "
                    + $"low={Number(bar.Low)} close={Number(bar.Close)} volume={Long(bar.Volume)} turnover={Number(bar.Turnover)}");
            }
        }

        private void WriteSplitComparison(string symbol, IReadOnlyDictionary<KLineRehab, DailyKLineReading> byRehab)
        {
            var rows = new SortedDictionary<string, Dictionary<KLineRehab, ProbeKLine>>(StringComparer.Ordinal);
            foreach (var (rehab, reading) in byRehab)
            {
                foreach (var bar in reading.KLines.Where(b => !b.IsBlank))
                {
                    var date = DateKey(bar.Time);
                    if (!rows.TryGetValue(date, out var row))
                        rows[date] = row = [];
                    row[rehab] = bar;
                }
            }

            output.Line($"split.symbol={symbol} split.dates={rows.Count.ToString(CultureInfo.InvariantCulture)}");
            foreach (var (date, row) in rows)
            {
                var cells = SplitRehabs.Select(r => row.TryGetValue(r, out var bar)
                    ? $"{RehabName(r)}.close={Number(bar.Close)} {RehabName(r)}.volume={Long(bar.Volume)}"
                    : $"{RehabName(r)}.close=(なし) {RehabName(r)}.volume=(なし)");
                output.Line($"split.row date={date} {string.Join(' ', cells)}");
            }

            // 無復権と比べて、終値・出来高が異なる日数と比の範囲（分割の前の日で出来高も比が動けば「出来高も調整される」）。
            foreach (var rehab in SplitRehabs.Where(r => r != KLineRehab.None))
            {
                var name = RehabName(rehab);
                var pairs = rows.Values
                    .Where(r => r.ContainsKey(KLineRehab.None) && r.ContainsKey(rehab))
                    .Select(r => (None: r[KLineRehab.None], Other: r[rehab]))
                    .ToList();
                if (!byRehab.ContainsKey(KLineRehab.None) || !byRehab.ContainsKey(rehab) || pairs.Count == 0)
                {
                    output.Line($"split.{name}.vsNone dates=0 (比べられない。無復権か {name} の取得が失敗・空)");
                    continue;
                }
                var closeDiffers = pairs.Count(p => Differs(p.None.Close, p.Other.Close));
                var volumeDiffers = pairs.Count(p => p.None.Volume != p.Other.Volume);
                var closeRatios = pairs.Select(p => Ratio(p.None.Close, p.Other.Close)).OfType<double>().ToList();
                var volumeRatios = pairs.Select(p => Ratio(p.Other.Volume, p.None.Volume)).OfType<double>().ToList();
                output.Line($"split.{name}.vsNone dates={pairs.Count.ToString(CultureInfo.InvariantCulture)} "
                    + $"closeDiffers={closeDiffers.ToString(CultureInfo.InvariantCulture)} "
                    + $"volumeDiffers={volumeDiffers.ToString(CultureInfo.InvariantCulture)} "
                    + $"closeRatio.noneOverRehab={Range(closeRatios)} volumeRatio.rehabOverNone={Range(volumeRatios)}");
            }
        }

        private void WriteQuotaSummary(int? usedBefore, int? usedFinal)
        {
            output.Line("section=quota-summary");
            for (var i = 0; i < _steps.Count; i++)
            {
                var s = _steps[i];
                output.Line($"quota.step[{i}] symbol={s.Symbol} rehab={RehabName(s.Rehab)} known={(s.Known ? "yes" : "no")} delta.used={Signed(s.Delta)}");
            }
            var unknown = _steps.Where(s => !s.Known).Select(s => s.Delta).ToList();
            var known = _steps.Where(s => s.Known).Select(s => s.Delta).ToList();
            output.Line($"quota.unit.unknownSecurityDeltas={Deltas(unknown)}");
            output.Line($"quota.unit.knownSecurityDeltas={Deltas(known)}");
            output.Line($"quota.unit.reading={ReadQuotaUnit(unknown, known)}");
            var total = usedBefore is { } b && usedFinal is { } f ? Signed(f - b) : "(不明)";
            output.Line($"quota.used.before={Int(usedBefore)} quota.used.final={Int(usedFinal)} quota.used.delta={total}");
        }
    }

    private static bool TryParse(IReadOnlyList<string> args, DateOnly today, out ProbeOptions options, out string error)
    {
        options = new ProbeOptions(DefaultSymbols, DefaultCount, DefaultSplitSymbol, DefaultSplitFrom, DefaultSplitTo);
        if (args.Count == 0 || args[0] != Flag)
        {
            error = $"`{Flag}` を先頭に置き、値は後ろのオプションで渡してください（`{Flag}=…` の形は受け付けません）。";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i += 2)
        {
            var option = args[i];
            if (!KnownOptions.Contains(option))
            {
                error = $"未知の引数です: `{OneLine(option)}`（使えるのは {string.Join(" / ", KnownOptions)}）。";
                return false;
            }
            if (values.ContainsKey(option))
            {
                error = $"`{option}` が重複しています。";
                return false;
            }
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                error = $"`{option}` の値がありません。";
                return false;
            }
            values[option] = args[i + 1];
        }

        var symbols = DefaultSymbols;
        if (values.TryGetValue(SymbolsOption, out var rawSymbols))
        {
            var parsed = new List<string>();
            foreach (var raw in rawSymbols.Split(','))
            {
                if (!TryNormalizeSymbol(raw, out var symbol))
                {
                    error = $"銘柄 `{OneLine(raw)}` は米国株のコード（例: AAPL・BRK.B。`US.` の接頭辞は可）ではありません。";
                    return false;
                }
                if (parsed.Contains(symbol))
                {
                    error = $"銘柄 `{symbol}` が重複しています。";
                    return false;
                }
                parsed.Add(symbol);
            }
            if (parsed.Count > MaxSymbols)
            {
                error = $"銘柄は {MaxSymbols} つまでです（取得枠を消費するため少数に抑える）。";
                return false;
            }
            symbols = parsed;
        }

        var count = DefaultCount;
        if (values.TryGetValue(CountOption, out var rawCount)
            && (!int.TryParse(rawCount, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 || count > MaxCount))
        {
            error = $"`{CountOption}` は 1〜{MaxCount} の整数です。";
            return false;
        }

        var splitGiven = new[] { SplitSymbolOption, SplitFromOption, SplitToOption }.Count(values.ContainsKey);
        var splitSymbol = DefaultSplitSymbol;
        var splitFrom = DefaultSplitFrom;
        var splitTo = DefaultSplitTo;
        if (splitGiven is > 0 and < 3)
        {
            error = $"`{SplitSymbolOption}`・`{SplitFromOption}`・`{SplitToOption}` は 3 つそろえて指定してください。";
            return false;
        }
        if (splitGiven == 3)
        {
            if (!TryNormalizeSymbol(values[SplitSymbolOption], out splitSymbol))
            {
                error = $"`{SplitSymbolOption}` は米国株のコードです。";
                return false;
            }
            if (!TryParseDate(values[SplitFromOption], out splitFrom) || !TryParseDate(values[SplitToOption], out splitTo))
            {
                error = $"`{SplitFromOption}` / `{SplitToOption}` は yyyy-MM-dd の日付です。";
                return false;
            }
            if (splitFrom > splitTo || splitTo.DayNumber - splitFrom.DayNumber > MaxSplitSpanDays || splitTo > today)
            {
                error = $"分割の期間は from ≦ to・{MaxSplitSpanDays} 日以内・to は今日以前にしてください。";
                return false;
            }
        }

        options = new ProbeOptions(symbols, count, splitSymbol, splitFrom, splitTo);
        error = string.Empty;
        return true;
    }

    private static bool TryNormalizeSymbol(string raw, out string symbol)
    {
        symbol = raw.Trim().ToUpperInvariant();
        if (symbol.StartsWith("US.", StringComparison.Ordinal))
            symbol = symbol[3..];
        return SymbolPattern.IsMatch(symbol);
    }

    private static bool TryParseDate(string raw, out DateOnly date) =>
        DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string RehabName(KLineRehab rehab) => rehab switch
    {
        KLineRehab.None => "none",
        KLineRehab.Forward => "forward",
        KLineRehab.Backward => "backward",
        _ => rehab.ToString(),
    };

    // 日足の Time は "yyyy-MM-dd"（環境によっては時刻付き）。日付の部分で揃える。
    private static string DateKey(string? time) =>
        string.IsNullOrWhiteSpace(time) ? "(なし)" : time.Trim().Split(' ')[0];

    private static bool Differs(double? a, double? b) =>
        a is not { } x || b is not { } y ? a.HasValue != b.HasValue : Math.Abs(x - y) > 1e-9 * Math.Max(1.0, Math.Abs(x));

    private static double? Ratio(double? numerator, double? denominator) =>
        numerator is { } n && denominator is { } d && d != 0 ? n / d : null;

    private static double? Ratio(long? numerator, long? denominator) =>
        numerator is { } n && denominator is { } d && d != 0 ? (double)n / d : null;

    private static string Range(IReadOnlyList<double> values) =>
        values.Count == 0
            ? "(なし)"
            : $"{Math.Round(values.Min(), 4).ToString("R", CultureInfo.InvariantCulture)}..{Math.Round(values.Max(), 4).ToString("R", CultureInfo.InvariantCulture)}";

    private static string Deltas(IReadOnlyList<int> deltas) =>
        deltas.Count == 0 ? "(なし)" : string.Join(',', deltas.Select(Signed));

    private static string Signed(int value) =>
        value > 0 ? "+" + value.ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Int(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "(なし)";

    private static string Long(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "(なし)";

    private static string Number(double? value) =>
        value is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : "(なし)";

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    private static void WriteException(ProbeOutput output, Exception ex)
    {
        var depth = 0;
        for (var e = ex; e is not null && depth < 4; e = e.InnerException, depth++)
        {
            output.Line($"error[{depth}].type={e.GetType().Name}");
            output.Line($"error[{depth}].message={OneLine(e.Message)}", maskLongDigitRuns: true);
        }
    }

    private static readonly Regex LongDigitRun = new("[0-9]{6,}", RegexOptions.CultureInvariant);

    public static string MaskLongDigitRuns(string text) =>
        LongDigitRun.Replace(text, m => "****" + m.Value[^2..]);

    // 出力の最終段（注文費用照会の検証口と同じ順序）。すべての行を書く前に通す。
    //   (1) 構成由来の伏せる値（長いものから・語の境界つきの完全一致。4 文字未満は対象外）
    //   (2) 例外文だけ: 6 桁以上の数字の並び（(1) の後に掛ける。先に掛けると数字を含む鍵のパスが完全一致しなくなる）
    // K 線の検証口は口座を選ばないため、口座番号の伏せ（注文費用照会の (2)）は要らない。
    private sealed class ProbeOutput
    {
        private readonly TextWriter _writer;
        private readonly Regex[] _sensitive;

        public ProbeOutput(TextWriter writer, IReadOnlyCollection<string> sensitiveValues)
        {
            _writer = writer;
            _sensitive = sensitiveValues
                .Where(v => v is not null && v.Trim().Length >= MinSensitiveLength)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(v => v.Length)
                .Select(v => new Regex($"(?<![A-Za-z0-9_]){Regex.Escape(v)}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant))
                .ToArray();
        }

        public void Line(string line, bool maskLongDigitRuns = false)
        {
            var text = line;
            foreach (var pattern in _sensitive)
                text = pattern.Replace(text, Masked);
            if (maskLongDigitRuns)
                text = MaskLongDigitRuns(text);
            _writer.WriteLine(text);
        }
    }
}
