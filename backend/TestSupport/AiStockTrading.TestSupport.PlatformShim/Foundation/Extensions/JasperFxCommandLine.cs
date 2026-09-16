using JasperFx;
using Microsoft.AspNetCore.Builder;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// NFR-01, ADR-0006, IADR-0129（2026-09-17 追記）, #811: 各サービスの Program.cs は `app.RunAiStockTradingAsync(args)` で
// 終わり、JasperFx のコマンドライン（`dotnet <dll> codegen write` 等）を受ける。ビルド段の `codegen write` は
// ホストを Build するだけで Start しない（外部トランスポートも stub される）が、本リポの Program.cs は Build 直後に
// EF Core の MigrateAsync を走らせるため、そこだけ「ホストとして稼働するときに限る」判定が要る。
public static class JasperFxCommandLine
{
    /// <summary>
    /// 引数が「ホストを起動して稼働する」意図か。引数なし・先頭が <c>run</c>・先頭が <c>--</c>（ASP.NET 流の構成引数）
    /// なら true。<c>codegen</c> / <c>describe</c> / <c>check-env</c> といった JasperFx のツールコマンドなら false
    /// （DB・ブローカに触らせない）。
    /// </summary>
    public static bool IsHostRun(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            return true;
        }

        var first = args[0];
        return first.Equals("run", StringComparison.OrdinalIgnoreCase)
            || first.StartsWith("--", StringComparison.Ordinal);
    }

    /// <summary>
    /// 引数を JasperFx のコマンドラインに渡すか。<c>--</c> で始まる引数は ASP.NET 流の構成引数
    /// （<c>--urls</c> / <c>--Key=Value</c>）であり、JasperFx へ渡してはならない。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>WebApplicationFactory</c> は <c>UseSetting</c>（テストの HostSettings）を <c>--Key=Value</c> の引数として
    /// エントリポイントへ渡す。JasperFx は先頭が <c>-</c> なら既定コマンド <c>run</c> を前置し、<c>run</c> が知らない
    /// フラグを**使い方の表示と終了コード 1** で拒否するため、ホストが一度も起動せず TestServer が
    /// "The server has not been started" で落ちる（実測: RiskManagement / TradeDecision の Wiring テスト 34 件）。
    /// JasperFx 自身も <c>--urls</c> 等は <c>RunAsync</c> へ素通しするが、対象が 3 つのフラグに限られる。
    /// </remarks>
    public static bool UsesJasperFxCommands(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length == 0 || !args[0].StartsWith("--", StringComparison.Ordinal);
    }

    /// <summary>
    /// 全サービス共通の終端。引数なし・<c>run</c>・JasperFx のツールコマンドは <c>RunJasperFxCommands</c> へ、
    /// <c>--</c> で始まる ASP.NET 流の引数は従来どおり <c>RunAsync</c> へ（<see cref="UsesJasperFxCommands"/>）。
    /// いずれもホスト稼働は従来の <c>app.Run()</c> と同じ（SIGTERM は <c>IHostApplicationLifetime</c> で止まる）。
    /// </summary>
    public static async Task<int> RunAiStockTradingAsync(this WebApplication app, string[] args)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);

        if (UsesJasperFxCommands(args))
        {
            return await app.RunJasperFxCommands(args);
        }

        await app.RunAsync();
        return 0;
    }
}
