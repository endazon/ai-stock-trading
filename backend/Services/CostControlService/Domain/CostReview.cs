namespace CostControlService.Domain;

// NFR（費用）, FR-16, 05_trading-assumptions §6: 月報の費用レビュー（費用÷資金比率）。数値はコードで算出する。
public static class CostReview
{
    // 費用÷資金 比率（資金 0 以下なら 0）。月報で必ずレビューする指標（05 §6 注意）。
    public static decimal CostToCapitalRatio(decimal totalCost, decimal capital) =>
        capital <= 0m ? 0m : totalCost / capital;
}
