using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace AiStockTrading.Shared.Contracts.Errors;

/// <summary>
/// NFR-06, IADR-0509, #1230: 400 / INVALID_ARGUMENT の応答へ<b>文言を載せてよい</b> <see cref="ArgumentException"/> の明示の印。
/// <para>
/// 応答を組む側（<c>ClientFacingErrors.MessageFor</c>）は、<b>この印のある例外だけ</b>文言を載せ、印の無いもの
/// （第三者のライブラリ・CoreLib・フレームワークの送出、印を付けていない自前の送出）は固定文言にする。
/// 判定は例外の中身（<see cref="Exception.Data"/>）だけで決まり、スタック・JIT の段階・インライン化に依らない
/// （IADR-0503 のスタックの先頭のフレームによる判定は tier-1 のインライン化で揺れた）。
/// </para>
/// <para>
/// <b>型ではなく <see cref="Exception.Data"/> の印にした理由</b>（IADR-0509 決定 1）: 自前の検証は
/// <see cref="ArgumentException"/> だけでなく <see cref="ArgumentOutOfRangeException"/>（<c>ActualValue</c> つき）や
/// CoreLib の補助（null で <see cref="ArgumentNullException"/>）も使う。印なら例外の型・<c>ParamName</c>・文言を変えずに付けられ、
/// 利用者が読む文言（Discord・画面）を 1 文字も変えない。Domain から使えるよう .NET 標準だけで書き、<c>Shared.Contracts</c> に置く。
/// </para>
/// </summary>
public static class ClientVisibleArgument
{
    /// <summary><see cref="Exception.Data"/> に置く印のキー。値は <see langword="true"/>。</summary>
    public const string DataKey = "AiStockTrading.ClientVisibleMessage";

    /// <summary>
    /// 例外へ「利用者へ見せてよい文言」の印を付けて同じ例外を返す（<c>throw new ArgumentException(…).ClientVisible();</c>）。
    /// 型・文言・<c>ParamName</c> は変えない。
    /// </summary>
    public static TException ClientVisible<TException>(this TException exception)
        where TException : ArgumentException
    {
        ArgumentNullException.ThrowIfNull(exception);
        exception.Data[DataKey] = true;
        return exception;
    }

    /// <summary>印のある <see cref="ArgumentException"/>（派生を含む）か。内側の例外は見ない。</summary>
    public static bool IsClientVisible(this Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is ArgumentException && exception.Data[DataKey] is true;
    }

    /// <summary>
    /// <see cref="ArgumentException.ThrowIfNullOrWhiteSpace"/> と同じ検査・同じ例外（null は <see cref="ArgumentNullException"/>）を、
    /// 印を付けて投げる。利用者が入力する欄（理由・銘柄コードなど）の空欄検査に使う。
    /// </summary>
    public static void ThrowIfNullOrWhiteSpace(
        [NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (!string.IsNullOrWhiteSpace(argument))
            return;

        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(argument, paramName);
        }
        catch (ArgumentException e)
        {
            throw e.ClientVisible();
        }

        throw new InvalidOperationException("ArgumentException.ThrowIfNullOrWhiteSpace が投げなかった（前提の崩れ）。");
    }
}
