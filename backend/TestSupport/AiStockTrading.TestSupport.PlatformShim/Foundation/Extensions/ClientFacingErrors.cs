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
public static class ClientFacingErrors
{
    /// <summary>印の無い入力系の例外の代わりに応答へ載せる固定文言。</summary>
    public const string InvalidRequestMessage = "要求の内容が正しくありません。";

    /// <summary>
    /// 応答へ載せる文言。<see cref="ClientVisibleArgument"/> の印があれば例外の文言、無ければ <paramref name="fixedMessage"/>
    /// （元の例外は Warning でログへ出す）。
    /// </summary>
    public static string MessageFor(Exception exception, ILogger logger, string fixedMessage = InvalidRequestMessage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(logger);
        if (exception.IsClientVisible())
            return exception.Message;

        logger.LogWarning(
            exception,
            "利用者へ見せる印の無い {ExceptionType} の文言を応答へ載せず、固定文言に置き換えました（IADR-0509）。",
            exception.GetType().FullName);
        return fixedMessage;
    }
}
