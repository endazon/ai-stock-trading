using System.Globalization;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Configuration;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, IADR-0039: Decision:* から多数決・二段オーケストレーションの構成を読む。
// 未設定・不正値は Default（1 票・スクリーニング無効＝現行挙動）に倒す安全側フォールバック。Program.cs から利用し単体テストする。
public static class DecisionOptionsLoader
{
    public static DecisionOrchestrationOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Decision");
        var options = DecisionOrchestrationOptions.Default;

        // VoteCount は 1 以上（不正・非数値・1 未満は既定 1 のまま＝安全側で現行挙動を保つ）。
        if (int.TryParse(section["VoteCount"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var voteCount)
            && voteCount >= 1)
        {
            options = options with { VoteCount = voteCount };
        }

        if (bool.TryParse(section["EnableScreening"], out var enableScreening))
        {
            options = options with { EnableScreening = enableScreening };
        }

        // #337, IADR-0247: スクリーニング入力のコンテキスト予算。未設定・不正・非正値は null
        // （縮退制御なし＝現行プロンプト）に倒す安全側フォールバック。
        if (int.TryParse(
                section["ScreeningContextBudgetChars"], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var budget)
            && budget > 0)
        {
            options = options with { ScreeningContextBudgetChars = budget };
        }

        // 空文字・未設定は null（モデル未指定＝ゲートウェイ既定）に正規化する。
        return options with
        {
            PrimaryModel = Normalize(section["PrimaryModel"]),
            SecondaryModel = Normalize(section["SecondaryModel"]),
        };
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
