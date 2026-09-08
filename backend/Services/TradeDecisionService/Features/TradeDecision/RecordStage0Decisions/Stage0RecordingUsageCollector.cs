using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-15, NFR（費用）, ADR-0011, ADR-0033 決定5, #632, IADR-0318 決定4:
// **Stage 0 記録の費用を月次上限の外へ出しつつ、モデルの割当統制は本番と同一に保つ**ためのアダプタ。
//
// 🔴 用途キー（purpose）には 2 つの消費者がいる。
//   (a) 基盤 LLM ゲートウェイのモデル選択と、応答モデルの照合（LlmAssignmentEvaluator）
//   (b) 費用の計上区分（LlmCostScope.IsGoverned ＝ 月次上限 15,000 円の対象範囲）
// ADR-0011 は (a) を本番と一致させることを段階ゲートの前提とし、ADR-0033 決定5 は (b) を本番の外に置く。
// **1 つのキーで両立できない**ため、呼び出しは `trade-decision` のまま行い、
// **計上の境界で `stage0-recording` へ付け替える**。
//
// 別解として用途キーそのものを新設し `LlmAssignments` へ登録する案があったが採らなかった。
//   - 割当表は**計画の確定値のスナップショット**（ADR-0014 / ADR-0017）であり、計画に無い用途を足せば
//     「表の値が計画と一致していること」を守るテストの意味が薄れる。
//   - 基盤（microservices-platform）の `Llm:Routing:PurposeModels` に新キーを登録しない限り、
//     `LlmRouter` は無音で `DefaultModel` へ落ちる。**別モデルで記録した判断列**で Stage 0 を評価すれば、
//     ADR-0011 が守ろうとした一致がその場で失われる。
//
// 🔴 **記録中でないときは素通しである**（本番の計上は 1 バイトも変えない）。
public sealed class Stage0RecordingUsageCollector(ILlmUsageReporter inner) : ILlmUsageReporter
{
    private readonly List<LlmUsage> _captured = [];

    /// <summary>記録中か。記録器が実行の前後で立てる（同一 DI スコープ内・単一の記録実行に閉じる）。</summary>
    public bool IsRecording { get; private set; }

    /// <summary>この記録実行で捕まえた計測（トークン量と実効モデル）。</summary>
    public IReadOnlyList<LlmUsage> Captured => _captured;

    /// <summary>記録の開始（捕捉を初期化する）。</summary>
    public void BeginRecording()
    {
        _captured.Clear();
        IsRecording = true;
    }

    /// <summary>記録の終了（以降は素通しへ戻る）。</summary>
    public void EndRecording() => IsRecording = false;

    /// <summary>直近の捕捉を取り出して消す（1 判断ぶんの費用を切り出すため）。</summary>
    public IReadOnlyList<LlmUsage> DrainCaptured()
    {
        var drained = _captured.ToArray();
        _captured.Clear();
        return drained;
    }

    public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default)
    {
        if (!IsRecording)
            return inner.ReportAsync(usage, cancellationToken);

        _captured.Add(usage);
        // ADR-0033 決定5: 月次上限（取引判断サイクル対象）へ積まない区分で publish する。
        // **publish 自体は行う** —— 実績は月報へ記載する必要があり（同決定4）、計上経路を止めると実績が残らない。
        return inner.ReportAsync(usage with { Purpose = LlmPurposes.Stage0Recording }, cancellationToken);
    }

    /// <summary>捕捉した計測から費用（円）を算出する。単価は**応答が名乗った実効モデル**で引く（IADR-0122 決定1）。</summary>
    public static decimal CostOf(IEnumerable<LlmUsage> usages, LlmPriceTable priceTable)
    {
        ArgumentNullException.ThrowIfNull(usages);
        ArgumentNullException.ThrowIfNull(priceTable);

        return usages.Sum(u => LlmPricing.Compute(u.InputTokens, u.OutputTokens, priceTable.Resolve(u.Model)));
    }
}
