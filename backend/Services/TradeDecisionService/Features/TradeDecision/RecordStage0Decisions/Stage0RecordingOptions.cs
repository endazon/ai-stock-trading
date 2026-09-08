using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-04, FR-15, NFR（費用）, ADR-0033 決定3/決定4/決定5, #632, IADR-0318:
// Stage 0 記録の構成（セクション "Stage0Recording"）。
//
// 🔴 **既定は「実行不能」である。** `Enabled` が既定 false であるうえ、承認値（`ApprovedEstimateJpy` /
// `ApprovedVoteCount`）が見積りと一致しなければ実行しない。ADR-0033 決定5 は
// 「実行前に見積りを提示し、利用者が承認しない限り実行しない」を必須としており、
// **承認を「構成値が見積りと一致すること」で表す**（実行の前に人が値を書き入れる以外に一致させる手段が無い）。
public sealed class Stage0RecordingOptions
{
    public const string SectionName = "Stage0Recording";

    /// <summary>記録の実行を行うか。**既定 false**（無効なら LLM を 1 回も呼ばない）。</summary>
    public bool Enabled { get; set; }

    /// <summary>記録対象期間の開始日（`YYYY-MM-DD`）。</summary>
    public string? From { get; set; }

    /// <summary>記録対象期間の終了日（`YYYY-MM-DD`）。</summary>
    public string? To { get; set; }

    /// <summary>
    /// LLM 学習カットオフ日（`YYYY-MM-DD`）。ADR-0033 決定3。
    /// **未設定なら記録しない** —— カットオフ日の分からない記録は、再生側で検証条件①を主張できない。
    /// </summary>
    public string? LlmTrainingCutoff { get; set; }

    /// <summary>記録対象の銘柄。</summary>
    public IReadOnlyList<SymbolEntry> Symbols { get; init; } = [];

    /// <summary>
    /// 多数決の実行回数（ADR-0033 決定4）。**利用者が決定5 の見積りと同時に承認する値**であり、
    /// <see cref="ApprovedVoteCount"/> と一致しなければ実行しない。
    /// </summary>
    public int VoteCount { get; set; } = 1;

    /// <summary>1 日あたりの判断回数（見積りの式の項）。現行の記録器は 1 回で走る。</summary>
    public int DecisionsPerDay { get; set; } = 1;

    /// <summary>見積りに用いる 1 判断あたりの入力トークン量（**実測値を入れる**。既定 0＝見積り 0 円＝実行不能）。</summary>
    public int InputTokensPerDecision { get; set; }

    /// <summary>見積りに用いる 1 判断あたりの出力トークン量（同上）。</summary>
    public int OutputTokensPerDecision { get; set; }

    /// <summary>
    /// 利用者が承認した見積り額（円）。**算出した見積りと一致しなければ実行しない。**
    /// 既定 null＝未承認。
    /// </summary>
    public decimal? ApprovedEstimateJpy { get; set; }

    /// <summary>利用者が承認した多数決回数。<see cref="VoteCount"/> と一致しなければ実行しない。既定 null＝未承認。</summary>
    public int? ApprovedVoteCount { get; set; }

    /// <summary>記録の書き出し先（JSON）。未設定なら書き出さない（＝記録が残らないので実行しない）。</summary>
    public string? OutputPath { get; set; }

    /// <summary>記録に用いるモデル識別子（ADR-0011 のピン留めモデル）。未設定ならゲートウェイの用途別割当に委ねる。</summary>
    public string? Model { get; set; }

    /// <summary>期間 [From, To]。いずれかが解釈不能なら null（＝実行しない）。</summary>
    public (DateOnly From, DateOnly To)? ParsePeriod()
    {
        if (!TryParseDate(From, out var from) || !TryParseDate(To, out var to) || from > to)
            return null;
        return (from, to);
    }

    /// <summary>LLM 学習カットオフ日。未設定・解釈不能は null（＝実行しない）。</summary>
    public DateOnly? ParseLlmTrainingCutoff() => TryParseDate(LlmTrainingCutoff, out var cutoff) ? cutoff : null;

    /// <summary>記録対象の銘柄（空・空白の記述は捨てる）。</summary>
    public IReadOnlyList<(string Symbol, Market Market)> ResolveSymbols() =>
        [.. Symbols
            .Where(e => !string.IsNullOrWhiteSpace(e.Symbol))
            .Select(e => (e.Symbol!.Trim(), e.Market))
            .Distinct()];

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>構成バインド用（Market は列挙名でバインドされる）。</summary>
    public sealed class SymbolEntry
    {
        public string? Symbol { get; set; }

        public Market Market { get; set; }
    }
}
