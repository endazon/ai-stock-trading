using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-10, ADR-0049 決定2, #1120, IADR-0465 決定1: 損切り幅の下限（1.0 × ATR(14, 日足)）の供給口。
// 🔴 **今は供給しない**（既定は NoAtrStopWidthFloorSource＝常に null）。ATR(14) は日足が判断へ通ってから
// （ADR-0048 決定 3 と同じ条件＝取得枠の確認・株式分割をまたいだ値の確認。#1117・#1118 の後の別 issue）ここへ差し込む。
// null のとき、取引判断は参照価格（アンカー後の現在値）の 2% を下限とする（`StopWidthFloorPolicy.Fallback`）。
public interface IStopWidthFloorSource
{
    /// <summary>
    /// (銘柄, 市場) の損切り幅の下限（1 株あたり・<paramref name="anchoredPrice"/> と同じローカル通貨）。
    /// 得られない（足が足りない・取得失敗・経路が無い）ときは <b>null</b>。呼び出し側が退避の 2% を使う。
    /// </summary>
    ValueTask<StopWidthFloor?> GetFloorAsync(
        string symbol, Market market, decimal anchoredPrice, CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-10, ADR-0049 決定2, #1120: 損切り幅の下限（1 株あたり・ローカル通貨）とその出所。
/// </summary>
public sealed record StopWidthFloor(decimal PerShare, StopWidthFloorSource Source);
