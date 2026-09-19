using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.GetSizingContext;

// FR-04, FR-10, IADR-0029: 取引判断のサイジングに供給する文脈（設定＋ポートフォリオ状態から導出）。
// 取引判断（#11）の SizingContext と同形。段階/日次残枠は負値を 0 にクランプ済み。
// FR-10, #869, ADR-0041 決定2, IADR-0354: equity（基準資金）は**ブローカーの口座照会に由来する**。
// 🔴 **null は「口座を照会できていない」を意味し、残枠も解決できない**（新規建ては発注審査で止まる）。
// 取引判断は残枠 null を「0」として扱い、**新規建てを提案しない**（0 で埋めて渡すと「枠がある」と読める）。
public sealed record SizingContextView(
    decimal? Capital,
    decimal? StageCapitalRemaining,
    decimal? DailyOrderRemaining,
    int ConsecutiveLosses,
    decimal DrawdownRatio,
    BrokerProvider Mode,
    RiskLimitSettings Limits,
    // FR-04, FR-10, ADR-0040 決定1, #854, IADR-0351 決定1: 損切りの実行機構の設定（S0〜S3）。取引判断が判断プロンプトの
    // 「保護の状態」に用いる（保有中の建玉に自動の損切りが効く前提に立ってよいかを LLM へ偽らずに伝えるため）。
    // 末尾の既定値つき項目として足し、既存の生成箇所を変えない（RiskStatusView と同じ足し方・IADR-0342 決定2）。
    // 🔴 これは**設定**であり、個々の建玉の逆指値が現在有効かどうかではない（その射影は無い）。
    StopLossExecutionMethod StopLossMethod = StopLossExecutionMethod.BrokerStopOrder);
