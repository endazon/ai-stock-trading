using System.Diagnostics;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using Microsoft.Extensions.Logging;
using Wolverine;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Infrastructure.Steps;

// FR-02, UC-01, IADR-0023: 定時系統の合流点。情報収集の完了（InformationCollected）を購読し、監視銘柄（watchlist）を巡回して
// 市場開場中のもののみ AI 判断を実行する。判断が成立（発注意図あり）した銘柄について TradeDecisionMade を発行する。
// 休場日（市場カレンダー閉場）の銘柄はサイクルを起動しない。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<InformationCollected> から Wolverine のハンドラへ移行した。
// ConsumeContext<T> は消え、メッセージ本体・IMessageBus・CancellationToken をメソッド引数で受け取る。
// IADR-0129 決定 9 によりハンドラ型は public sealed とする（Wolverine は public でない型を受け付けない）。
//
// NFR-07, #287, IADR-0255: 定時系統の判断回数・内訳・レイテンシを銘柄ごとに計上する（銘柄はタグにしない。
// 系列のカーディナリティを業務量に比例させないため。銘柄単位の追跡はログ・トレースが担う）。
//
// 🔴 NFR, FR-04, FR-11, #1111, IADR-0483 決定2: 銘柄ごとに捕まえた例外は、その銘柄のこの巡回での**最終の失敗**である
// （同じ巡回で再試行しない。次の巡回は新しい判断）。1 件につき 1 回だけ監査台帳へ渡す（型名・発生源・銘柄・時刻だけ）。
// 報告口は**必須依存**にする（省略可能にすると Program.cs から配線が消えても試験が緑のまま記録だけが止まる。IADR-0163 決定2）。
//
// 🔴 FR-02, NFR-02, #1169, IADR-0490: 1 通で全銘柄を順に判断するため、ハンドラの実行時間は銘柄数に比例する。
//   決定1: 実行時間の上限は ScheduledCycleBudget から導いた値（ScheduledCycleTimeoutPolicy。Wolverine の既定 60 秒に頼らない）。
//          1 銘柄の上限は**銘柄ごとの締め切り**として強制し、超えた銘柄はその銘柄の失敗として分離する
//          （サイクル全体の打ち切り→再配送にしない）。予算も**必須依存**（上と同じ理由）。
//   決定2: 判断の DecisionId は (起点イベント, 市場, 銘柄) から決定的に導く（ScheduledDecisionIds）。サイクルが打ち切られても
//          落ちても、再配送で出た判断は同じ DecisionId になり、下流の DecisionId の冪等が重複発注を止める。
public sealed class InformationCollectedHandler(
    AppSvc decisionService,
    IWatchlistProvider watchlist,
    IMarketCalendar calendar,
    IClock clock,
    BusinessMetrics metrics,
    NewsCollectionStatusStore newsStatus,
    ITradeDecisionFailureReporter failureReporter,
    ScheduledCycleBudget budget,
    ILogger<InformationCollectedHandler> logger)
{
    public async Task Handle(InformationCollected message, IMessageBus bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        var now = clock.UtcNow;

        // FR-04, ADR-0020 決定2, #1081, IADR-0455: ニュースの状態（取得済み／欠測／未構成。null＝不明）を**判断の前に**記録する。
        // 定時・急変の両方の判断が同じ最新値をプロンプトへ明示する（RAG を経由しない経路）。
        newsStatus.Record(message.NewsStatus, message.NewsStatusValidFor, message.CollectedAt);

        // FR-02, IADR-0095: 権威源（市場監視 #10）から当該サイクルの監視銘柄を照会する（未結線なら構成ベース）。
        // 🔴 FR-02, ADR-0044, #1134, IADR-0475: 読めなければ直前に読めた一覧。一度も読めていなければ null（不明）で、
        // このサイクルの判断をしない（構成の既定 watchlist で判断しない。警告は供給口が障害ごとに 1 回出す）。
        var watchlistSymbols = await watchlist.GetWatchlistAsync(cancellationToken).ConfigureAwait(false);
        if (watchlistSymbols is null)
            return;

        // #1169, IADR-0490 決定1: 上限は監視銘柄数の前提から導いてある。前提を超えたらサイクルが上限に達し得ることを告げる
        // （打ち切り→再配送になっても決定2 で重複はしないが、LLM の費用が二重になり判断の時刻がずれる）。
        if (watchlistSymbols.Count > budget.MaxWatchedSymbols)
        {
            logger.LogWarning(
                "監視銘柄 {Count} 件が定時サイクルの上限の前提 {Max} 件を超えています。サイクルが実行時間の上限 {Timeout} に"
                + "達すると打ち切られて再配送されます（判断は DecisionId で冪等）。TradeCycle__MaxWatchedSymbols を見直してください。",
                watchlistSymbols.Count, budget.MaxWatchedSymbols, budget.HandlerTimeout);
        }

        foreach (var watched in watchlistSymbols)
        {
            if (!calendar.IsOpen(watched.Market, now))
            {
                // 休場日（週末・祝日）はサイクルを起動しない。
                continue;
            }

            // 1 銘柄の判断/発行失敗でサイクル全体を再配送させない（IADR-0023）。再配送すると発行済み銘柄も再ループされる。
            // 失敗銘柄はログに残して次銘柄へ継続し、次回巡回で再評価する（キャンセルは伝播させる）。
            // ［2026-10-06 追記 / #1169・IADR-0490］旧注記「DecisionId は都度新規採番のため下流の冪等をすり抜け重複発注し得る」は
            // 決定2 で解消した（再配送でも同じ DecisionId）。なお Wolverine は失敗した試行の発行を送らずに捨てる（発行は成功後にまとめて送る）。
            try
            {
                // #1169, IADR-0490 決定1: 1 銘柄の締め切り。超えたらこの銘柄の失敗として下の catch が捕まえる
                // （捕まえる条件はハンドラの token であり、銘柄の締め切りではない＝サイクルの打ち切り・停止は従来どおり伝播させる）。
                using var symbolDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                symbolDeadline.CancelAfter(budget.PerSymbol);

                // NFR-07, #287: 判断の所要と結果は「成立したか」に関わらず計上する（見送りも 1 回の判断である）。
                var started = Stopwatch.GetTimestamp();
                var decision = await decisionService
                    // NFR-02, #689, IADR-0307: 定時サイクルの起点は**収集の完了時刻**である。
                    // ここで渡さないと、下流（発注完了・記録完了）が区間を閉じられず未観測になる。
                    .DecideAsync(
                        DecisionTrigger.Scheduled(watched.Symbol, watched.Market, message.CollectedAt),
                        symbolDeadline.Token)
                    .ConfigureAwait(false);
                metrics.RecordTradeDecisionDuration(
                    BusinessMetrics.TriggerScheduled, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                metrics.RecordTradeDecision(BusinessMetrics.TriggerScheduled, decision?.Intent.Side);

                if (decision is null)
                    continue; // 方針なし・Hold・見送り

                // 🔴 #1169, IADR-0490 決定2: 再配送をまたいで同じ DecisionId にする。起点イベントの ID が空なら導かない
                // （空から導くと以後の判断がすべて同じ ID になり、下流が再配送として捨てる）。
                if (ScheduledDecisionIds.For(message.EventId, watched.Symbol, watched.Market) is { } decisionId)
                {
                    decision = decision with { DecisionId = decisionId };
                }
                else
                {
                    logger.LogWarning(
                        "定時サイクルの起点イベントの ID が空のため、DecisionId を決定的に導けません（再配送の冪等が効かない）: {Symbol}",
                        watched.Symbol);
                }

                logger.LogInformation(
                    "定時判断: DecisionId={DecisionId} {Symbol} {Side} 数量={Quantity}",
                    decision.DecisionId, decision.Intent.Symbol, decision.Intent.Side, decision.Intent.Quantity);
                await bus.PublishAsync(decision).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "定時サイクルの銘柄処理でエラー: {Symbol}。この銘柄をスキップし継続します。", watched.Symbol);
                // #1111, IADR-0483 決定2: 最終の失敗として 1 件だけ台帳へ渡す（報告口は例外を投げない）。
                await failureReporter
                    .ReportFinalFailureAsync(BusinessMetrics.TriggerScheduled, watched.Symbol, watched.Market, ex)
                    .ConfigureAwait(false);
            }
        }
    }
}
