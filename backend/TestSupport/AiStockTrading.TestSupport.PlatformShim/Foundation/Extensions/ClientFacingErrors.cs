using AiStockTrading.Shared.Contracts.Errors;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// NFR-06, IADR-0503, #1206: 群のフィルタ・gRPC の写しが 400 / INVALID_ARGUMENT の応答へ載せる例外の文言を、
// **載せてよいと明示された例外に限る**。フレームワーク・EF・ドライバ・第三者のライブラリが投げた例外の文言は内部構造
// （テーブル名・制約名・接続先・キーの値など）を含み得るので固定文言に置き換え、元の例外はログへ出す（状態コードは変えない）。
//
// NFR-06, IADR-0509, #1230: 判定は**明示の印**（ClientVisibleArgument。Exception.Data の印）だけで行う。スタックの先頭のフレームで
// 「自前の送出か」を見る判定（IADR-0503 決定 2）は、段階コンパイルの tier-1 で第三者の小さなメソッドが自前のフレームへインライン化
// されると結果が変わったため廃した。印の無いものは、自前のコードの送出（ThrowIfNullOrWhiteSpace 等の CoreLib の補助を含む）でも固定文言になる。
//
// SC-01, SC-02, NFR-06, IADR-0586, #1472: 印のある文言からは .NET が付ける末尾（` (Parameter 'x')`・`Actual value was N.` の行）を落とす。
// 画面（IADR-0585）と Discord Bot（gRPC の写しも本関数を通る）は文言をそのまま出すため、利用者に引数名と英語が見えていた。
public static class ClientFacingErrors
{
    /// <summary>印の無い入力系の例外の代わりに応答へ載せる固定文言。</summary>
    public const string InvalidRequestMessage = "要求の内容が正しくありません。";

    /// <summary>
    /// 応答へ載せる文言。<see cref="ClientVisibleArgument"/> の印があれば例外の文言（.NET が付ける引数名・実際の値の末尾は落とす）、
    /// 無ければ <paramref name="fixedMessage"/>（元の例外は Warning でログへ出す）。
    /// </summary>
    public static string MessageFor(Exception exception, ILogger logger, string fixedMessage = InvalidRequestMessage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(logger);
        if (exception.IsClientVisible())
            return WithoutFrameworkSuffix((ArgumentException)exception);

        logger.LogWarning(
            exception,
            "利用者へ見せる印の無い {ExceptionType} の文言を応答へ載せず、固定文言に置き換えました（IADR-0509）。",
            exception.GetType().FullName);
        return fixedMessage;
    }

    // IADR-0586 決定 1: .NET が Message の末尾へ付ける部分は、同じ ParamName（と ActualValue）で文言を空にした例外の Message そのものである。
    // 英語の文字列を直書きせず、同じ実行時（同じ文化の資源）から末尾を得て、一致したときだけ落とす。例外そのものは変えない。
    private static string WithoutFrameworkSuffix(ArgumentException exception)
    {
        var message = exception.Message;
        var suffix = exception switch
        {
            ArgumentOutOfRangeException { ActualValue: not null } outOfRange =>
                new ArgumentOutOfRangeException(outOfRange.ParamName, outOfRange.ActualValue, string.Empty).Message,
            { ParamName: { Length: > 0 } paramName } => new ArgumentException(string.Empty, paramName).Message,
            _ => string.Empty,
        };

        return suffix.Length > 0 && message.Length > suffix.Length && message.EndsWith(suffix, StringComparison.Ordinal)
            ? message[..^suffix.Length].TrimEnd()
            : message;
    }
}
