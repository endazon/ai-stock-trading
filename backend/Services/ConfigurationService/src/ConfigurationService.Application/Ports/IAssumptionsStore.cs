using AiStockTrading.Configuration.Application.State;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Kernel.Trading;

namespace AiStockTrading.Configuration.Application.Ports;

// FR-17, IADR-0012/0021: 全体前提条件ストア（単一行＋Version）。未設定時は既定値をシードして返す。
public interface IAssumptionsStore
{
    /// <summary>現在の前提条件とバージョン（未設定は既定をシード）。</summary>
    VersionedAssumptions GetCurrent();

    /// <summary>
    /// 前提条件を保存する。expectedVersion が現在版と一致する場合のみ更新し、Version を +1 して返す。
    /// 不一致なら <see cref="AssumptionsConcurrencyException"/> を送出する（楽観的排他制御）。
    /// </summary>
    int Save(TradingAssumptions assumptions, int expectedVersion);
}
