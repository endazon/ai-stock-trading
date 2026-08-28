using ConfigurationService.Application.Ports;
using ConfigurationService.Application.State;
using ConfigurationService.Domain;
using AiStockTrading.Shared.Kernel.Trading;

namespace ConfigurationService.Application.Adapters;

// FR-17, IADR-0021: 全体前提条件ストアのインメモリ実装（テスト・単体実行用）。PostgreSQL 永続化は Worker の
// EfAssumptionsStore で差し替える。既定値を Version=1 でシードし、Save は expectedVersion 一致時のみ +1 する。
public sealed class InMemoryAssumptionsStore : IAssumptionsStore
{
    private readonly Lock _gate = new();
    private TradingAssumptions _current = TradingAssumptionsDefaults.Create();
    private int _version = 1;

    public VersionedAssumptions GetCurrent()
    {
        lock (_gate)
        {
            return new VersionedAssumptions(_current, _version);
        }
    }

    public int Save(TradingAssumptions assumptions, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(assumptions);
        lock (_gate)
        {
            if (expectedVersion != _version)
                throw new AssumptionsConcurrencyException(expectedVersion, _version);

            _current = assumptions;
            _version++;
            return _version;
        }
    }
}
