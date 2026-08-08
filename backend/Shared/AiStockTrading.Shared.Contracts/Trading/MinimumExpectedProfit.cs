namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-17, 05_trading-assumptions §4, #358, IADR-0173:
/// 最小期待利益のしきい値（<b>往復費用＋税</b>の m 倍）を解く純関数。
/// <para>
/// 計画 §4（利用者決定 2026-07-23）は「往復費用＋税の <b>2 倍</b> を下回る取引は見送り」と定める。
/// <b>税は結果に依存する</b>——譲渡益税は譲渡益（＝ 利益 − 費用）に掛かるため、しきい値 <c>T</c> は
/// </para>
/// <code>
/// T = m × (C + (T − C) × r)        m = 倍率 / C = 費用 / r = 譲渡益税率
/// </code>
/// <para>
/// という<b>不動点</b>で定まる。これを <c>T</c> について解くと <c>T = m × C × (1 − r) / (1 − m × r)</c> となる。
/// <c>m = 2</c> / <c>r = 0.20315</c> では <b>T ≈ 2.684 × C</b>（旧実装の <c>1.5 × C</c> の約 1.79 倍）。
/// </para>
/// <para>
/// <b>本式は「税引後で費用の m 倍が残る」ことを意味しない。</b> 計画が書いているのは「利益が
/// <b>費用と税の合計</b>の m 倍以上であること」であり、本式はそれをそのまま解いたものである。
/// </para>
/// <para>
/// <b>ここが単一の情報源である。</b> 同じ式を必要とするのは設定サービスの概算費用関数
/// （<c>CostCalculator</c>）と取引判断の採算ゲート（<c>ProfitabilityGate</c>）であり、両者は別ユニットの
/// Domain で互いを参照できない。<b>式を 2 か所に書くと、片方だけ直したときに気付けない</b>ため、
/// 双方が参照する共有契約へ置く。
/// </para>
/// </summary>
public static class MinimumExpectedProfit
{
    /// <summary>
    /// しきい値を解く。<b>解が無いときは <c>null</c> を返す。</b>
    /// <para>
    /// 分母 <c>1 − m × r</c> が 0 以下になると、<b>どれだけ利益が大きくても条件を満たせない</b>
    /// （利益を増やすと税も同じ速さで増え、要求が伸び続ける）。このとき<b>負のしきい値を返して
    /// 全通過させることは絶対に避ける</b>——呼び出し側は安全側（見送り）へ倒すこと。
    /// </para>
    /// <para>
    /// 倍率・税率はいずれも運用で変更できる設定値であるため、この状態は<b>構成の異常として実際に到達し得る</b>。
    /// 現行値（<c>m = 2</c> / <c>r = 0.20315</c>）では <c>m × r = 0.4063</c> であり余裕がある。
    /// </para>
    /// </summary>
    /// <param name="cost">費用（往復の概算取引費用 ＋ 判断費用）。負値は 0 に正規化する。</param>
    /// <param name="multiple">
    /// 最小期待利益倍率 <c>m</c>（計画確定値 2）。<b>非正の値は構成異常として解無し（<c>null</c>）に倒す</b>（#461）。
    /// </param>
    /// <param name="taxRate">譲渡益税率 <c>r</c>（計画確定値 0.20315）。負値は 0 に正規化する。</param>
    /// <returns>最小期待利益（<b>費用控除前</b>の期待利益と比較する）。解が無ければ <c>null</c>。</returns>
    public static decimal? Threshold(decimal cost, decimal multiple, decimal taxRate)
    {
        var normalizedCost = cost > 0m ? cost : 0m;
        var rate = taxRate > 0m ? taxRate : 0m;

        // #461, IADR-0177: **倍率が非正なら解無しとして扱う。** 費用も税率も正規化されるため
        // 負のしきい値を生み得るのは倍率だけであり、負の倍率では分母 1 − m × r が **1 より大きくなって
        // 下の解無し判定をすり抜ける**（例: m = −2 / r = 0.20315 → 分母 1.406 > 0）。結果は負のしきい値、
        // すなわち**全通過**である。採算ゲートは呼び出し前に m <= 0 を弾いていたが、費用算出は
        // 素通ししていた —— **同じ式を守る規則は式の側に置く**（呼び出し側ごとの防御は片方だけ抜ける）。
        if (multiple <= 0m) return null;

        var denominator = 1m - multiple * rate;
        if (denominator <= 0m) return null;

        // 税率 0 では (1 − r) / (1 − m × r) = 1 となり、従来式 m × C へ退化する。
        return multiple * normalizedCost * (1m - rate) / denominator;
    }
}
