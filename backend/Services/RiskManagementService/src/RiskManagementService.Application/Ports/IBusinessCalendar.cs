namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-10: 営業日の算出。日次損失ロックアウトの解除日（翌営業日）を求めるのに用いる（05_trading-assumptions §5）。
// 本 PR は週末スキップの最小実装（WeekendBusinessCalendar）。祝日データを含む市場カレンダーは #21 で差し替える。
public interface IBusinessCalendar
{
    /// <summary>指定日の「次の営業日」を返す。ロックアウトの解除日（この日から新規発注が再び可能）に用いる。</summary>
    DateOnly NextBusinessDay(DateOnly date);

    /// <summary>指定日が営業日（週末・休場日でない）か。撤退の定期評価（#166）の休場ガードに用いる（#21 と同型）。</summary>
    bool IsBusinessDay(DateOnly date);
}
