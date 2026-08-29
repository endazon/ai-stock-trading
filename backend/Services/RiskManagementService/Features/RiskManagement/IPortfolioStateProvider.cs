
namespace RiskManagementService.Features.RiskManagement;

// FR-10: 判定時点のポートフォリオ状態（保有・当日発注累計・実現/含み損益・DD・連敗・当日取引銘柄）の供給。
// 実データは取引台帳（OrderApproved/OrderExecuted の純射影・IADR-0018）から得る。実体は LedgerPortfolioStateProvider が配線済み。
public interface IPortfolioStateProvider
{
    PortfolioState GetCurrent();
}
