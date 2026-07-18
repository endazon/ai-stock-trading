namespace AiStockTrading.Notification.Application.State;

// FR-14, UC-06, ADR-0009: 受け付けるコマンド種別。詳細設計07 のコマンド体系のうち kill switch と
// 一時停止（pause/resume）・稼働状態照会（status）を扱う。
public enum BotCommandKind
{
    // 未知・解析不能。呼び出し側は必ず拒否する（暗黙実行しない）。
    Unknown,

    // /killswitch: 全停止の起動（確認ボタン＋確認フレーズを要する）。
    KillSwitchEngage,

    // /killswitch off: 全停止の解除（確認ステップを要する）。
    KillSwitchDisengage,

    // /pause: 新規建ての一時停止（確認ボタンのみ・フレーズ不要。kill switch より軽い統制）。
    Pause,

    // /resume: 一時停止の解除（確認ボタンのみ）。pause のみ解除する（kill switch・ロックアウトは解除しない）。
    Resume,

    // /status: 稼働状態の参照（表示専用・副作用なし）。
    Status,
}

// FR-14: 解析済みコマンド。
public sealed record BotCommand(BotCommandKind Kind)
{
    public static readonly BotCommand Unknown = new(BotCommandKind.Unknown);
}
