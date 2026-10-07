using JasperFx;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

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
        EnsureExceptionHandlerInstalled(app);
        EnsureAuthenticationInsideExceptionHandler(app);
        EnsureServiceProviderValidation(app);

        if (UsesJasperFxCommands(args))
        {
            return await app.RunJasperFxCommands(args);
        }

        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// NFR-06, IADR-0496, #1192: 例外処理（<see cref="ExceptionHandlingExtensions.UseAiStockTradingExceptionHandler"/>）の付け忘れを
    /// 起動時に止める。付け忘れたサービスは、例外時に（Development なら開発者向けページで要求ヘッダーごと）素の応答を返すため、
    /// 黙って稼働させない（WebApplicationFactory の試験も同じ終端を通るので、試験でも赤になる）。
    /// </summary>
    internal static void EnsureExceptionHandlerInstalled(WebApplication app)
    {
        if (!ExceptionHandlingExtensions.IsInstalled(app))
        {
            throw new InvalidOperationException(
                "例外処理が導入されていません。Program.cs で app.UseAiStockTradingMiddleware() か "
                + "app.UseAiStockTradingExceptionHandler() を Build() の直後に呼んでください（IADR-0496）。");
        }
    }

    /// <summary>
    /// NFR-06, IADR-0496（#1205 監査の追記）, #1192: 認証・認可のサービスを登録しているのに、パイプラインへ明示で入れていなければ
    /// 起動を止める。WebApplication はその場合 UseAuthentication / UseAuthorization を**利用者のパイプラインより外側**に自動で挿入する
    /// ため、認証スキームの例外（例: Keycloak 不達時の JwtBearer の鍵取得失敗）が例外処理の外へ抜け、Development なら
    /// 開発者向けページが Authorization ヘッダーごと応答する。明示の挿入（例外処理の後ろ）は
    /// <see cref="ExceptionHandlingExtensions.UseAiStockTradingExceptionHandler"/> が順序を、本表明が有無を守る。
    /// </summary>
    internal static void EnsureAuthenticationInsideExceptionHandler(WebApplication app)
    {
        var isService = app.Services.GetService<IServiceProviderIsService>();
        var properties = ((IApplicationBuilder)app).Properties;
        if (isService?.IsService(typeof(IAuthenticationSchemeProvider)) is true
            && !properties.ContainsKey(ExceptionHandlingExtensions.AuthenticationMiddlewareSetKey))
        {
            throw new InvalidOperationException(
                "認証を登録しているのに app.UseAuthentication() が明示されていません。例外処理の直後に "
                + "app.UseAuthentication(); app.UseAuthorization(); を呼ぶか app.UseAiStockTradingMiddleware() を使ってください"
                + "（自動挿入は例外処理の外側に入る。IADR-0496）。");
        }

        if (isService?.IsService(typeof(IAuthorizationHandlerProvider)) is true
            && !properties.ContainsKey(ExceptionHandlingExtensions.AuthorizationMiddlewareSetKey))
        {
            throw new InvalidOperationException(
                "認可を登録しているのに app.UseAuthorization() が明示されていません。例外処理の直後に "
                + "app.UseAuthentication(); app.UseAuthorization(); を呼ぶか app.UseAiStockTradingMiddleware() を使ってください"
                + "（自動挿入は例外処理の外側に入る。IADR-0496）。");
        }
    }

    /// <summary>
    /// NFR-06, IADR-0496（#1205 監査の追記）, #1192: DI 検証（<see cref="ServiceProviderValidationExtensions.UseAiStockTradingServiceProviderValidation"/>）の
    /// 付け忘れを起動時に止める（Production では既定で外れるため、付け忘れは黙って検証の無い Pod になる）。
    /// </summary>
    internal static void EnsureServiceProviderValidation(WebApplication app)
    {
        if (!ServiceProviderValidationExtensions.IsEnabled(app.Services))
        {
            throw new InvalidOperationException(
                "DI 検証が有効になっていません。Program.cs で WebApplication.CreateBuilder の直後に "
                + "builder.UseAiStockTradingServiceProviderValidation() を呼んでください（IADR-0496）。");
        }
    }
}
