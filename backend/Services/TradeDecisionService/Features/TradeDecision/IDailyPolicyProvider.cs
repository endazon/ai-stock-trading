using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-07, IADR-0028: 確定済み日報の方針。確定前・未確定・依存先障害なら null を返し、判断サービスは取引しない（FR-07・フェイルセーフ）。
// 実データは報告書サービス（#14）の GET /reports/daily-policy を同期照会して供給する（HttpDailyPolicyProvider）。
// 同期 HTTP を sync-over-async にしないため非同期とする。Summary は LLM プロンプトに渡す方針要約。
public interface IDailyPolicyProvider
{
    Task<DailyPolicy?> GetCurrentAsync(CancellationToken cancellationToken = default);
}

// 確定済み日報の方針（当日）。Summary は当日の売買方針の要約。
// FR-04, FR-11, ADR-0068 決定4, #1373, IADR-0548 決定5: Reference はこの方針を持つ確定版の期間キーと版番号。判断の記録
// （TradeDecisionMade / TradeDecisionHeld の Policy）へ載せる。🔴 null＝送り手が返さなかった（並走中の古い送り手）。
// **方針の有無の判定には使わない**（欠けていても方針は方針であり、判断を止めない）。
// 🔴 FR-07, #1423 M-6, ADR-0075 決定1・2, IADR-0583 決定3: NoResponseHalted は「無応答時の停止」が効いている印（報告書サービスが判定する）。
// 立っていれば判断の合流点が全体を見送る。**方針（Date・Summary）は直近の確定済み日報のまま**であり、閉場前の手仕舞いは印を読まない
// （方針を読めないへ倒さない。ADR-0070 決定2 の分岐に当てない）。欠落（古い送り手）は false＝停止していない。
public record DailyPolicy(DateOnly Date, string Summary, DailyPolicyReference? Reference = null, bool NoResponseHalted = false);
