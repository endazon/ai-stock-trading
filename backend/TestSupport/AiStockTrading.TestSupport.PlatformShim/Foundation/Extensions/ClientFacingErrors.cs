using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// NFR-06, IADR-0503, #1206: 群のフィルタ・gRPC の写しが 400 / INVALID_ARGUMENT の応答へ載せる例外の文言を、
// **自前のコードが投げた例外に限る**。フレームワーク・EF・ドライバが投げた例外の文言は内部構造（テーブル名・制約名・接続先など）を
// 含み得るので固定文言に置き換え、元の例外はログへ出す（状態コードは変えない）。
//
// 判定: 例外のスタックの先頭から、CoreLib（`ArgumentException.ThrowIfNullOrWhiteSpace` 等の送出用の補助関数）のフレームを飛ばした
// 最初のフレームが、呼び出し側のサービスのアセンブリか `AiStockTrading.Shared.*` なら自前の送出とみなす。スタックの無い例外
// （投げられていない）・宣言型の分からないフレーム（動的メソッド）は自前とみなさない（安全側＝固定文言）。
public static class ClientFacingErrors
{
    /// <summary>自前の送出でない入力系の例外の代わりに応答へ載せる固定文言。</summary>
    public const string InvalidRequestMessage = "要求の内容が正しくありません。";

    /// <summary>自前とみなす共有アセンブリの名前の接頭辞。</summary>
    public const string SharedAssemblyPrefix = "AiStockTrading.Shared.";

    private static readonly Assembly CoreLib = typeof(object).Assembly;

    /// <summary>
    /// 例外が <paramref name="serviceAssembly"/> か <c>AiStockTrading.Shared.*</c> のコードから投げられたか。
    /// </summary>
    public static bool IsRaisedByOwnCode(Exception exception, Assembly serviceAssembly)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(serviceAssembly);

        foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
        {
            var assembly = frame.GetMethod()?.DeclaringType?.Assembly;
            if (assembly is null)
                return false;
            if (assembly == CoreLib)
                continue;

            return assembly == serviceAssembly
                || (assembly.GetName().Name?.StartsWith(SharedAssemblyPrefix, StringComparison.Ordinal) ?? false);
        }

        return false;
    }

    /// <summary>
    /// 応答へ載せる文言。自前の送出なら例外の文言、そうでなければ <paramref name="fixedMessage"/>（元の例外は Warning でログへ出す）。
    /// </summary>
    public static string MessageFor(
        Exception exception, Assembly serviceAssembly, ILogger logger, string fixedMessage = InvalidRequestMessage)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (IsRaisedByOwnCode(exception, serviceAssembly))
            return exception.Message;

        logger.LogWarning(
            exception,
            "自前のコード以外が投げた {ExceptionType} の文言を応答へ載せず、固定文言に置き換えました（IADR-0503）。",
            exception.GetType().FullName);
        return fixedMessage;
    }
}
