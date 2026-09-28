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

    /// <summary>接続・解決・照会を合わせた上限。既定の返信待ち（15 秒）× 解決の最大 5 往復を覆う。</summary>
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(createQuery);
        ArgumentNullException.ThrowIfNull(stdout);

        if (!TryParse(args, out var orderId, out var usageError))
        {
            stdout.WriteLine("result=usage-error");
            stdout.WriteLine($"error.message={OneLine(usageError)}");
            stdout.WriteLine($"usage=dotnet \"$SERVICE_DLL\" {Flag} <注文ID（10 進の OrderID または OrderIDEx）>");
            stdout.WriteLine($"exitCode={ExitUsageOrConfiguration}");
            return ExitUsageOrConfiguration;
        }

        stdout.WriteLine($"probe=order-fee orderId={orderId} trdEnv=SIMULATE");

        IOrderFeeQuery query;
        try
        {
            query = createQuery();
        }
        catch (Exception ex)
        {
            // 構成不正（moomoo 以外・実弾階層・RSA 鍵の未マウント等）。接続はしていない。
            stdout.WriteLine("result=config-error");
            WriteException(stdout, ex);
            stdout.WriteLine("getOrderFee.sent=no");
            stdout.WriteLine($"exitCode={ExitUsageOrConfiguration}");
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
                stdout.WriteLine("result=error");
                WriteException(stdout, ex);
                // 接続前の失敗か、送信後の返信待ちで切れたかは例外からは言い切れない。
                stdout.WriteLine("getOrderFee.sent=unknown");
                stdout.WriteLine($"exitCode={ExitQueryFailed}");
                return ExitQueryFailed;
            }

            var exitCode = Write(stdout, result);
            stdout.WriteLine($"exitCode={exitCode}");
            return exitCode;
        }
        finally
        {
            (query as IDisposable)?.Dispose();
        }
    }

    private static int Write(TextWriter stdout, OrderFeeQueryResult result)
    {
        stdout.WriteLine($"account=SIMULATE({result.MaskedAccountId})");
        if (result.ResolvedMarket is not null)
            stdout.WriteLine($"order.market={result.ResolvedMarket}");
        if (result.ResolvedOrderStatus is { } status)
            stdout.WriteLine($"order.status={status.ToString(CultureInfo.InvariantCulture)}");
        stdout.WriteLine($"order.orderIdEx={OneLine(result.OrderIdEx ?? "(なし)")}");
        stdout.WriteLine($"getOrderFee.sent={(result.Sent ? "yes" : "no")}");

        switch (result.Outcome)
        {
            case OrderFeeQueryOutcome.OrderNotFound:
                stdout.WriteLine("result=order-not-found");
                return ExitQueryFailed;
            case OrderFeeQueryOutcome.OrderIdExMissing:
                stdout.WriteLine("result=order-id-ex-missing");
                return ExitQueryFailed;
        }

        stdout.WriteLine($"retType={(result.RetType?.ToString(CultureInfo.InvariantCulture) ?? "(なし)")}");
        stdout.WriteLine($"retMsg={OneLine(result.RetMsg ?? string.Empty)}");
        if (result.Outcome == OrderFeeQueryOutcome.Failed)
        {
            stdout.WriteLine("result=failed");
            return ExitQueryFailed;
        }

        var itemCount = 0;
        for (var i = 0; i < result.Fees.Count; i++)
        {
            var fee = result.Fees[i];
            stdout.WriteLine(
                $"fee[{i}].orderIdEx={OneLine(fee.OrderIdEx ?? "(なし)")} fee[{i}].feeAmount={Number(fee.FeeAmount)} fee[{i}].items={fee.Items.Count}");
            for (var j = 0; j < fee.Items.Count; j++)
            {
                var item = fee.Items[j];
                stdout.WriteLine($"fee[{i}].item[{j}].title={OneLine(item.Title ?? "(なし)")}");
                stdout.WriteLine($"fee[{i}].item[{j}].value={Number(item.Value)}");
                itemCount++;
            }
        }
        stdout.WriteLine($"fees.orders={result.Fees.Count} fees.items={itemCount}");

        // 「値を返すか」の答え。注文ぶんの行が在っても項目も合計も無ければ「返さない」側へ数える。
        var returned = itemCount > 0 || result.Fees.Any(f => f.FeeAmount is not null);
        stdout.WriteLine(returned ? "result=fees-returned" : "result=no-fees");
        return returned ? ExitFeesReturned : ExitNoFees;
    }

    private static bool TryParse(IReadOnlyList<string> args, out string orderId, out string error)
    {
        orderId = string.Empty;
        if (args.Count != 2 || args[0] != Flag)
        {
            error = $"引数は `{Flag} <注文ID>` のちょうど 2 つにしてください（受け取った数: {args.Count}）。";
            return false;
        }
        var value = args[1];
        if (!OrderIdPattern.IsMatch(value))
        {
            error = "注文 ID は英数字・`_`・`-` の 1〜64 文字で、`-` から始まらないこと。";
            return false;
        }
        orderId = value;
        error = string.Empty;
        return true;
    }

    private static void WriteException(TextWriter stdout, Exception ex)
    {
        var depth = 0;
        for (var e = ex; e is not null && depth < 4; e = e.InnerException, depth++)
        {
            stdout.WriteLine($"error[{depth}].type={e.GetType().Name}");
            stdout.WriteLine($"error[{depth}].message={OneLine(e.Message)}");
        }
    }

    private static string Number(double? value) =>
        value is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : "(なし)";

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
