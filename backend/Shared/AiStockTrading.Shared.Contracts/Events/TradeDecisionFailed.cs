using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// 🔴 NFR, FR-04, FR-11, #1111, IADR-0483 決定1・2: 取引判断の**最中の例外**が、再試行の後の**最終の失敗**になった事実（1 回の最終の失敗につき 1 件）。
//
// 従来はログにしか残らず、Pod の再起動で消えた（定時の経路は銘柄ごとに捕まえて次へ進み、価格変動の経路は再試行の後に
// RabbitMQ の退避先へ移る。#1092 段 2〔IADR-0462〕の対象外）。裁定（2026-10-02）は「最小限で残す」である。
//
//   - 載せるのは **例外の型名・発生源（定時か価格変動か）・銘柄・時刻だけ**。🔴 **例外のメッセージとスタックは載せない**
//     （秘密情報・口座 ID を含み得る。payload は監査台帳に 7 年残る）。型名は総称型なら定義の名前（型引数・アセンブリ名を含めない）。
//   - 🔴 **再試行のたびには出さない。** 定時の経路は銘柄ごとの捕捉が最終（その巡回で同じ銘柄を再試行しない）、価格変動の経路は
//     配送回数が最大配送回数に達した配送の失敗だけが最終（この失敗で `<queue>_error` へ移る）。
//   - CycleTrigger: `BusinessMetrics.TriggerScheduled` / `TriggerPriceMovement` の語彙（TradeDecisionForgoneBeforeLlm と同じ）。
//   - 発行はランタイムの MessageBus から行う（ハンドラが例外で終わると、その処理中に発行したメッセージは捨てられる。IADR-0129）。
//   - 監査台帳だけが購読する（通知しない）。
public record TradeDecisionFailed(
    Guid EventId,
    string Symbol,
    Market Market,
    string CycleTrigger,
    string ExceptionType,
    DateTimeOffset OccurredAt);
