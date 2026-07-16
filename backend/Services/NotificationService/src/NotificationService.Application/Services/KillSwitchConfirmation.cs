using AiStockTrading.Notification.Application.State;

namespace AiStockTrading.Notification.Application.Services;

// FR-14, UC-06, IADR-0063 決定5: 高リスク操作の確認ステップ（詳細設計07「認証・認可」層4）。
// kill switch 起動は確認ボタン（2段階）に加えて**確認フレーズの入力**を必須とする。
//
// 安全既定: 確認フレーズが**未設定なら起動を拒否する**。設定漏れで誤爆防止の閂が外れないようにする
// （「未設定＝フレーズ不要」にしない）。照合は前後空白と大小文字のみ正規化し、それ以外は厳密一致とする。
public static class KillSwitchConfirmation
{
    public static ConfirmationResult Verify(string? entered, DiscordBotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 未設定は「フレーズ不要」ではなく拒否（安全既定）。
        if (string.IsNullOrWhiteSpace(options.KillSwitchConfirmationPhrase))
            return ConfirmationResult.Rejected("確認フレーズが未設定のため起動しない（安全既定）");

        if (string.IsNullOrWhiteSpace(entered))
            return ConfirmationResult.Rejected("確認フレーズが入力されていない");

        var normalizedEntered = entered.Trim();
        var expected = options.KillSwitchConfirmationPhrase.Trim();

        return string.Equals(normalizedEntered, expected, StringComparison.OrdinalIgnoreCase)
            ? ConfirmationResult.Confirmed
            : ConfirmationResult.Rejected("確認フレーズが一致しない");
    }
}

// FR-14: 確認ステップの結果。
public sealed record ConfirmationResult(bool IsConfirmed, string Reason)
{
    public static readonly ConfirmationResult Confirmed = new(true, "確認済み");

    public static ConfirmationResult Rejected(string reason) => new(false, reason);
}
