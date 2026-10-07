using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// NFR-06, NFR-05, IADR-0496, #1192: 未処理例外の応答を全サービスで ProblemDetails に固定する。
//
// 🔴 Development の WebApplication は開発者向け例外ページを**利用者のパイプラインより外側に**自動で挿入し、例外時に
// スタックトレースと**全要求ヘッダー（Authorization: Bearer を含む）**を応答へ載せる（PoC でトークンが漏れた）。
// 本委譲はパイプラインの内側で例外を受けて応答を書き切るので、外側のページへ例外が届かない（環境名に依らない多重防御）。
// 配備の環境名はチャートが Production に固定する（templates/deployment.yaml。Development は描画で止める）。
//
// 応答に載せるのは type・title・status・traceId だけである。例外の型・メッセージ・スタック・要求ヘッダーは載せない
// （例外はミドルウェアが Error ログへ出す。traceId でログと突き合わせる）。
public static class ExceptionHandlingExtensions
{
    /// <summary><see cref="IApplicationBuilder.Properties"/> に置く導入済みの印（<see cref="JasperFxCommandLine.RunAiStockTradingAsync"/> が確かめる）。</summary>
    public const string InstalledPropertyKey = "AiStockTrading.ExceptionHandlerInstalled";

    /// <summary>
    /// 未処理例外を ProblemDetails（ヘッダー・スタック・例外メッセージなし）に写す。パイプラインの**先頭**で呼ぶ
    /// （認証など後続のミドルウェアの例外も受けるため）。2 回呼んでも 1 回だけ入れる。
    /// </summary>
    public static WebApplication UseAiStockTradingExceptionHandler(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (IsInstalled(app))
        {
            return app;
        }

        app.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = WriteProblemAsync });
        ((IApplicationBuilder)app).Properties[InstalledPropertyKey] = true;
        return app;
    }

    /// <summary>本拡張で例外処理を導入済みか。</summary>
    public static bool IsInstalled(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return ((IApplicationBuilder)app).Properties.ContainsKey(InstalledPropertyKey);
    }

    internal static Task WriteProblemAsync(HttpContext context)
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        // Development の最小 API は要求本文の束縛失敗を BadHttpRequestException で投げる（ThrowOnBadRequest）。状態は保つ。
        var status = error is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = status < StatusCodes.Status500InternalServerError
                ? "要求を処理できませんでした。"
                : "サーバー内部でエラーが発生しました。",
            Type = status < StatusCodes.Status500InternalServerError
                ? "https://tools.ietf.org/html/rfc9110#section-15.5.1"
                : "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        context.Response.StatusCode = status;
        return Results.Problem(problem).ExecuteAsync(context);
    }
}
