using AiStockTrading.Shared.Contracts.Trading;
using BacktestService.Domain;
using AwesomeAssertions;
using Xunit;

namespace BacktestService.Tests;

// FR-15, FR-20, ADR-0016 決定14, #388, IADR-0304: 検証した走行が空売りを含んでいたかの**観測**。
// 計画は「空売りを含む戦略で Stage 0 の 7 条件を再度満たす」ことを Stage 3 の空売り実弾解禁の
// 前提条件とするが、「含む」の判定方法を定めていない（環流 planning#534）。実装は保守的な側＝観測を採る。
public class ShortSellingObservationTests
{
    private static readonly DateOnly Day1 = new(2026, 7, 15);
    private static readonly DateOnly Day2 = new(2026, 7, 16);

    private static BacktestFill Fill(
        DateOnly date, int signedQuantity, string symbol = "AAPL", Market market = Market.UnitedStates) =>
        new(date, symbol, market, signedQuantity, 100m, 1m, 0m);

    [Fact]
    public void 売り建てから入る走行は空売りを含むと観測される()
    {
        var fills = new[] { Fill(Day1, -10), Fill(Day2, +10) };

        ShortSellingObservation.Includes(fills).Should().BeTrue();
    }

    // **否定形**: 買いから入って手仕舞うだけの走行は空売りを含まない。
    [Fact]
    public void 買い建てから入り手仕舞うだけの走行は空売りを含まない()
    {
        var fills = new[] { Fill(Day1, +10), Fill(Day2, -10) };

        ShortSellingObservation.Includes(fills).Should().BeFalse();
    }

    // **否定形**: 建玉をゼロ跨ぎでドテンした走行は、跨いだ先が売り建てなので空売りを含む。
    // 「売り注文があったか」ではなく「売り建玉を持ったか」を見ていることの確認である。
    [Fact]
    public void ゼロを跨いで売り建てへ反転した走行は空売りを含む()
    {
        var fills = new[] { Fill(Day1, +10), Fill(Day2, -15) };

        ShortSellingObservation.Includes(fills).Should().BeTrue();
    }

    // **否定形**: 銘柄ごとに畳む。ある銘柄の買い建玉が、別銘柄の売り建玉を打ち消してはならない。
    [Fact]
    public void 建玉は銘柄ごとに畳み他銘柄の買い建玉で相殺しない()
    {
        var fills = new[] { Fill(Day1, +10, "AAPL"), Fill(Day1, -5, "MSFT") };

        ShortSellingObservation.Includes(fills).Should().BeTrue();
    }

    // **否定形**: 市場が違えば別建玉である（同じティッカーが複数市場に存在し得る）。
    [Fact]
    public void 建玉は市場ごとに畳み別市場の買い建玉で相殺しない()
    {
        var fills = new[]
        {
            Fill(Day1, +10, "0001", Market.Japan),
            Fill(Day1, -5, "0001", Market.UnitedStates),
        };

        ShortSellingObservation.Includes(fills).Should().BeTrue();
    }

    // **否定形（保守的な既定）**: 空売り注文を出したが約定しなかった走行は「含まない」。
    // 約定していない以上、借株料もドローダウンも検証していない（IADR-0304 決定2）。
    // 約定列が空であることが、そのまま未約定の表現である（BacktestRun.UnfilledOrderCount 側に残る）。
    [Fact]
    public void 約定が無い走行は空売りを含まない()
    {
        ShortSellingObservation.Includes([]).Should().BeFalse();
    }

    // **否定形（fail-safe）**: 約定列そのものが無い（null）場合も「含まない」へ倒す。
    [Fact]
    public void 約定列がnullなら空売りを含まない()
    {
        ShortSellingObservation.Includes(null).Should().BeFalse();
    }

    // 空売りを含む走行は、その後どれだけ買い建てへ戻しても「含む」のままである
    // （解禁条件は「空売りを含む戦略で合格したか」であり、最終建玉の向きではない）。
    [Fact]
    public void 空売りの後に買い建てへ戻しても含むのままである()
    {
        var fills = new[] { Fill(Day1, -10), Fill(Day2, +30) };

        ShortSellingObservation.Includes(fills).Should().BeTrue();
    }
}
