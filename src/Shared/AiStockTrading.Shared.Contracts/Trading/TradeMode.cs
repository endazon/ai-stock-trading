namespace AiStockTrading.Shared.Contracts.Trading;

// FR-12, FR-20: ペーパー/実弾の動作モード。段階ゲートが許可するモードのみ実行できる
public enum TradeMode
{
    Paper,
    Live,
}
