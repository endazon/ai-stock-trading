extern alias RiskManagementWorker;

using System.Security.Cryptography;
using System.Text;
using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using Microsoft.Extensions.Logging;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Domain;
using TradeDecisionService.Features.TradeDecision.DecideTrade;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

/// <summary>記録実行の結末。</summary>
public enum Stage0RecordingStatus
{
    /// <summary>構成で無効（既定）。LLM を 1 回も呼んでいない。</summary>
    Disabled,

    /// <summary>期間・カットオフ日・銘柄・出力先のいずれかが未構成。LLM を 1 回も呼んでいない。</summary>
    NotConfigured,

    /// <summary>🔴 **未承認**（ADR-0033 決定5）。見積りは提示したが、LLM を 1 回も呼んでいない。</summary>
    NotApproved,

    /// <summary>最後まで記録した。</summary>
    Completed,

    /// <summary>🔴 見積り額を超えたため**停止**した（途中までの記録は保存する）。</summary>
    StoppedOverBudget,

    /// <summary>記録は採れたが書き出しに失敗した。</summary>
    SaveFailed,
}

/// <summary>記録実行の報告（利用者へ提示する材料）。</summary>
public sealed record Stage0RecordingOutcome(
    Stage0RecordingStatus Status,
    string Reason,
    Stage0RecordingEstimate Estimate,
    decimal ActualCostJpy,
    int LlmCallCount,
    Stage0DecisionRecordSet? RecordSet)
{
    /// <summary>LLM を 1 回でも呼んだか（否定形テストの観測点）。</summary>
    public bool CalledLlm => LlmCallCount > 0;
}

