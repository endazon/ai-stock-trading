using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;

// FR-06, FR-16, FR-10, #611, ADR-0022 決定5, IADR-0286 決定1・決定2: 為替レート源の読み（JPY 1 単位あたりの USD 額）から
// **基準通貨（USD）1 単位あたりの表示通貨（JPY）額**＝「1 USD あたりの円」を導く純関数。
//
// 🔴 **認識時（リスク管理の承認記録）と期末（報告書の期末レート）の両方がこの 1 箇所を通る。**
// 2 箇所に別々の逆数・鮮度の規則を書くと、片方だけ直して静かにずれる（IADR-0197 決定3 と同じ理由）。
//
// 規則:
//   - 読みが無い（源が無い・取得不可）→ null（＝未記録／未供給。**推定しない**）。
//   - 鮮度切れ（Expired＝30 日超）→ null。統制が「新規建てに使えない」と判定した観測を、
//     税務にも効く数値（為替差損益）の根にしない。警告域（5〜30 日）は取引側と同じく採る（計画 §5「直近レートで続行」）。
//   - 逆数は丸めない（BojFxRateSource / FredFxRateSource と同じ規律）。ただし**観測値の復元に伴う末尾の雑音だけ落とす**——
//     源は「1 USD あたりの円（小数 2〜4 桁）」を逆数にして返しており、その逆数を取ると 28 桁の末尾に 1e-26 程度の
//     雑音が乗る。10 桁で丸めると観測値そのものに戻り、丸めが値を変えることはない（観測値の桁数 ≪ 10）。
public static class FxBaseToDisplayRate
{
    /// <summary>復元時の丸め桁数。観測値（小数 2〜4 桁）より十分大きく、逆数の逆数の末尾雑音より十分小さい。</summary>
    public const int Decimals = 10;

    /// <summary>
    /// 読みから「1 USD あたりの円」を導く。解決できない・鮮度切れ・非正の観測は <c>null</c>。
    /// </summary>
    public static decimal? FromReading(FxRateReading? reading)
    {
        if (reading is null)
            return null;

        if (reading.Freshness == FxRateFreshness.Expired)
            return null;

        // ポート契約: Rate は「quote 通貨（JPY）1 単位あたりの基準通貨（USD）額」。0 以下は源が弾いているが、
        // 契約を破る実装が混ざっても除算例外で承認記録・報告書生成を落とさない。
        var jpyToUsd = reading.Rate.Rate;
        if (jpyToUsd <= 0m)
            return null;

        return decimal.Round(1m / jpyToUsd, Decimals, MidpointRounding.ToEven);
    }
}
