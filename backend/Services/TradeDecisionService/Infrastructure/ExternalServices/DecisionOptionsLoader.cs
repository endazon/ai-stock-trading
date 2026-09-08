using System.Globalization;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Configuration;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, IADR-0039, IADR-0212, IADR-0278, IADR-0313, #571, #567: Decision:* から多数決・二段オーケストレーションの構成を読む。
// VoteCount 未設定・不正値は Default（1 票）に倒す安全側フォールバック。
// 🔴 EnableScreening は Default（false）ではなく true をベースラインにする（IADR-0278）——
// #335（IADR-0212）が層別 purpose を配線済みで、基盤 LlmGateway への trade-decision-screening 登録
// （#571・microservices-platform 側）を前提に二段判断を既定で有効化する。構成で明示的に
// Decision:EnableScreening=false を与えれば従来どおり無効化できる（上書き経路は維持）。
// 🔴 ScreeningContextBudgetChars も同様に**既定で有効**にした（IADR-0313 決定2。既定 150,000 文字）——
// 縮退制御は IADR-0247 で opt-in として出荷され、以後 1 度も動いていなかった。無効化は "0" / "off" を明示する。
// Program.cs から利用し単体テストする。
public static class DecisionOptionsLoader
{
    public static DecisionOrchestrationOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Decision");
        // IADR-0278: 構成既定は DecisionOrchestrationOptions.Default そのものではなく EnableScreening=true。
        // Default レコード自体（VoteCount=1・スクリーニング無効）は単体テストの便宜的基準値として不変。
        var options = DecisionOrchestrationOptions.Default with { EnableScreening = true };

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

        // #337, IADR-0247 / #567, IADR-0313 決定3: スクリーニング入力のコンテキスト予算（縮退制御）。
        options = options with { ScreeningContextBudgetChars = ResolveScreeningBudget(section["ScreeningContextBudgetChars"]) };

        // 空文字・未設定は null（モデル未指定＝ゲートウェイ既定）に正規化する。
        return options with
        {
            PrimaryModel = Normalize(section["PrimaryModel"]),
            SecondaryModel = Normalize(section["SecondaryModel"]),
        };
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // FR-02, FR-04, #567, IADR-0313 決定3: 予算の解決。**既定は有効**（DefaultScreeningContextBudgetChars）であり、
    // 統制を落とす操作は明示に限る。
    //
    //   未設定・空文字     → 既定 150,000（縮退制御あり）
    //   正の整数           → その値（運用の上書き）
    //   "0" / "off"        → null（**明示的な無効化**。従来プロンプト＝IADR-0072 決定2 へ戻す）
    //   負数・非数値       → 既定（不正値は**統制を残す側**へ倒す）
    //
    // 🔴 **不正値の倒し先が IADR-0247 の頃から反転している。** 既定が無効だった頃は「不正値 → 制御なし」が
    // 安全側だったが、既定が有効になった以上、不正値で統制が黙って外れるほうが危険である。
    private static int? ResolveScreeningBudget(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars;
        }

        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget))
        {
            return DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars;
        }

        return budget switch
        {
            0 => null,                                                            // 明示的な無効化
            > 0 => budget,                                                        // 運用の上書き
            _ => DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars,  // 負数は不正値
        };
    }
}
