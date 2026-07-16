namespace AiStockTrading.Notification.Application.State;

// FR-14, UC-06: 本 PR で受け付けるコマンド種別。詳細設計07 のコマンド体系のうち kill switch のみを扱う。
public enum BotCommandKind
{
    // 未知・解析不能。呼び出し側は必ず拒否する（暗黙実行しない）。
    Unknown,

    // /killswitch: 全停止の起動（確認ボタン＋確認フレーズを要する）。
    KillSwitchEngage,

    // /killswitch off: 全停止の解除（確認ステップを要する）。
    KillSwitchDisengage,
}

// FR-14: 解析済みコマンド。
public sealed record BotCommand(BotCommandKind Kind)
{
    public static readonly BotCommand Unknown = new(BotCommandKind.Unknown);
}
