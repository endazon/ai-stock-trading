using System.Globalization;
using System.Text.RegularExpressions;

namespace OrderExecutionService.Features.OrderExecution.ProbeOrderFee;

// FR-11, FR-16, ADR-0016 決定15, #1086, IADR-0300（2026-09-29 追記）: 注文費用照会の 1 回実行の検証口。
//
// 起動引数 `--probe-order-fee <注文ID>` で order-execution のイメージから Host を立てずに実行し、
// Trd_GetOrderFee を **1 回だけ** 撃って応答を標準出力へ出し、終了コードを返す。段 2（実費の供給）へ進む前に、
// **SIMULATE 口座で費用照会が値を返すか**を実測で確かめるための道具であり、経費の記録（TradeExpenseRecorded）は行わない。
//
// 🔴 受け取るのは読み取り専用ポート IOrderFeeQuery の**生成関数だけ**である。IMoomooTradeClient（発注・取消を持つ）
// も IBrokerAdapter も受け取らないため、ここから書き込み系の API へ届く経路が無い（試験で固定）。
// 🔴 照会は 1 回。再試行・ループを置かない（失敗はそのまま出して終わる）。
// 🔴 秘密を出さない。口座 ID は伏せた形でしか受け取らず、構成の値は 1 つも表示しない。
public static class OrderFeeProbeCommand
{
    public const string Flag = "--probe-order-fee";

    /// <summary>照会成功で費用項目が 1 件以上返った。</summary>
    public const int ExitFeesReturned = 0;

    /// <summary>照会失敗（retType ≠ 0・注文が見つからない・OrderIDEx が空・接続失敗・タイムアウト）。</summary>
    public const int ExitQueryFailed = 1;

    /// <summary>引数不正・構成不正（照会口を組まない＝接続しない）。</summary>
    public const int ExitUsageOrConfiguration = 2;

    /// <summary>照会成功（retType=0）だが費用が空だった。</summary>
    public const int ExitNoFees = 3;

    /// <summary>
    /// 接続・解決・照会を合わせた上限。往復は最大 7 回（接続 1・口座一覧 1・注文一覧 4・費用照会 1）で、
    /// 既定の返信待ち 15 秒 × 7 = 105 秒を覆う。
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    // 注文 ID（10 進の OrderID か OrderIDEx）。`-` 始まりは別の引数の取り違えとして拒む。
    private static readonly Regex OrderIdPattern = new("^[0-9A-Za-z_][0-9A-Za-z_-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 引数が検証口の起動を求めているか。値の有無・形を問わず旗が在れば true（誤った形で Host を起動させないため）。
    /// </summary>
    public static bool IsRequested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(a => a == Flag || a.StartsWith(Flag + "=", StringComparison.Ordinal));
    }

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<IOrderFeeQuery> createQuery,
        TextWriter stdout,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? sensitiveValues = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(createQuery);
        ArgumentNullException.ThrowIfNull(stdout);

        // 🔴 すべての行は ProbeOutput を通して出す（出力の最終段で口座 ID を伏せる。どの経路の例外文にも効く）。
        // sensitiveValues は構成由来の伏せる値（接続先 host:port・鍵のパス等）。照会口の生成前（構成不正）にも効く。
        var output = new ProbeOutput(stdout, sensitiveValues ?? []);
        if (!TryParse(args, out var orderId, out var usageError))
        {
            output.Line("result=usage-error");
            output.Line($"error.message={OneLine(usageError)}");
            output.Line($"usage=dotnet \"$SERVICE_DLL\" {Flag} <注文ID（10 進の OrderID または OrderIDEx）>");
            output.Line($"exitCode={ExitUsageOrConfiguration}");
            return ExitUsageOrConfiguration;
        }

        output.Line($"probe=order-fee orderId={orderId} trdEnv=SIMULATE");

        IOrderFeeQuery query;
        try
        {
            query = createQuery();
            output.Redactor = query as IProbeOutputRedactor;
        }
        catch (Exception ex)
        {
            // 構成不正（moomoo 以外・実弾階層・RSA 鍵の未マウント等）。接続はしていない。
            output.Line("result=config-error");
            WriteException(output, ex);
            output.Line("getOrderFee.sent=no");
            output.Line($"exitCode={ExitUsageOrConfiguration}");
            return ExitUsageOrConfiguration;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? DefaultTimeout);

