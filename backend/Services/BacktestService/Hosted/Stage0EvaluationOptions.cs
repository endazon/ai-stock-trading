using System.Globalization;
using BacktestService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace BacktestService.Hosted;

// FR-15, FR-20, ADR-0008, ADR-0033, #688, IADR-0310 決定1: Stage 0 判定の定時駆動の構成（セクション "Backtest:Stage0"）。
//
// 🔴 **既定は無効である**（fail-safe）。無効なら巡回もバー取得も publish も一切起きない。
// 有効化してよいのは、少なくとも次が揃ってからである（#632, IADR-0329）。
//   - 過去データ源（Backtest:BarData:Provider）が実運用に足る状態であること（ADR-0023 決定5 の未確認 2 点。**未了**）
//   - 評価対象に本番戦略（`Strategy=recorded-replay`）を選び、**その記録が在る**こと
//     （戦略そのものは #632 / IADR-0318 で**載っている**。未了なのは記録の取得であり、ADR-0033 決定5 の
//      見積り提示→利用者承認を要する）
//   - 学習カットオフ日（LlmTrainingCutoff）が構成されていること（**ADR-0037 決定2 で `2026-01-31` が登録済み**）
// **過去データと記録が揃うまでは、有効化しても verdict は必ず不合格になる**（fail-closed。経路の確認にはなる）。
public sealed class Stage0EvaluationOptions
{
    public const string SectionName = "Backtest:Stage0";

    /// <summary>定時駆動を行うか。**既定 false**（未設定・空はすべて無効）。</summary>
    public bool Enabled { get; set; }

    /// <summary>巡回間隔（秒）。既定 86,400＝日次。60 未満は 60 へクランプする（過去データ源を叩き続けないため）。</summary>
    public int IntervalSeconds { get; set; } = 86_400;

    /// <summary>評価期間の遡り日数（当日から遡る暦日）。既定 365。1 未満は 1 へクランプする。</summary>
    public int LookbackDays { get; set; } = 365;

    /// <summary>
    /// LLM 学習カットオフ日（`YYYY-MM-DD`）。**未設定・解釈不能は「未充足」として扱う**
    /// （ADR-0033 決定3。未設定を充足へ倒さない）。値は ADR-0037 決定2 が計画へ登録した `2026-01-31` であり、
    /// **構成から受け取る**（コード既定にしない —— 未設定と登録済みが区別できなくなるため）。
    /// </summary>
    public string? LlmTrainingCutoff { get; set; }

    /// <summary>評価対象の銘柄。空なら取得対象が無く、バーは 0 本になる（＝fail-closed の経路）。</summary>
    public IReadOnlyList<SymbolEntry> Symbols { get; init; } = [];

    /// <summary>
    /// FR-04, FR-15, ADR-0033, #632, IADR-0318 決定3: 評価対象の戦略。
    /// <para>
    /// <c>placeholder</c>（**既定**）は #688 / IADR-0310 の駆動確認用であり、verdict は不合格固定である。
    /// <c>recorded-replay</c> は ADR-0033 の記録再生戦略で、**記録が構成と整合するときだけ**本物の判定器へ進む。
    /// 未知の値・空は既定（<c>placeholder</c>）へ倒す —— 綴り違いで本番戦略が黙って走らないようにするため、
    /// 未知の値はログへ警告を出す（<see cref="ResolveStrategy"/>）。
    /// </para>
    /// </summary>
    public string? Strategy { get; set; }

    /// <summary>AI 判断の記録の供給（<c>recorded-replay</c> のときだけ使う）。</summary>
    public RecordingOptions Recording { get; init; } = new();

    /// <summary>戦略識別子（構成値）。</summary>
    public const string PlaceholderStrategyName = "placeholder";

    /// <summary>戦略識別子（構成値）。</summary>
    public const string RecordedReplayStrategyName = "recorded-replay";

    /// <summary>
    /// 実効の戦略名。未設定・空・未知はすべて <see cref="PlaceholderStrategyName"/>（＝不合格固定）へ倒す。
    /// </summary>
    public string ResolveStrategy() =>
        string.Equals(Strategy?.Trim(), RecordedReplayStrategyName, StringComparison.OrdinalIgnoreCase)
            ? RecordedReplayStrategyName
            : PlaceholderStrategyName;

    /// <summary>構成に戦略名が書かれているが解釈できないか（＝綴り違いの疑い。警告の材料）。</summary>
    public bool HasUnknownStrategy() =>
        !string.IsNullOrWhiteSpace(Strategy)
        && !string.Equals(Strategy.Trim(), PlaceholderStrategyName, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Strategy.Trim(), RecordedReplayStrategyName, StringComparison.OrdinalIgnoreCase);

    /// <summary>実効の巡回間隔。</summary>
    public TimeSpan EffectiveInterval() => TimeSpan.FromSeconds(Math.Max(60, IntervalSeconds));

    /// <summary>評価期間 [from, to]（to は当日）。</summary>
    public (DateOnly From, DateOnly To) EvaluationWindow(DateOnly today) =>
        (today.AddDays(-Math.Max(1, LookbackDays)), today);

    /// <summary>
    /// LLM 学習カットオフ日。未設定・解釈不能は null（＝未充足へ倒す）。
    /// </summary>
    public DateOnly? ParseLlmTrainingCutoff() =>
        DateOnly.TryParse(LlmTrainingCutoff, CultureInfo.InvariantCulture, DateTimeStyles.None, out var cutoff)
            ? cutoff
            : null;

    /// <summary>
    /// 構成の銘柄から取得対象のユニバースを組む。
    /// **上場/廃止区間（PIT メンバーシップ）は構成では持たない** —— 生存者バイアスを排した実ユニバースの
    /// 供給は本番戦略（ADR-0033）と同時に要る。ここでは「期間中ずっと構成銘柄」として扱う
    /// （取得対象が広がる向きであり、合格側へ倒れる余地は無い）。
    /// </summary>
    public SecurityUniverse ToUniverse() =>
        new([.. Symbols
            .Where(e => !string.IsNullOrWhiteSpace(e.Symbol))
            .Select(e => new UniverseMembership(e.Symbol!.Trim(), e.Market, DateOnly.MinValue, DelistedOn: null))]);

    /// <summary>構成バインド用（Market は列挙名でバインドされる）。</summary>
    public sealed class SymbolEntry
    {
        public string? Symbol { get; set; }

        public Market Market { get; set; }
    }

    /// <summary>
    /// FR-04, FR-15, ADR-0033 決定2, #632, IADR-0318: AI 判断の記録の供給（<c>Backtest:Stage0:Recording</c>）。
    /// **未設定なら記録なし**＝合格 verdict は出ない。
    /// </summary>
    public sealed class RecordingOptions
    {
        /// <summary>記録集合（JSON）のパス。未設定・存在しない・解釈不能はすべて「記録なし」へ倒す。</summary>
        public string? Path { get; set; }
    }
}
