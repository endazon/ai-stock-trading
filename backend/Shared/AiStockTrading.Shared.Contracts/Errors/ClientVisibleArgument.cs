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
    /// <see cref="ArgumentException.ThrowIfNullOrWhiteSpace"/> と同じ検査・同じ型の例外（null は <see cref="ArgumentNullException"/>、
    /// 空・空白は <see cref="ArgumentException"/>。<c>ParamName</c> も同じ）を、<b>日本語の文言</b>「<paramref name="label"/>は必須です。」で
    /// 印を付けて投げる。利用者が入力する欄（理由・銘柄コードなど）の空欄検査に使う。
    /// <para>
    /// SC-01, SC-02, IADR-0586 決定 2, #1472: 以前は CoreLib の例外をそのまま投げたため、英語の既定文言
    /// （<c>The value cannot be an empty string …</c>）が画面と Discord に出ていた。<paramref name="label"/> は項目の日本語名（例: 「理由」）。
    /// </para>
    /// </summary>
    public static void ThrowIfNullOrWhiteSpace(
        [NotNull] string? argument,
        string label,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (!string.IsNullOrWhiteSpace(argument))
            return;

        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var message = $"{label}は必須です。";
        if (argument is null)
            throw new ArgumentNullException(paramName, message).ClientVisible();

        throw new ArgumentException(message, paramName).ClientVisible();
    }
}