            OrderFeeQueryResult result;
            try
            {
                // 🔴 1 回だけ。例外でも撃ち直さない。
                result = await query.QueryOrderFeeAsync(orderId, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                output.Line("result=error");
                WriteException(output, ex);
                // 接続前の失敗か、送信後の返信待ちで切れたかは例外からは言い切れない。
                output.Line("getOrderFee.sent=unknown");
                output.Line($"exitCode={ExitQueryFailed}");
                return ExitQueryFailed;
            }

            var exitCode = Write(output, result);
            output.Line($"exitCode={exitCode}");
            return exitCode;
        }
        finally
        {
            (query as IDisposable)?.Dispose();
        }
    }

    private static int Write(ProbeOutput output, OrderFeeQueryResult result)
    {
        output.Line($"account=SIMULATE({result.MaskedAccountId})");
        if (result.ResolvedMarket is not null)
            output.Line($"order.market={result.ResolvedMarket}");
        if (result.ResolvedOrderStatus is { } status)
            output.Line($"order.status={status.ToString(CultureInfo.InvariantCulture)}");
        output.Line($"order.orderIdEx={OneLine(result.OrderIdEx ?? "(なし)")}");
        output.Line($"getOrderFee.sent={(result.Sent ? "yes" : "no")}");

        switch (result.Outcome)
        {
            case OrderFeeQueryOutcome.OrderNotFound:
                output.Line("result=order-not-found");
                return ExitQueryFailed;
            case OrderFeeQueryOutcome.OrderIdExMissing:
                output.Line("result=order-id-ex-missing");
                return ExitQueryFailed;
        }

        output.Line($"retType={(result.RetType?.ToString(CultureInfo.InvariantCulture) ?? "(なし)")}");
        output.Line($"retMsg={OneLine(result.RetMsg ?? string.Empty)}");
        if (result.Outcome == OrderFeeQueryOutcome.Failed)
        {
            output.Line("result=failed");
            return ExitQueryFailed;
        }

        var itemCount = 0;
        for (var i = 0; i < result.Fees.Count; i++)
        {
            var fee = result.Fees[i];
            output.Line(
                $"fee[{i}].orderIdEx={OneLine(fee.OrderIdEx ?? "(なし)")} fee[{i}].feeAmount={Number(fee.FeeAmount)} fee[{i}].items={fee.Items.Count}");
            for (var j = 0; j < fee.Items.Count; j++)
            {
                var item = fee.Items[j];
                output.Line($"fee[{i}].item[{j}].title={OneLine(item.Title ?? "(なし)")}");
                output.Line($"fee[{i}].item[{j}].value={Number(item.Value)}");
                itemCount++;
            }
        }
        output.Line($"fees.orders={result.Fees.Count} fees.items={itemCount}");

        // 「値を返すか」の答え。注文ぶんの行が在っても項目も合計も無ければ「返さない」側へ数える。
        var returned = itemCount > 0 || result.Fees.Any(f => f.FeeAmount is not null);
        output.Line(returned ? "result=fees-returned" : "result=no-fees");
        return returned ? ExitFeesReturned : ExitNoFees;
    }

    private static bool TryParse(IReadOnlyList<string> args, out string orderId, out string error)
    {
        orderId = string.Empty;
        string value;
        if (args.Count == 2 && args[0] == Flag)
        {
            value = args[1];
        }
        else if (args.Count == 1 && args[0].StartsWith(Flag + "=", StringComparison.Ordinal))
        {
            // IsRequested が `=` 形も検証口の起動と判定するため、形も同じく受け付ける（判定と解釈の非対称を作らない）。
            value = args[0][(Flag.Length + 1)..];
        }
        else
        {
            error = $"引数は `{Flag} <注文ID>`（または `{Flag}=<注文ID>`）だけにしてください（受け取った数: {args.Count}）。";
            return false;
        }
        if (!OrderIdPattern.IsMatch(value))
        {
            error = "注文 ID は英数字・`_`・`-` の 1〜64 文字で、`-` から始まらないこと。";
            return false;
        }
        orderId = value;
        error = string.Empty;
        return true;
    }

    private static void WriteException(ProbeOutput output, Exception ex)
    {
        var depth = 0;
        for (var e = ex; e is not null && depth < 4; e = e.InnerException, depth++)
        {
            output.Line($"error[{depth}].type={e.GetType().Name}");
            // 例外文は OpenD の retMsg をそのまま含み得る。口座が確定する前（口座一覧の照会の失敗）は伏せる値が
            // 分からないため、6 桁以上の数字の並びを末尾 2 桁以外伏せる（口座 ID の形。例外文に注文 ID を読む用は無い）。
            // 🔴 数字の並びの伏せは最終段の**最後**に掛ける（先に掛けると数字を含む鍵のパス・host が完全一致しなくなる）。
            output.Line($"error[{depth}].message={OneLine(e.Message)}", maskLongDigitRuns: true);
        }
    }

    private static readonly Regex LongDigitRun = new("[0-9]{6,}", RegexOptions.CultureInvariant);

    public static string MaskLongDigitRuns(string text) =>
        LongDigitRun.Replace(text, m => "****" + m.Value[^2..]);

    private static string Number(double? value) =>
        value is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : "(なし)";

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>構成由来の伏せる値として扱う最短の長さ。これより短い値（`1` 等）は一般の文字と区別できないため伏せない。</summary>
    public const int MinSensitiveLength = 4;

    /// <summary>伏せた箇所に置く文字列。</summary>
    public const string Masked = "<伏せ>";

    // 出力の最終段。すべての行を書く前に次の順で通す（順序は試験で固定）。
    //   (1) 構成由来の伏せる値（長いものから・語の境界つきの完全一致。4 文字未満は対象外）
    //   (2) 照会口の伏せ（口座 ID。照会口の生成前は無し）
    //   (3) 例外文だけ: 6 桁以上の数字の並び（口座が確定する前の口座 ID の形）
    // (3) を先に掛けると、数字を含む鍵のパスや host が (1) で完全一致しなくなり大半が漏れる。
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
                .Select(SensitivePattern)
                .ToArray();
        }

        public IProbeOutputRedactor? Redactor { get; set; }

        public void Line(string line, bool maskLongDigitRuns = false)
        {
            var text = line;
            foreach (var pattern in _sensitive)
                text = pattern.Replace(text, Masked);
            text = Redactor?.Redact(text) ?? text;
            if (maskLongDigitRuns)
                text = MaskLongDigitRuns(text);
            _writer.WriteLine(text);
        }

        // 語の境界つき（前後が英数字・`_` でない）。`opend` が `opendir` を壊さない。
        private static Regex SensitivePattern(string value) =>
            new($"(?<![A-Za-z0-9_]){Regex.Escape(value)}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
    }
}
