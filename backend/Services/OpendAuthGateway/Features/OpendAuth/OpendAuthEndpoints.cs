using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpendAuthGateway.Common;
using OpendAuthGateway.Infrastructure;

namespace OpendAuthGateway.Features.OpendAuth;

/// <summary>
/// #722, ADR-0002, IADR-0053, IADR-0320: OpenD 認証サイドカーの HTTP 面。
/// <para>
/// 公開するのは<b>ちょうど 3 本</b>である。増やすときは IADR-0320 を改定すること —— この面は
/// 実口座への窓口（OpenD のコンソール）に直結しており、口が増えるたびに攻撃面が増える。
/// </para>
/// <list type="bullet">
///   <item><c>GET  /opend-auth/state</c>   … コンソール末尾（整形済み）と待たれているプロンプト</item>
///   <item><c>GET  /opend-auth/captcha</c> … 画像 CAPTCHA の写し（固定パス・引数なし）</item>
///   <item><c>POST /opend-auth/verify</c>  … 検証コードの投入（閉じた 3 コマンドのみ）</item>
/// </list>
/// <para>
/// ⚠️ <b>本サービス自身は認証を持たない。</b> Ingress を持たず Pod 網にしか bind せず、
/// 呼び出し元はクラスタ内の BFF だけである（利用者認証は BFF の手前で済んでいる）。
/// 入力面の安全は認証ではなく <see cref="OpendConsoleCommand"/> の allowlist が担う。
/// </para>
/// </summary>
public static class OpendAuthEndpoints
{
    /// <summary>ログのカテゴリ。<b>投入値は決してこのカテゴリへも出さない。</b></summary>
    private const string LoggerCategory = "OpendAuthGateway.OpendAuth";

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    /// <summary>3 本のエンドポイントを登録する。</summary>
    public static IEndpointRouteBuilder MapOpendAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/opend-auth");
        group.MapGet("/state", GetState);
        group.MapGet("/captcha", GetCaptcha);
        group.MapPost("/verify", PostVerifyAsync);
        group.MapPost("/resend", PostResend);
        return app;
    }

    /// <summary>
    /// #722: いまの状態を返す。OpenD が動いていなくても 200 を返す（<c>consoleAvailable=false</c>）
    /// —— 画面は「まだ出ていない」と「壊れている」を区別できたほうがよい。
    /// </summary>
    private static IResult GetState(IOptions<OpendAuthOptions> options)
    {
        var opt = options.Value;
        var raw = ReadTail(opt.ConsoleLogPath, opt.ConsoleTailBytes, out var consoleAvailable);
        var console = ConsoleTail.Sanitize(raw);
        var prompt = ConsoleTail.DetectPrompt(console);

        return Results.Ok(new OpendAuthState(
            ConsoleTail.ToWireValue(prompt),
            CaptchaAvailable(opt),
            consoleAvailable,
            console));
    }

    /// <summary>
    /// #722: 画像 CAPTCHA の写しを返す。
    /// 🔴 <b>パスは固定であり、引数を取らない。</b> 呼び出し側が読む対象を選べるようにすると、
    /// 同居する PVC（<c>Device.dat</c> / <c>OpenD.xml</c>）の読み出しへ一歩で繋がる。
    /// </summary>
    private static IResult GetCaptcha(HttpContext context, IOptions<OpendAuthOptions> options)
    {
        var opt = options.Value;
        if (!CaptchaAvailable(opt)) return Results.NotFound();

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(opt.CaptchaPath);
        }
        catch (IOException)
        {
            // 複写ループが差し替えている最中に読んだ場合。次の要求で取れる。
            return Results.NotFound();
        }

        // 使い捨ての一枚である。中間・ブラウザのどちらにも残さない。
        context.Response.Headers.CacheControl = "no-store";
        return Results.File(bytes, "image/png");
    }

    /// <summary>
    /// #722: 検証コードを OpenD の標準入力へ投入する。
    /// <para>
    /// 順序が効く: <b>本文の上限 → JSON 解釈 → allowlist 検証 → 流量制限 → 書き込み</b>。
    /// 検証を流量制限より<b>前</b>に置くのは、壊れた要求を投げ続けるだけで正当な投入の枠を
    /// 使い切れてしまうのを避けるためである（棄却された要求は 1 バイトも書かない＝資源を消費しない）。
    /// </para>
    /// </summary>
    private static async Task<IResult> PostVerifyAsync(
        HttpContext context,
        IOptions<OpendAuthOptions> options,
        IOpendStdinWriter writer,
        SubmissionRateLimiter limiter,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(LoggerCategory);
        var opt = options.Value;

        var body = await ReadBoundedBodyAsync(context.Request.Body, opt.MaxRequestBodyBytes, context.RequestAborted);
        if (body is null)
        {
            logger.LogWarning("opend-auth: 要求本文が上限（{Limit} バイト）を超えたため読まずに落とした。", opt.MaxRequestBodyBytes);
            return Results.Json(new OpendAuthError("payload_too_large"), statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        VerifyRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<VerifyRequest>(body, RequestJson);
        }
        catch (JsonException)
        {
            // 例外メッセージには本文の断片が入るため、ログにも応答にも出さない。
            return Results.Json(new OpendAuthError("invalid_json"), statusCode: StatusCodes.Status400BadRequest);
        }

        if (request is null)
        {
            return Results.Json(new OpendAuthError("invalid_json"), statusCode: StatusCodes.Status400BadRequest);
        }

        // 🔴 planning#594: **どのコマンドを送るかはサーバが決める。** 判定の源は
        // コンソールの複製から検出した「いま待っているプロンプト」ただ 1 つであり、要求本文ではない。
        // これにより「待機中のプロンプトと食い違う種別」という組み合わせが存在しなくなる。
        var raw = ReadTail(opt.ConsoleLogPath, opt.ConsoleTailBytes, out var consoleAvailable);
        if (!consoleAvailable)
        {
            // **「入力を待っていない」と混ぜない。** ここは状態を取得できていない側である。
            logger.LogWarning("opend-auth: コンソールの複製を読めないため投入を受け付けない。何も書いていない。");
            return Results.Json(new OpendAuthError("console_unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var prompt = ConsoleTail.DetectPrompt(ConsoleTail.Sanitize(raw));
        if (prompt is not VerifyKind.Phone and not VerifyKind.Pic)
        {
            // 入力待ちでないときにコードを流すと、次のプロンプトで消費されて 1 回を食う。
            logger.LogWarning("opend-auth: 検証コードの入力待ちではないため棄却した（prompt={Prompt}）。何も書いていない。",
                ConsoleTail.ToWireValue(prompt) ?? "(なし)");
            return Results.Json(new OpendAuthError("not_waiting"), statusCode: StatusCodes.Status409Conflict);
        }

        if (!OpendConsoleCommand.TryCompose(prompt.Value, request.Code, out var line, out var rejection))
        {
            // 🔴 投入値は載せない。載せてよいのは「なぜ落としたか」だけである。
            logger.LogWarning("opend-auth: 投入を棄却した（理由={Reason}）。何も書いていない。", rejection);
            return Results.Json(new OpendAuthError(ToErrorCode(rejection)), statusCode: StatusCodes.Status400BadRequest);
        }

        if (!limiter.TryAcquire(out var retryAfter))
        {
            logger.LogWarning("opend-auth: 流量制限により棄却した（{Max} 件 / {Window} 秒）。",
                limiter.MaxSubmissions, limiter.Window.TotalSeconds);
            context.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Json(new OpendAuthError("rate_limited"), statusCode: StatusCodes.Status429TooManyRequests);
        }

        var outcome = writer.WriteLine(line);
        if (outcome != StdinWriteOutcome.Written)
        {
            return Results.Json(
                new OpendAuthError(outcome == StdinWriteOutcome.NoReader ? "opend_not_listening" : "stdin_unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // 🔴 記録するのは「受理した事実・時刻・コマンド種別・結果」だけである（planning#594・NFR-05）。
        // コードの値はここにも応答にも載せない。
        logger.LogInformation("opend-auth: 投入を受理した（kind={Kind}）。", ConsoleTail.ToWireValue(prompt));
        return Results.Ok(new VerifyAccepted("accepted", ConsoleTail.ToWireValue(prompt)!));
    }

    /// <summary>
    /// #722 / planning#594: SMS の再送を要求する（<c>req_phone_verify_code</c>・引数なし）。
    /// <para>
    /// 本文を取らない。<b>コードが失効したときの唯一の出口</b>であり、待機中のプロンプトが
    /// <c>phone</c> でも <c>resend</c> でも打てる（失効したコードを待っている状態から抜ける手段が要る）。
    /// </para>
    /// <para>
    /// 🔴 <b>流量制限は投入と同じ枠を使う。</b> 別枠にすると、再送だけを連打して
    /// moomoo 側の SMS 送信枠を使い切れてしまう。
    /// </para>
    /// </summary>
    private static IResult PostResend(
        HttpContext context,
        IOptions<OpendAuthOptions> options,
        IOpendStdinWriter writer,
        SubmissionRateLimiter limiter,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(LoggerCategory);
        var opt = options.Value;

        ReadTail(opt.ConsoleLogPath, opt.ConsoleTailBytes, out var consoleAvailable);
        if (!consoleAvailable)
        {
            logger.LogWarning("opend-auth: コンソールの複製を読めないため再送を受け付けない。何も書いていない。");
            return Results.Json(new OpendAuthError("console_unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!OpendConsoleCommand.TryCompose(VerifyKind.Resend, code: null, out var line, out var rejection))
        {
            logger.LogWarning("opend-auth: 再送を棄却した（理由={Reason}）。何も書いていない。", rejection);
            return Results.Json(new OpendAuthError(ToErrorCode(rejection)), statusCode: StatusCodes.Status400BadRequest);
        }

        if (!limiter.TryAcquire(out var retryAfter))
        {
            logger.LogWarning("opend-auth: 流量制限により再送を棄却した（{Max} 件 / {Window} 秒）。",
                limiter.MaxSubmissions, limiter.Window.TotalSeconds);
            context.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Json(new OpendAuthError("rate_limited"), statusCode: StatusCodes.Status429TooManyRequests);
        }

        var outcome = writer.WriteLine(line);
        if (outcome != StdinWriteOutcome.Written)
        {
            return Results.Json(
                new OpendAuthError(outcome == StdinWriteOutcome.NoReader ? "opend_not_listening" : "stdin_unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("opend-auth: 再送を受理した。");
        return Results.Ok(new VerifyAccepted("accepted", "resend"));
    }

    /// <summary>棄却理由 → 応答に載せる符号。<b>入力そのものは決して含めない。</b></summary>
    private static string ToErrorCode(VerifyRejection rejection) => rejection switch
    {
        VerifyRejection.UnknownKind => "unknown_kind",
        VerifyRejection.MissingCode => "missing_code",
        VerifyRejection.UnexpectedCode => "unexpected_code",
        VerifyRejection.CodeTooLong => "code_too_long",
        _ => "malformed_code",
    };

    /// <summary>
    /// 上限バイト数まで読み、超えたら <c>null</c> を返す（＝読み切らずに諦める）。
    /// <para>
    /// Kestrel の <c>MaxRequestBodySize</c> も別に設定しているが、こちらは
    /// <b>ホストの実装に依らず同じ振る舞いを保証する</b>ためのものである
    /// （試験用 <c>TestServer</c> は Kestrel の上限を持たない）。
    /// </para>
    /// </summary>
    private static async Task<string?> ReadBoundedBodyAsync(Stream body, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await body.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (n == 0) break;
            read += n;
        }

        if (read > maxBytes) return null;
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>コンソール複製の末尾を最大 <paramref name="maxBytes"/> バイト読む。</summary>
    private static string ReadTail(string path, int maxBytes, out bool available)
    {
        available = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            available = true;
            var length = stream.Length;
            var start = length > maxBytes ? length - maxBytes : 0;
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[(int)(length - start)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // OpenD がまだ起動していない（または emptyDir が空）。異常ではない。
            return string.Empty;
        }
        catch (IOException)
        {
            available = false;
            return string.Empty;
        }
    }

    /// <summary>画像 CAPTCHA の写しが「読める大きさで存在する」か。</summary>
    private static bool CaptchaAvailable(OpendAuthOptions options)
    {
        var info = new FileInfo(options.CaptchaPath);
        return info.Exists && info.Length > 0 && info.Length <= options.MaxCaptchaBytes;
    }
}