// FR-04, FR-11, FR-15, NFR（費用）, ADR-0003, ADR-0011, ADR-0033 決定2/決定4/決定5, #632, IADR-0318:
// **Stage 0 の記録器** —— 過去の各判断時点に「その時点までの情報だけ」を与えて AI 判断を記録する。
//
// 記録側を取引判断サービスに置くのは、**本番の判断経路がここにあるから**である
// （プロンプト構築 `TradeDecisionPromptBuilder`・構造化解析 `TradeDecisionParser`・多数決 `DecisionAggregator`・
// サイジング `PositionSizer`・LLM 客・費用計測）。バックテスト側へ複製すれば、本番と検証で判断経路が二重になり、
// ADR-0011 が段階ゲートの前提とした「検証したものと本番で走るものの一致」が構造的に保てない。
//
// 🔴 **既定では何もしない。** 無効・未構成・未承認のいずれでも `ILlmCompletionClient` は 1 回も呼ばれない。
public sealed class Stage0DecisionRecorder(
    ILlmCompletionClient llm,
    IAsOfDecisionInputProvider inputs,
    IStage0DecisionRecordSink sink,
    Stage0RecordingUsageCollector usage,
    LlmPriceTable priceTable,
    TimeProvider timeProvider,
    ILogger<Stage0DecisionRecorder> logger)
{
    /// <summary>
    /// FR-15, ADR-0033 決定5: 見積りを算出する（**実行しない**）。
    /// 「見積りを実行せずに取得できる」ことが決定5 の「提示」の要件である。
    /// </summary>
    public Stage0RecordingEstimate Estimate(Stage0RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var period = options.ParsePeriod();
        var tradingDays = period is { } p ? Stage0RecordingBudget.WeekdayCount(p.From, p.To) : 0;

        return Stage0RecordingBudget.Estimate(
            new Stage0RecordingEstimateInput(
                SymbolCount: options.ResolveSymbols().Count,
                TradingDayCount: tradingDays,
                DecisionsPerDay: options.DecisionsPerDay,
                VoteCount: options.VoteCount,
                InputTokensPerDecision: options.InputTokensPerDecision,
                OutputTokensPerDecision: options.OutputTokensPerDecision),
            priceTable.Resolve(options.Model));
    }

    public async Task<Stage0RecordingOutcome> RunAsync(
        Stage0RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var estimate = Estimate(options);

        // ADR-0033 決定5: 見積りは**実行の可否によらず提示する**（承認の材料になる）。
        logger.LogInformation(
            "Stage 0 記録の見積り: 呼び出し {Calls} 回（銘柄 {Symbols} × 平日 {Days} × 1 日 {PerDay} 回 × 多数決 {Votes} 回）"
            + "・入力 {InputTokens} トークン / 出力 {OutputTokens} トークン・合計 {TotalJpy} 円。"
            + "承認するには Stage0Recording:ApprovedEstimateJpy と ApprovedVoteCount を設定してください。",
            estimate.CallCount, options.ResolveSymbols().Count,
            options.ParsePeriod() is { } period
                ? Stage0RecordingBudget.WeekdayCount(period.From, period.To)
                : 0,
            options.DecisionsPerDay, options.VoteCount,
            estimate.InputTokens, estimate.OutputTokens, estimate.TotalJpy);

        if (!options.Enabled)
            return Outcome(Stage0RecordingStatus.Disabled, "記録は無効です（既定）。", estimate);

        var configuration = ValidateConfiguration(options);
        if (configuration is not null)
            return Outcome(Stage0RecordingStatus.NotConfigured, configuration, estimate);

        var approval = ValidateApproval(options, estimate);
        if (approval is not null)
            return Outcome(Stage0RecordingStatus.NotApproved, approval, estimate);

        return await RecordAsync(options, estimate, cancellationToken).ConfigureAwait(false);
    }

    // 🔴 実行の前提が欠けていれば LLM を呼ばない。**記録が残らない実行を作らない**（出力先の未設定を含む）。
    private static string? ValidateConfiguration(Stage0RecordingOptions options)
    {
        if (options.ParsePeriod() is null)
            return "記録期間（Stage0Recording:From / To）が未設定または不正です。";
        if (options.ParseLlmTrainingCutoff() is null)
            return "LLM 学習カットオフ日（Stage0Recording:LlmTrainingCutoff）が未設定です（ADR-0033 決定3）。";
        if (options.ResolveSymbols().Count == 0)
            return "記録対象の銘柄（Stage0Recording:Symbols）が未設定です。";
        if (string.IsNullOrWhiteSpace(options.OutputPath))
            return "記録の書き出し先（Stage0Recording:OutputPath）が未設定です（記録が残らない実行は行いません）。";
        if (options.VoteCount < 1 || options.DecisionsPerDay < 1)
            return "多数決回数・1 日あたり判断回数は 1 以上でなければなりません。";
        return null;
    }

    // 🔴 ADR-0033 決定5 の承認ゲート。**構成値が見積りと一致しない限り実行しない**。
    // 一致させる手段は「人が見積りを見て値を書き入れる」以外に無く、これが「利用者の承認」の機械的表現である。
    private static string? ValidateApproval(Stage0RecordingOptions options, Stage0RecordingEstimate estimate)
    {
        if (estimate.CallCount <= 0)
            return "見積りの呼び出し回数が 0 です（トークン量・期間・銘柄の設定を確認してください）。";
        if (options.ApprovedVoteCount is not { } approvedVotes || approvedVotes != options.VoteCount)
            return $"多数決回数が未承認です（Stage0Recording:ApprovedVoteCount に {options.VoteCount} を設定してください）。";
        if (options.ApprovedEstimateJpy is not { } approvedJpy)
            return $"見積り額が未承認です（Stage0Recording:ApprovedEstimateJpy に {estimate.TotalJpy} を設定してください）。";

        // 金額は円単位で端数を持つため、円未満 2 桁で丸めて一致を見る（構成へ書き写せる粒度に合わせる）。
        if (decimal.Round(approvedJpy, 2) != decimal.Round(estimate.TotalJpy, 2))
        {
            return $"承認額 {approvedJpy} 円が見積り {estimate.TotalJpy} 円と一致しません"
                + "（見積りが変わったら承認し直してください）。";
        }

        return null;
    }

    private async Task<Stage0RecordingOutcome> RecordAsync(
        Stage0RecordingOptions options, Stage0RecordingEstimate estimate, CancellationToken cancellationToken)
    {
        var (from, to) = options.ParsePeriod()!.Value;
        var cutoff = options.ParseLlmTrainingCutoff()!.Value;
        var symbols = options.ResolveSymbols();

        var records = new List<Stage0DecisionRecord>();
        var actualCost = 0m;
        var callCount = 0;
        var stopped = false;

        usage.BeginRecording();
        try
        {
            for (var day = from; day <= to && !stopped; day = day.AddDays(1))
            {
                // 週末は判断時点にならない（見積りの平日基準と揃える）。休場日は供給側が null を返してスキップされる。
                if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    continue;

                foreach (var (symbol, market) in symbols)
                {
                    var input = await inputs.GetAsync(symbol, market, day, cancellationToken).ConfigureAwait(false);
                    if (input is null)
                        continue; // 非取引日・復元できない日（ADR-0033 §残るもの）。判断を発明しない。

                    var (record, calls, cost) = await RecordOneAsync(options, symbol, market, input, cancellationToken)
                        .ConfigureAwait(false);
                    records.Add(record);
                    callCount += calls;
                    actualCost += cost;

                    // 🔴 ADR-0033 決定5: 実行中に見積り額を超えたら停止して報告する（黙って消費しない）。
                    // 判断時点の単位で見るため、超過は最大 1 判断ぶん（多数決回数だけの呼び出し）に限られる。
                    // 裏返すと超過幅は VoteCount に比例する（VoteCount 回 × 1 判断あたりの費用まで上振れし得る）。
                    // 判断の途中で打ち切ると多数決が成立せず記録が壊れるため、判断単位で見る（IADR-0318）。
                    if (actualCost > estimate.TotalJpy)
                    {
                        stopped = true;
                        logger.LogWarning(
                            "Stage 0 記録: 実績 {Actual} 円が見積り {Estimate} 円を超えたため停止します"
                            + "（ここまでの判断 {Records} 件は保存します）。",
                            actualCost, estimate.TotalJpy, records.Count);
                        break;
                    }
                }
            }
        }
        finally
        {
            usage.EndRecording();
        }

        var recordSet = BuildRecordSet(options, from, to, cutoff, symbols, records);
        var saved = await sink.SaveAsync(recordSet, cancellationToken).ConfigureAwait(false);

        var status = stopped
            ? Stage0RecordingStatus.StoppedOverBudget
            : saved ? Stage0RecordingStatus.Completed : Stage0RecordingStatus.SaveFailed;
        var reason = status switch
        {
            Stage0RecordingStatus.StoppedOverBudget =>
                $"見積り {estimate.TotalJpy} 円を超えたため停止しました（実績 {actualCost} 円）。",
            Stage0RecordingStatus.SaveFailed => "記録の書き出しに失敗しました。",
            _ => $"記録しました（判断 {records.Count} 件・実績 {actualCost} 円）。",
        };

        logger.LogInformation(
            "Stage 0 記録の結末: {Status}（判断 {Records} 件・LLM 呼び出し {Calls} 回・実績 {Actual} 円 / 見積り {Estimate} 円）。",
            status, records.Count, callCount, actualCost, estimate.TotalJpy);

        return new Stage0RecordingOutcome(status, reason, estimate, actualCost, callCount, recordSet);
    }

    // 1 判断時点の記録。**本番と同じ経路**でプロンプトを組み、多数決回数だけ LLM を呼び、本番と同じ規則で集約する。
    private async Task<(Stage0DecisionRecord Record, int Calls, decimal Cost)> RecordOneAsync(
        Stage0RecordingOptions options,
        string symbol,
        Market market,
        AsOfDecisionInput input,
        CancellationToken cancellationToken)
    {
        // 定時サイクル相当のトリガー（価格文脈は as-of の参照価格で補う。IADR-0099 決定2 と同じ形）。
        var trigger = DecisionTrigger.Scheduled(symbol, market);
        var prompt = TradeDecisionPromptBuilder.Build(
            trigger, input.Policy, input.Sizing, input.References, includeProfitability: false,
            currentPrice: input.ReferencePrice);
        var fingerprint = Fingerprint(prompt);

        if (input.DroppedFutureReferenceCount > 0 || input.DroppedUndatedReferenceCount > 0)
        {
            // 落とした参考情報は黙って捨てない（記録の入力が本番より痩せた事実は環流の材料になる）。
            logger.LogInformation(
                "Stage 0 記録: {Symbol} {AsOf} の参考情報から未来 {Future} 件・日付不明 {Undated} 件を除外しました。",
                symbol, input.AsOf, input.DroppedFutureReferenceCount, input.DroppedUndatedReferenceCount);
        }

        var raws = new List<Stage0RawDecision>(options.VoteCount);
        var votes = new List<LlmDecision>(options.VoteCount);
        var calls = 0;
        var cost = 0m;
        var inputTokens = 0;
        var outputTokens = 0;

        for (var attempt = 1; attempt <= options.VoteCount; attempt++)
        {
            // 🔴 ADR-0011 / IADR-0318 決定4: 用途は**本番と同じ** `trade-decision`。
            // ピン留めモデルの照合とフォールバック禁止（ADR-0017 決定2）を本番と同一に効かせる。
            // 費用の計上区分だけを `Stage0RecordingUsageCollector` が `stage0-recording` へ付け替える。
            var output = await llm
                .CompleteAsync(prompt, options.Model, LlmPurposes.TradeDecision, cancellationToken)
                .ConfigureAwait(false);
            calls++;

            var parsed = TradeDecisionParser.ParseDetailed(output);
            votes.Add(parsed.Decision);

            // この 1 回で発生した計測を切り出して費用へ積む（実効モデルで単価を引く。IADR-0122 決定1）。
            var captured = usage.DrainCaptured();
            var callInput = captured.Sum(u => u.InputTokens);
            var callOutput = captured.Sum(u => u.OutputTokens);
            var callCost = Stage0RecordingUsageCollector.CostOf(captured, priceTable);
            inputTokens += callInput;
            outputTokens += callOutput;
            cost += callCost;

            raws.Add(new Stage0RawDecision(
                attempt,
                ToRecordAction(parsed.Decision.Action),
                parsed.Decision.Rationale,
                parsed.Decision.ReferencePrice,
                parsed.Decision.StopLossDistancePerShare,
                callInput,
                callOutput,
                parsed.IsUnparseable));
        }

        // ADR-0033 決定4: 多数決は本番と同じ規則（同数・空は安全側 Hold）。
        var aggregated = DecisionAggregator.Aggregate(votes);
        var signedQuantity = SignedQuantity(aggregated.Decision, input);

        return (new Stage0DecisionRecord(
            symbol, market, input.AsOf, fingerprint, options.Model ?? string.Empty, options.VoteCount,
            raws, ToRecordAction(aggregated.Decision.Action), aggregated.Decision.Rationale,
            signedQuantity, cost, inputTokens, outputTokens), calls, cost);
    }

    // FR-10, IADR-0003/0107: サイジングは本番と同じ `PositionSizer` を使う（複製しない）。
    //
    // 🔴 **決済（Close）判定・採算ゲートは記録に含めない。** ADR-0033 決定1 が評価対象と定めたのは
    // 「FR-04 の AI 判断（Hold/Buy/Sell）」であり、保有建玉の有無に依存する決済経路（IADR-0119）と
    // 採算ゲート（IADR-0076）は判断そのものではない。再生側は建玉をシミュレータが持つため、
    // 決済は「反対方向の数量」として自然に畳まれる（`SignedInventory`）。
    private static int SignedQuantity(LlmDecision decision, AsOfDecisionInput input)
    {
        if (decision.Action == TradeAction.Hold)
            return 0;
        if (decision.ReferencePrice <= 0m || decision.StopLossDistancePerShare <= 0m
            || decision.StopLossDistancePerShare >= decision.ReferencePrice)
        {
            return 0; // IADR-0035 の不変量違反は見送りへ倒す（本番と同じ）。
        }

        var context = input.Sizing;
        var referencePriceBase = decision.ReferencePrice * input.RateToBase;
        var stopLossDistanceBase = decision.StopLossDistancePerShare * input.RateToBase;
        var sizeFactor = PositionSizer.GetSizeFactor(context.ConsecutiveLosses, context.DrawdownRatio, context.Limits);
        var availableCapital = Math.Max(0m, Math.Min(context.StageCapitalRemaining, context.DailyOrderRemaining));
        var quantity = PositionSizer.CalculateCappedQuantity(
            context.Capital,
            context.Limits.PerTradeRiskRatio,
            stopLossDistanceBase,
            referencePriceBase,
            context.Limits.MaxOrderAmountFor(context.Capital),
            availableCapital,
            sizeFactor);

        if (quantity <= 0)
            return 0;

        return decision.Action == TradeAction.Buy ? quantity : -quantity;
    }

    private Stage0DecisionRecordSet BuildRecordSet(
        Stage0RecordingOptions options,
        DateOnly from,
        DateOnly to,
        DateOnly cutoff,
        IReadOnlyList<(string Symbol, Market Market)> symbols,
        IReadOnlyList<Stage0DecisionRecord> records)
    {
        IReadOnlyList<Stage0RecordedSymbol> recorded =
            [.. symbols.Select(s => new Stage0RecordedSymbol(s.Symbol, s.Market))];
        var model = options.Model ?? string.Empty;
        var hash = Stage0StrategyIdentity.ComputeContentHash(from, to, recorded, cutoff, model, records);

        return new Stage0DecisionRecordSet(
            from, to, recorded, cutoff, timeProvider.GetUtcNow(), model,
            Stage0StrategyIdentity.StrategyIdFor(model, hash), records);
    }

    private static Stage0DecisionAction ToRecordAction(TradeAction action) => action switch
    {
        TradeAction.Buy => Stage0DecisionAction.Buy,
        TradeAction.Sell => Stage0DecisionAction.Sell,
        _ => Stage0DecisionAction.Hold,
    };

    // 入力の指紋。**プロンプト本文は記録に載せない**（保有ポジション・資金残枠等の機微を含む）。
    private static string Fingerprint(string prompt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));

    private static Stage0RecordingOutcome Outcome(
        Stage0RecordingStatus status, string reason, Stage0RecordingEstimate estimate) =>
        new(status, reason, estimate, ActualCostJpy: 0m, LlmCallCount: 0, RecordSet: null);
}
