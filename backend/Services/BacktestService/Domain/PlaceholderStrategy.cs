namespace BacktestService.Domain;

// FR-15, FR-20, ADR-0008, ADR-0033, #688, IADR-0310: **駆動経路の検証専用**のプレースホルダ戦略。
//
// 計画 ADR-0033（2026-09-05 の利用者裁定）は Stage 0 の評価対象を「取引判断サービスの AI 判断そのもの」と定め、
// 記録・再生方式で評価すると決めた。同 ADR の「統制と現在の実現手段」表は、その暫定手段として #688 を
// 名指しで「プレースホルダ戦略での動作確認に留める」と定めている。本型はその「プレースホルダ戦略」である。
//
// 🔴 **［#632 / IADR-0318］本番戦略（RecordedDecisionReplayStrategy）は既に在る。** 本型は撤去せず、
// **構成 `Backtest:Stage0:Strategy` の既定（`placeholder`）＝駆動経路の検証用**として残る。
//
// 🔴 **本戦略の走行結果を本番の合否として扱ってはならない。** 注文を 1 件も出さないため、
// 走行は「何もしなかった」ことしか示さない。verdict は <see cref="Features.Backtest.EvaluateStage0Gate.Stage0DriverVerdict"/>
// が**不合格固定**で組む（合格を作れる口が無い）。
//
// 本番戦略（記録・再生）との差し替えは駆動側の構成 1 点で行う（publish 経路は共通のまま）。
public sealed class PlaceholderStrategy : IBacktestStrategy
{
    /// <summary>
    /// verdict が運ぶ戦略識別子。**プレースホルダであることが受け手（Risk・監査）から判る値**にする
    /// （`BacktestEvaluated.StrategyId` は verdict の無効化契機「戦略の変更」を機械判定する鍵でもある）。
    /// </summary>
    public const string StrategyId = "placeholder/no-op";

    /// <summary>注文を 1 件も出さない（約定も建玉も生まれない＝空売りの観測も常に false になる）。</summary>
    public IReadOnlyList<BacktestOrder> DecideOrders(BacktestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [];
    }
}
