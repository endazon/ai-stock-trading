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
// （プロンプト構築 `TradeDecisionPromptBuilder`・二段の順序と多数決 `DecisionOrchestrator`（`DecisionAggregator`）・
// 構造化解析 `TradeDecisionParser`・サイジング `PositionSizer`・LLM 客・費用計測）。
// 🔴 FR-15, ADR-0054 決定3, #1196, IADR-0498: **一次スクリーニング → 本判断の二段も本番のオーケストレータをそのまま使う**
// （順序を複製しない）。一次で見送れば本判断を呼ばない —— 本番で走る系そのものを記録する。バックテスト側へ複製すれば、本番と検証で判断経路が二重になり、
// ADR-0011 が段階ゲートの前提とした「検証したものと本番で走るものの一致」が構造的に保てない。
//
// 🔴 **既定では何もしない。** 無効・未構成・未承認のいずれでも `ILlmCompletionClient` は 1 回も呼ばれない。
// 🔴 #1196, IADR-0498: production は本番の二段オーケストレーションの構成（DI の単一の値）。一次プロンプトの形
// （スクリーニング入力の予算＝縮退の有無）を本番に合わせるために読む。二段の有効化・多数決回数・モデルの希望値は
// 記録の構成（承認した値）で上書きする —— 二段は本番の構成によらず必ず通す（ADR-0054 決定3）。
public sealed class Stage0DecisionRecorder(
    ILlmCompletionClient llm,
    DecisionOrchestrationOptions production,
    IAsOfDecisionInputProvider inputs,
    IStage0DecisionRecordSink sink,
    Stage0RecordingUsageCollector usage,
    LlmPriceTable priceTable,
    TimeProvider timeProvider,
    ILogger<Stage0DecisionRecorder> logger,
    MinimumEntryNotionalOptions? minimumEntryNotional = null)
{
    // 🔴 FR-10, #1209, IADR-0507: 最小の名目額のしきい値は**本番の判断と同じ構成**（DI の単一の値＝Sizing:MinEntryNotionalRatio）を使う。
    // 未指定は既定（1%）で効く（不在を「統制なし」にしない。IADR-0163 決定2 の規律・TradeDecisionAppService と同じ）。
    private readonly MinimumEntryNotionalOptions _minimumEntryNotional =
        minimumEntryNotional ?? MinimumEntryNotionalOptions.Default;

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
                OutputTokensPerDecision: options.OutputTokensPerDecision,
                ScreeningInputTokensPerDecision: options.ScreeningInputTokensPerDecision,
                ScreeningOutputTokensPerDecision: options.ScreeningOutputTokensPerDecision),
            priceTable.Resolve(options.Model),
            // FR-15, ADR-0054 決定1・決定3, #1196: 一次の層の単価。希望値が無ければ一次のピン（応答するはずのモデル）で引く。
            priceTable.Resolve(options.ScreeningModel ?? LlmAssignments.For(LlmPurposes.TradeDecisionScreening)?.PrimaryModel));
    }

    public async Task<Stage0RecordingOutcome> RunAsync(
        Stage0RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var estimate = Estimate(options);

        // ADR-0033 決定5: 見積りは**実行の可否によらず提示する**（承認の材料になる）。
        logger.LogInformation(
            "Stage 0 記録の見積り: 呼び出し {Calls} 回（銘柄 {Symbols} × 平日 {Days} × 1 日 {PerDay} 回 ×（一次 1 ＋ 多数決 {Votes} 回））"
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
                    // 判断時点の単位で見るため、超過は最大 1 判断ぶん（一次 1 回＋多数決回数だけの呼び出し。#1196）に限られる。
                    // 裏返すと超過幅は VoteCount に比例する（一次＋VoteCount 回 × 1 呼び出しあたりの費用まで上振れし得る）。
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
        // 🔴 #854, IADR-0351 決定7: **保有なしを明示して渡す。** 記録は銘柄 × 判断時点で独立であり、保有は再生側
        // （BacktestService）のシミュレーションでしか決まらない——記録器は知り得ない。既定（null＝不明）のままでは
        // プロンプトが「不明なら Hold」と述べ、全件が Hold へ倒れて Stage 0 が成立しない。
        // 帰結: 記録が検証するのは本番プロンプトの「保有なし」の枝だけである（IADR-0351「残る制約」）。
        // 🔴 FR-04, ADR-0044 決定 3, #1034, IADR-0440 決定 7（2026-09-27 改訂）: 監視銘柄は **as-of 入力の当時の監視銘柄（(e)）だけ**を渡す。
        // 🔴 **記録の対象銘柄（Stage0Recording:Symbols）を監視銘柄の代わりに渡さない** —— 当時の方針が挙げる銘柄と食い違うことがある。
        // 再構成の供給口（#1049）が入るまで `input.Watchlist` は null であり、節は「不明」になる。その記録は (e) を
        // 再構成不可と申告し（`AsOfDecisionInput`）、Stage 0 の合否から外れる（ADR-0044 決定 4 の暫定手段）。
        // FR-02, FR-04, #1035, IADR-0451: 値動きの行は as-of 入力の日中文脈から書く（前日比は日足の前日終値があれば出し、
        // 当日始値比・日中高安は常に「不明」。場中の本番の判断が知り得ない当日の全体の値を渡さない）。
        // 🔴 FR-04, ADR-0048 決定 2, #1139, IADR-0479: 出来高は as-of 入力の値（判断時点の前営業日までの確定足から本番と同じ計算）を渡す。
        // 判断の出来高が無効なら null で、従来の「出来高: 未提供」の行のまま（本番の無効の構成と同じプロンプト）。
        var prompt = TradeDecisionPromptBuilder.Build(
            trigger, input.Policy, input.Sizing, input.References, includeProfitability: false,
            currentPrice: input.ReferencePrice, held: HeldPosition.None, working: WorkingEntryOrders.None,
            watchlist: input.Watchlist, intraday: input.Intraday, volume: input.Volume, stopFloor: input.StopFloor);
        var fingerprint = Fingerprint(prompt);

        if (input.DroppedFutureReferenceCount > 0 || input.DroppedUndatedReferenceCount > 0)
        {
            // 落とした参考情報は黙って捨てない（記録の入力が本番より痩せた事実は環流の材料になる）。
            logger.LogInformation(
                "Stage 0 記録: {Symbol} {AsOf} の参考情報から未来 {Future} 件・日付不明 {Undated} 件を除外しました。",
                symbol, input.AsOf, input.DroppedFutureReferenceCount, input.DroppedUndatedReferenceCount);
        }

        // 🔴 FR-15, ADR-0036 決定1, #749, IADR-0387: **再構成できなかった入力があれば Warning を出す。**
        // 記録は止めない（同決定「『外す』は『走らせない』ではない」）が、この判断は **Stage 0 の合否から外れる**。
        // 上の除外件数の Information とは別立てにする —— あちらは as-of の正常な振る舞い（未来を落とした）を
        // 含むが、こちらは**合格の射程が狭まる**という統制上の事実であり、運用が気づくべき水準が違う。
        if (input.NotReconstructableKinds.Count > 0)
        {
            logger.LogWarning(
                "Stage 0 記録: {Symbol} {AsOf} は as-of 入力 {Kinds} を再構成できませんでした。"
                + "記録は残しますが、**この判断は Stage 0 の判定母集団から除かれます**（計画 ADR-0036 決定1）。",
                symbol, input.AsOf, string.Join(", ", input.NotReconstructableKinds));
        }

        // 🔴 FR-04, FR-15, ADR-0054 決定3, #1196, IADR-0498: **本番と同じ二段を本番のオーケストレータで走らせる。**
        // 一次（`trade-decision-screening`・1 回）で関心なし・解析不能なら本判断（`trade-decision`・多数決回数）を呼ばない。
        // 順序・打ち切り・多数決は `DecisionOrchestrator` のものであり、ここで複製しない（複製すれば検証した系と本番の系がずれ始める）。
        // 🔴 ADR-0011 / IADR-0318 決定4: 用途は**本番と同じ**（層ごと）。ピン留めモデルの照合とフォールバック禁止（ADR-0017 決定2）を
        // 本番と同一に効かせる。費用の計上区分だけを `Stage0RecordingUsageCollector` が `stage0-recording` へ付け替える。
        // 各呼び出しの出力と実効モデルは、オーケストレータへ渡す LLM 客を包んで呼び出しの直後に切り出す（記録用・挙動は変えない）。
        var capturing = new CapturingLlmClient(llm, usage);
        var orchestrator = new DecisionOrchestrator(capturing, OrchestrationFor(options), logger);
        var orchestrated = await orchestrator
            .DecideAsync(
                () => BuildScreeningPrompt(trigger, input),
                prompt,
                // #1187: 保有なし（プロンプトと同じ HeldPosition.None）を明示する。記録の判断は新規建ての枝だけであり、
                // 決済の損切り幅の任意化（本番の二次本判断）は掛からない（挙動は従来どおり）。
                HeldPosition.None.SignedQuantity,
                cancellationToken)
            .ConfigureAwait(false);

        var screeningCall = capturing.Calls.First(c => c.Purpose == LlmPurposes.TradeDecisionScreening);
        var screen = TradeDecisionParser.ParseScreening(screeningCall.Output);
        var screening = new Stage0ScreeningDecision(
            ToRecordAction(screen.Action),
            screen.IsUnparseable,
            // 🔴 FR-04, FR-11, #1290, IADR-0524 決定 3: 一次の根拠文に文字化けの疑いがあれば記録にも目印を付ける。
            // 検出はオーケストレータが受け取った地点で 1 回だけ行い、その印を使う（ここで検出し直さない）。
            RationaleGarbleDetector.Mark(screen.Rationale, orchestrated.ScreeningRationaleGarbleSuspected),
            screeningCall.Usages.Sum(u => u.InputTokens),
            screeningCall.Usages.Sum(u => u.OutputTokens),
            EffectiveModelOf(screeningCall.Usages));

        var raws = new List<Stage0RawDecision>(options.VoteCount);
        foreach (var (call, index) in capturing.Calls
            .Where(c => c.Purpose == LlmPurposes.TradeDecision)
            .Select((c, i) => (c, i)))
        {
            // 本番と同じ解析器で読み直す（決定的）。多数決そのものはオーケストレータの結果を使う。
            var parsed = TradeDecisionParser.ParseDetailed(call.Output, HeldPosition.None.SignedQuantity);
            raws.Add(new Stage0RawDecision(
                index + 1,
                ToRecordAction(parsed.Decision.Action),
                parsed.Decision.Rationale,
                parsed.Decision.ReferencePrice,
                parsed.Decision.StopLossDistancePerShare,
                call.Usages.Sum(u => u.InputTokens),
                call.Usages.Sum(u => u.OutputTokens),
                parsed.IsUnparseable,
                EffectiveModelOf(call.Usages)));
        }

        // 実効モデルで単価を引く（IADR-0122 決定1）。費用は両層の全呼び出しの合計。
        var allUsages = capturing.Calls.SelectMany(c => c.Usages).ToArray();
        var cost = Stage0RecordingUsageCollector.CostOf(allUsages, priceTable);
        var inputTokens = allUsages.Sum(u => u.InputTokens);
        var outputTokens = allUsages.Sum(u => u.OutputTokens);

        // ADR-0033 決定4: 多数決は本番と同じ規則（同数・空は安全側 Hold）。一次で見送れば一次の Hold（根拠つき）。
        var decision = orchestrated.Decision;
        var (signedQuantity, belowMinimumNotional) = SignedQuantity(decision, input);
        if (belowMinimumNotional == true)
        {
            // FR-10, #1209, IADR-0507: 本番ならサイジングの直後に SizedBelowMinimumNotional で見送る判断。数量は消さずに判定を記録へ残し、
            // 再生が新規建てにだけ適用する（記録器は保有を知らない）。
            logger.LogInformation(
                "Stage 0 記録: {Symbol} {AsOf} は新規建てとして最小の名目額に満たない（本番は SizedBelowMinimumNotional で見送る。"
                + "再生は新規建てになるときに見送る）。quantity={Quantity} ratio={Ratio}",
                symbol, input.AsOf, signedQuantity, _minimumEntryNotional.Ratio);
        }

        return (new Stage0DecisionRecord(
            symbol, market, input.AsOf, fingerprint, options.Model ?? string.Empty, options.VoteCount,
            raws, ToRecordAction(decision.Action), MajorityRationale(decision, signedQuantity),
            signedQuantity, cost, inputTokens, outputTokens,
            // FR-15, ADR-0036 決定1, #749, IADR-0387: 入力ごとの再構成可否を記録へ残す（**外した範囲が読めるようにする**）。
            input.AsOfInputs,
            // FR-15, ADR-0054 決定3, #1196, IADR-0498: 一次の判断と一次に応答したモデルを本判断と別に残す。
            screening,
            // FR-10, #1209, IADR-0507: 新規建てとして最小の名目額に満たないか（本番と同じ判定。null は判定していない）。
            belowMinimumNotional), capturing.Calls.Count, cost);
    }

    // FR-15, ADR-0054 決定3, #1196, IADR-0498: 記録の二段の構成。本番の構成（一次プロンプトの予算）を引き継ぎ、
    // 二段は必ず有効にし、多数決回数とモデルの希望値は記録の構成（承認した値）にする。
    private DecisionOrchestrationOptions OrchestrationFor(Stage0RecordingOptions options) => production with
    {
        EnableScreening = true,
        VoteCount = options.VoteCount,
        PrimaryModel = options.ScreeningModel,
        SecondaryModel = options.Model,
    };

    // FR-04, FR-15, ADR-0054 決定3, #1196, IADR-0498: 一次のプロンプトを**本番と同じ形**で組む（`TradeDecisionAppService` の一次の枝と同じ分岐）。
    // 予算（ScreeningContextBudgetChars）があれば参考情報を縮退して載せ、無ければ参考情報なし（IADR-0072 決定2 / IADR-0247 / IADR-0313）。
    // 保有なし・未約定なし・ニュースの状態は不明（二次と同じ。as-of で再構成しない）・買い増しの審査は無い（保有なし）。
    private string BuildScreeningPrompt(DecisionTrigger trigger, AsOfDecisionInput input)
    {
        if (production.ScreeningContextBudgetChars is { } budget)
        {
            var assembled = ScreeningContextAssembler.Assemble(
                trigger, input.Policy, input.References, input.ReferencePrice, budget, input.Watchlist);
            return TradeDecisionPromptBuilder.BuildScreening(
                trigger, input.Policy, input.Sizing, input.ReferencePrice, assembled.RetainedReferences,
                HeldPosition.None, WorkingEntryOrders.None, input.Watchlist, input.Intraday, news: null, input.Volume,
                addOnBlockers: null);
        }

        return TradeDecisionPromptBuilder.BuildScreening(
            trigger, input.Policy, input.Sizing, input.ReferencePrice, held: HeldPosition.None,
            working: WorkingEntryOrders.None, watchlist: input.Watchlist, intraday: input.Intraday, news: null,
            volume: input.Volume, addOnBlockers: null);
    }

    // 呼び出しの実効モデル（応答が名乗った値）。計測が無い・値が割れる・空は null（＝不明。再生側はピンと一致したと読まない）。
    private static string? EffectiveModelOf(IReadOnlyList<LlmUsage> usages)
    {
        var models = usages
            .Select(u => u.Model?.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return models is [{ Length: > 0 } single] ? single : null;
    }

    // FR-04, FR-11, ADR-0040 決定5, #822, IADR-0343 決定3: 記録の多数決根拠も本番の発行と同じ突合を掛ける
    // （記録した数量と食い違う株数言及に注記）。Hold は数量を持たないため対象外。生の判断（各票）は出力そのままで残す。
    private static string MajorityRationale(LlmDecision decision, int signedQuantity) =>
        decision.Action == TradeAction.Hold
            ? decision.Rationale
            : RationaleQuantityReconciler.Reconcile(decision.Rationale, Math.Abs(signedQuantity)).Rationale;

    // FR-10, IADR-0003/0107: サイジングは本番と同じ `PositionSizer` を使う（複製しない）。
    //
    // 🔴 **決済（Close）判定・採算ゲートは記録に含めない。** ADR-0033 決定1 が評価対象と定めたのは
    // 「FR-04 の AI 判断（Hold/Buy/Sell）」であり、保有建玉の有無に依存する決済経路（IADR-0119）と
    // 採算ゲート（IADR-0076）は判断そのものではない。再生側は建玉をシミュレータが持つため、
    // 決済は「反対方向の数量」として自然に畳まれる（`SignedInventory`）。
    //
    // 🔴 FR-10, #1176, IADR-0495 決定1, #1209, IADR-0507: **最小の名目額は本番と同じ関数・同じしきい値で判定する**（数量 > 0 のとき）。
    // 記録器が評価するのは保有なしの枝だけ（IADR-0351 決定7）なので、Buy / Sell はすべて新規建てとして判定する。
    // 数量は 0 にしない —— 再生ではこの注文が建玉の決済として働くことがあり、本番は決済に名目額を掛けない（適用は再生側が新規建てにだけ行う）。
    private (int SignedQuantity, bool? BelowMinimumNotional) SignedQuantity(LlmDecision decision, AsOfDecisionInput input)
    {
        if (decision.Action == TradeAction.Hold)
            return (0, null);
        if (decision.ReferencePrice <= 0m || decision.StopLossDistancePerShare <= 0m
            || decision.StopLossDistancePerShare >= decision.ReferencePrice)
        {
            return (0, null); // IADR-0035 の不変量違反は見送りへ倒す（本番と同じ）。
        }

        // 🔴 FR-10, ADR-0049 決定2・決定3, #1120, IADR-0465 決定5: 本番と同じ下限を掛けてからサイジングする（幅を下限まで広げ、見送らない）。
        // Stage 0 には現在値のアンカーが無く、記録の参照価格が判断時点の価格である。各票の生の幅（Stage0RawDecision）は従来どおり残る。
        // #1122, IADR-0486 決定4: ATR(14) は判断時点の前営業日までの確定足から本番と同じ計算で求めた値（input.StopFloor）を使う
        // （ADR-0049 決定2「Stage 0 では同じ値を計算する」）。無効・得られないときは本番と同じく参照価格の 2%。
        var stopWidth = StopWidthFloorPolicy.Apply(
            decision.StopLossDistancePerShare, StopWidthFloorPolicy.Resolve(input.StopFloor?.Supplied, decision.ReferencePrice));

        var context = input.Sizing;
        var referencePriceBase = decision.ReferencePrice * input.RateToBase;
        var stopLossDistanceBase = stopWidth.AppliedWidthPerShare * input.RateToBase;
        var sizeFactor = PositionSizer.GetSizeFactor(context.ConsecutiveLosses, context.DrawdownRatio, context.Limits);
        // #869, ADR-0041 決定2, IADR-0354: 基準資金・残枠の未供給（null）は 0 として畳む（本番の経路と同じ扱い）。
        var capital = context.Capital ?? 0m;
        var availableCapital = Math.Max(
            0m, Math.Min(context.StageCapitalRemaining ?? 0m, context.DailyOrderRemaining ?? 0m));
        var quantity = PositionSizer.CalculateCappedQuantity(
            capital,
            context.Limits.PerTradeRiskRatio,
            stopLossDistanceBase,
            referencePriceBase,
            context.Limits.MaxOrderAmountFor(capital),
            availableCapital,
            sizeFactor);

        if (quantity <= 0)
            return (0, null);

        // 本番（TradeDecisionAppService のサイジングの直後）と同じ式: 名目額＝数量 × 参照価格（基準通貨）、equity＝サイジングの資金。
        var belowMinimum = MinimumEntryNotional.IsBelow(quantity * referencePriceBase, capital, _minimumEntryNotional.Ratio);

        return (decision.Action == TradeAction.Buy ? quantity : -quantity, belowMinimum);
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

    // FR-15, ADR-0054 決定3, #1196, IADR-0498: オーケストレータへ渡す LLM 客の包み。呼び出しをそのまま委ね、直後に
    // その 1 回で発生した計測（実効モデル・トークン量）を切り出して層（用途）と出力に結びつける。**挙動は 1 バイトも変えない。**
    private sealed class CapturingLlmClient(ILlmCompletionClient inner, Stage0RecordingUsageCollector usage)
        : ILlmCompletionClient
    {
        public List<CapturedCall> Calls { get; } = [];

        public async Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken cancellationToken = default)
        {
            var output = await inner.CompleteAsync(prompt, model, purpose, cancellationToken).ConfigureAwait(false);
            Calls.Add(new CapturedCall(purpose, output, usage.DrainCaptured()));
            return output;
        }
    }

    private sealed record CapturedCall(string? Purpose, string Output, IReadOnlyList<LlmUsage> Usages);

    private static Stage0RecordingOutcome Outcome(
        Stage0RecordingStatus status, string reason, Stage0RecordingEstimate estimate) =>
        new(status, reason, estimate, ActualCostJpy: 0m, LlmCallCount: 0, RecordSet: null);
}
