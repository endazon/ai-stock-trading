using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 T-10-760〜T-10-764, FR-10, FR-03, UC-02, ADR-0040 決定1（S1）, #936, IADR-0393:
// 市場監視が比べる損切りライン（取引台帳の射影 → GET /risk-controls/open-positions）は、保有中のエントリー（ロット）の
// うち**最も保護的なライン**である。減少は古いロットから削る。
// 市場監視はこのラインで到達を 1 件出し、発注執行は S1 の行ごとに自分のラインで判定する（IADR-0344 決定4）。
// したがってここが「発注執行に残る S1 の行のどのラインよりも保護的か等しい」限り、どの行の損切りも遅れない（T-10-764）。
public class PortfolioProjectionStopLossLotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 13, 30, 0, TimeSpan.Zero);

    private static LedgerFill Buy(int qty, decimal? stop, int minute) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, qty, 335m, T0.AddMinutes(minute), stop);

    private static LedgerFill Sell(int qty, int minute) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, qty, 331m, T0.AddMinutes(minute));

    private static LedgerFill Short(int qty, decimal? stop, int minute) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Open, qty, 335m, T0.AddMinutes(minute), stop);

    private static LedgerFill Cover(int qty, int minute) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Close, qty, 336m, T0.AddMinutes(minute));

    private static OpenPosition Project(params LedgerFill[] fills) =>
        PortfolioProjection.ProjectOpenPositions(fills).Should().ContainSingle().Subject;

    // ---- T-10-760: 稼働 PoC の配置（713 株 331.67 を先に・715 株 330.88 を後に建てた） ----

    [Fact]
    public void 稼働中の2本建てでは先に建てた高いラインを採る_後から建てた低いラインに丸めない()
    {
        // #936 の観測: 市場監視は 1,428 株を 330.88 と比べていた（最新エントリー）。713 株の行の 331.67 が使われず、
        // 価格が 331.67〜330.88 の間にある間は損切りが発火しない（0.79 遅れる）。
        var position = Project(Buy(713, 331.67m, 0), Buy(715, 330.88m, 60));

        position.Quantity.Should().Be(1_428);
        position.StopLossPrice.Should().Be(331.67m, "ロングの最も保護的なライン＝最も高いライン");
        position.StopLossUnknown.Should().BeFalse("両方のエントリーがラインを持つ");
    }

    // ---- T-10-761: 同じ配置で 713 株の行が売れたら 330.88 へ下がる（古いロットから削る） ----

    [Fact]
    public void 稼働中の配置で先に建てた713株が売れたら残る715株のラインへ下がる()
    {
        // 331.67 で到達 → 発注執行が 713 株の行だけを決済 → 約定が台帳に入る。古いロット（713 株 331.67）を削る。
        // 削らずに 331.67 を持ち続けると、330.88 の行しか無いのに毎巡回「到達」が出る（発注執行は Matched=0）。
        var position = Project(Buy(713, 331.67m, 0), Buy(715, 330.88m, 60), Sell(713, 120));

        position.Quantity.Should().Be(715);
        position.StopLossPrice.Should().Be(330.88m);
    }

    // ---- T-10-762: 逆の配置では、取り違えても保護的な側（高いライン）に残る ----

    [Fact]
    public void 低いラインを先に建て高いラインが先に売れた配置ではラインは保護的な側に残り低い方へは下がらない()
    {
        // 715 株 330.88 を先に、713 株 331.67 を後に建てた。331.67 で到達して 713 株の行が売れる。
        // 台帳はどの決済がどのエントリーを閉じたかを持たないので古いロット（330.88）から削る＝取り違える。
        // 🔴 取り違えの倒れ方は「ラインが実際より保護的」側だけである: 331.67 が残り、残る 715 株の行（330.88）の
        // 損切りは遅れない（価格が 330.88 に達すれば到達は必ず出ている）。遅れる側（330.88 へ下がってから
        // 330.88 未満の行が残る）には倒れない。従来（最新エントリー）でもこの配置は 331.67 だった。
        var position = Project(Buy(715, 330.88m, 0), Buy(713, 331.67m, 60), Sell(713, 120));

        position.Quantity.Should().Be(715);
        position.StopLossPrice.Should().Be(331.67m);
    }

    // ---- T-10-763: ショート・反転・全決済 ----

    [Fact]
    public void ショートは最も低いラインを採り反転は反転後の約定だけのラインにし全決済でロットを捨てる()
    {
        // ショートの最も保護的なライン＝最も低いライン（到達はショート: 現在値 ≧ ライン）。
        Project(Short(10, 340m, 0), Short(10, 338m, 60)).StopLossPrice.Should().Be(338m);

        // 反転: ロングの 331.67 は反転後のショートへ引き継がない。
        var flipped = Project(Buy(5, 331.67m, 0), Short(8, 345m, 60));
        flipped.Side.Should().Be(TradeSide.Sell);
        flipped.Quantity.Should().Be(3);
        flipped.StopLossPrice.Should().Be(345m);

        // 全決済の後に建て直したら、前の建玉のライン（331.67）は残らない。
        Project(Buy(10, 331.67m, 0), Sell(10, 60), Buy(10, 320m, 120)).StopLossPrice.Should().Be(320m);

        // ショートの一部買戻しも古いロットから削る（338 のロットだけが残る）。
        Project(Short(10, 340m, 0), Short(10, 338m, 60), Cover(10, 120)).StopLossPrice.Should().Be(338m);
        Project(Short(10, 336m, 0), Short(10, 338m, 60), Cover(10, 120)).StopLossPrice.Should().Be(338m);
    }

    // ---- T-10-764: 遅らせないことの掃き（発注執行の割り当て規則を写したモデルと突き合わせる） ----

    /// <summary>発注執行の保護記録の写し（S1＝ソフトウェア逆指値・S0＝ブローカー側逆指値）。作成順に並べる。</summary>
    private sealed class Row(int quantity, decimal line, bool software)
    {
        public int Quantity { get; set; } = quantity;
        public decimal Line { get; } = line;
        public bool Software { get; } = software;
    }

    [Fact]
    public void 無作為な建てと損切りと外部の減少の列で台帳のラインは発注執行に残るS1の行のどのラインより保護的か等しい()
    {
        // モデル化した規則（本番の側の正本）:
        // - 市場監視は台帳のライン（本射影）と現在値を比べ、達したら到達を出す（MarketMonitorAppService）。
        // - 発注執行は到達を受けて、**行自身のラインに達した S1 の行だけ**を売る（SoftwareStopExecutor.Reached）。
        // - S0 の行はブローカーの逆指値が自分のラインで売る（市場監視に依らない）。
        // - 外部要因の減少は S1 の行 → S0 の行の順、それぞれ古い行から割り当てる（IADR-0344 追記(5) 決定1）。
        // 性質: 価格が動いた直後に「自分のラインに達しているのに残っている S1 の行」が 1 本も無いこと
        // （＝どの行の損切りも遅れない）。併せて、台帳のラインがどの S1 の行のラインより低くないこと。
        var random = new Random(936);
        var checkedMoves = 0;
        var s1Closes = 0;
        for (var trial = 0; trial < 400; trial++)
        {
            var rows = new List<Row>();
            var fills = new List<LedgerFill>();
            var minute = 0;
            var price = 100m;

            for (var step = 0; step < 40; step++)
            {
                var roll = random.Next(10);
                if (roll < 4)
                {
                    // 建て（エントリー）。ラインは現在値より下。S1 と S0 を混ぜる。
                    var qty = random.Next(1, 30);
                    var line = price - random.Next(1, 12);
                    rows.Add(new Row(qty, line, software: random.Next(3) != 0));
                    fills.Add(Buy(qty, line, minute++));
                }
                else if (roll < 8)
                {
                    // 価格が動く。S0 はブローカーが、S1 は市場監視の到達を受けた発注執行が売る。
                    price += random.Next(-6, 5);
                    var brokerSold = SellReached(rows, price, software: false);
                    if (brokerSold > 0)
                        fills.Add(Sell(brokerSold, minute++));

                    var line = Line(fills);
                    if (line is { } monitorLine && StopLossReached(price, monitorLine))
                    {
                        var systemSold = SellReached(rows, price, software: true);
                        if (systemSold > 0)
                        {
                            fills.Add(Sell(systemSold, minute++));
                            s1Closes++;
                        }
                    }

                    rows.Where(r => r.Software && StopLossReached(price, r.Line)).Should().BeEmpty(
                        $"試行 {trial} 手 {step}: 価格 {price} に達した S1 の行が残っている＝損切りが遅れた（台帳のライン {line}）");
                    checkedMoves++;
                }
                else if (rows.Count > 0)
                {
                    // 外部要因の減少（手動決済・取り込み）。発注執行は S1 → S0、それぞれ古い行から割り当てる。
                    var total = rows.Sum(r => r.Quantity);
                    var reduce = random.Next(1, total + 1);
                    ReduceOldestFirst(rows.Where(r => r.Software).ToList(), ref reduce);
                    ReduceOldestFirst(rows.Where(r => !r.Software).ToList(), ref reduce);
                    rows.RemoveAll(r => r.Quantity == 0);
                    fills.Add(Sell(total - rows.Sum(r => r.Quantity), minute++));
                }

                var projected = PortfolioProjection.ProjectOpenPositions(fills);
                projected.Sum(p => p.Quantity).Should().Be(rows.Sum(r => r.Quantity), "台帳と行の株数は一致させてある");
                if (rows.Any(r => r.Software))
                {
                    projected.Single().StopLossPrice.Should().BeGreaterThanOrEqualTo(
                        rows.Where(r => r.Software).Max(r => r.Line),
                        $"試行 {trial} 手 {step}: 台帳のラインが残っている S1 の行のラインより低い");
                }
            }
        }

        // 掃きが空虚でないこと（到達を伴う価格の動きと S1 の決済が実際に起きている）。
        checkedMoves.Should().BeGreaterThan(4_000);
        s1Closes.Should().BeGreaterThan(500);
    }

    private static decimal? Line(List<LedgerFill> fills) =>
        PortfolioProjection.ProjectOpenPositions(fills).SingleOrDefault()?.StopLossPrice;

    private static bool StopLossReached(decimal price, decimal line) => price <= line;

    private static int SellReached(List<Row> rows, decimal price, bool software)
    {
        var reached = rows.Where(r => r.Software == software && StopLossReached(price, r.Line)).ToList();
        foreach (var row in reached)
            rows.Remove(row);
        return reached.Sum(r => r.Quantity);
    }

    private static void ReduceOldestFirst(List<Row> rows, ref int remaining)
    {
        foreach (var row in rows)
        {
            var taken = Math.Min(row.Quantity, remaining);
            row.Quantity -= taken;
            remaining -= taken;
            if (remaining == 0)
                return;
        }
    }
}
