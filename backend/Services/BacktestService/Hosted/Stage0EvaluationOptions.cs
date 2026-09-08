using System.Globalization;
using BacktestService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace BacktestService.Hosted;

// FR-15, FR-20, ADR-0008, ADR-0033, #688, IADR-0310 決定1: Stage 0 判定の定時駆動の構成（セクション "Backtest:Stage0"）。
//
// 🔴 **既定は無効である**（fail-safe）。無効なら巡回もバー取得も publish も一切起きない。
// 有効化してよいのは、少なくとも次が揃ってからである。
//   - 過去データ源（Backtest:BarData:Provider）が実運用に足る状態であること（ADR-0023 決定5 の未確認 2 点）
//   - 本番戦略（ADR-0033 の AI 判断の記録・再生）が載っていること
// **どちらも未了である現在、有効化しても verdict は必ず不合格になる**（駆動経路の確認にしか使えない）。
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
    /// （ADR-0033 決定3。カットオフ日の供給元は計画側に未登録であり、未設定を充足へ倒さない）。
    /// </summary>
    public string? LlmTrainingCutoff { get; set; }

    /// <summary>評価対象の銘柄。空なら取得対象が無く、バーは 0 本になる（＝fail-closed の経路）。</summary>
    public IReadOnlyList<SymbolEntry> Symbols { get; init; } = [];

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
}
