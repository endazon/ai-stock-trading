using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;

namespace AiStockTrading.RiskManagement.Application.Tests;

// テスト用の固定クロック。ロックアウトの翌営業日解除を決定的に検証するため時刻・当日を明示制御する。
internal sealed class FakeClock(DateTimeOffset utcNow, DateOnly today) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public DateOnly Today { get; set; } = today;
}

// テスト用のポートフォリオ状態プロバイダ。判定入力を明示的に組み立てる。
internal sealed class FakePortfolioStateProvider(PortfolioState state) : IPortfolioStateProvider
{
    public PortfolioState State { get; set; } = state;

    public PortfolioState GetCurrent() => State;
}
