using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

/// <summary>
/// FR-04, FR-10, #1396, IADR-0561 決定 5: 判断のプロンプトへ渡す「市場の現地時刻と閉場までの残り時間」の材料。
/// <paramref name="SessionClose"/> はその市場ローカルの日付の最後の閉場（市場カレンダー。休場日は null）。
/// 方針の時刻の条件（「終盤は新規建てを控える」等）を AI が読めるようにするための補助であり、強制は審査の締め切りと閉場前の手仕舞いが行う。
/// </summary>
public sealed record MarketClockContext(Market Market, DateTimeOffset Now, DateTimeOffset? SessionClose);
