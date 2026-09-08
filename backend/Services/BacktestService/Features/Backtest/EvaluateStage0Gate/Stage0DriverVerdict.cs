using BacktestService.Domain;

namespace BacktestService.Features.Backtest.EvaluateStage0Gate;

// FR-15, FR-20, ADR-0008, ADR-0033, #688, IADR-0310: 定時駆動（Hosted/Stage0EvaluationService）が publish する
// **不合格固定**の Stage 0 verdict を組む純関数。
//
// 🔴 **本型は合格（Passed=true）を作れない。** Stage0GateResult を `Passed: false` 固定で直接組み、
// Stage0GateEvaluator を呼ばない。理由は 2 つある。
//   1. **評価対象がプレースホルダである**（ADR-0033 の本番戦略＝AI 判断の記録・再生は未実装。
//      同 ADR は #688 を名指しで「プレースホルダ戦略での動作確認に留める」と定めた）。
//      プレースホルダの成績を本番の合否として記録すれば、go-live の判断材料が偽になる。
//   2. **過去データが空でも 7 条件は「揃い得る」**。DataCutoffPolicy.IsAllAfterCutoff は `bars.All(...)` であり、
//      **空リストに対して true を返す**（真空的に真）。空バーを弾いているのは DSR・コスト頑健性・
//      ウォークフォワードが不成立になるという**間接的な**担保だけで、入力の供給経路が変われば崩れる。
//      駆動側は「バーが 0 本なら判定を走らせない」ことで、判定の手前で構造的に断つ。
//
// 実際の合否判定（Stage0GateService）は、本番戦略と評価文脈（試行台帳・PBO 行列・OOS リターン）が
// 揃ってから駆動へ配線する。そのときも本型の役割は「評価できない場合の不合格」に残る。
public static class Stage0DriverVerdict
{
    /// <summary>
    /// FR-15, IADR-0310 決定2: 過去データが 1 本も無い場合の verdict（**判定は走らせていない**）。
    /// 既定の provider は `none`（外部へ 1 リクエストも出さない）であるため、有効化直後の実運用は常にこの経路である。
    /// </summary>
    public static Stage0Decision NoHistoricalBars() =>
        Build([Stage0GateCheck.NoHistoricalBars, Stage0GateCheck.PlaceholderStrategy], dataCutoffSatisfied: false);

    /// <summary>
    /// FR-15, FR-20, ADR-0033, IADR-0310 決定3: プレースホルダ戦略の走行に対する verdict。
    /// **走行が何を示していても不合格**である。
    /// </summary>
    /// <param name="dataCutoffSatisfied">
    /// 全バーが LLM 学習カットオフ後か。**カットオフ日が未構成なら false を渡すこと**
    /// （ADR-0033 決定3 は汚染対策をカットオフ後データに限り、カットオフ日の供給元は計画側に未登録である。
    /// 未構成を「充足」に倒すと、検証条件①が確認されていないのに満たしたように読める）。
    /// </param>
    public static Stage0Decision PlaceholderRun(bool dataCutoffSatisfied)
    {
        var checks = new List<Stage0GateCheck> { Stage0GateCheck.PlaceholderStrategy };
        if (!dataCutoffSatisfied)
            checks.Add(Stage0GateCheck.DataCutoff);
        return Build(checks, dataCutoffSatisfied);
    }

    // 不合格固定の組み立て。DSR/PBO は「算出していない」ことを表す 0 を置く（プレースホルダの走行から
    // 意味のある値は出ない。試行台帳も PBO 行列も本番戦略が要る）。
    private static Stage0Decision Build(IReadOnlyList<Stage0GateCheck> failedChecks, bool dataCutoffSatisfied)
    {
        var gate = new Stage0GateResult(Passed: false, FailedChecks: failedChecks);
        return new Stage0Decision(
            gate,
            Stage0Promotion.Evaluate(gate),
            DeflatedSharpe: 0d,
            ProbabilityOfBacktestOverfitting: 0d,
            DataCutoffSatisfied: dataCutoffSatisfied);
    }
}
