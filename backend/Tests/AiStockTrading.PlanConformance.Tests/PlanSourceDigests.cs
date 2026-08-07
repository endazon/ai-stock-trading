namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// NFR, #378, IADR-0166: 計画書の 1 節と、それを**最後に <see cref="PlanRiskDefaults"/> へ転記した時点**の
/// ダイジェスト。
/// </summary>
/// <param name="RelativePath">planning submodule ルートからの相対パス。</param>
/// <param name="HeadingPrefix">節の見出しの前方一致（例: <c>"### 1."</c> / <c>"## 決定"</c>）。</param>
/// <param name="Sha256">正規化した節本文の SHA-256（小文字 16 進）。</param>
/// <param name="Citation"><see cref="PlanRiskDefaults"/> 側の出典表記と対応させる文字列。</param>
public sealed record PlanSourceDigest(string RelativePath, string HeadingPrefix, string Sha256, string Citation);

/// <summary>
/// NFR, #378, IADR-0166: <b>計画書 → <see cref="PlanRiskDefaults"/> の人手転記</b>を検知するためのダイジェスト表。
/// <para>
/// <b>本表が赤くなったときの意味は「値が間違っている」ではない。</b>「計画側の当該節が変わった。
/// <see cref="PlanRiskDefaults"/> を読み直して転記し、ダイジェストを更新せよ」である。
/// 値の照合は <see cref="PlanConformanceTests"/> の検査1 が担う。本表は<b>その手前の 1 ホップ</b>だけを見る。
/// </para>
/// <para>
/// <b>ダイジェストだけを更新して緑に戻すことは技術的に可能である。</b> これは本機構の限界であり
/// （IADR-0166 決定1 の「気づく機会を作る」という位置づけの帰結）、レビューで見るしかない。
/// </para>
/// <para>
/// <b>節単位であり、ファイル全体ではない</b>（IADR-0166 決定2）。計画書には <c>## 変更履歴</c> が
/// 改訂のたびに追記されるため、全体ハッシュでは計画側のあらゆる更新で赤くなり、
/// 「中身を読まずにダイジェストだけ更新する」運用へ堕ちる。それは検査が無いより悪い。
/// </para>
/// </summary>
public static class PlanSourceDigests
{
    private const string Assumptions = "projects/ai-stock-trading/06_technical/05_trading-assumptions.md";
    private const string Adr = "projects/ai-stock-trading/07_adr";

    public static IReadOnlyList<PlanSourceDigest> All { get; } =
    [
        // --- 05_trading-assumptions: PlanRiskDefaults が引用する節 ---
        new(Assumptions, "### 1.", "002e780b89e35c417dbb83d52b5909e3a6172f101397cb5cf25e56ccbcab18d0", "05_trading-assumptions §1（口座・税制）"),
        new(Assumptions, "### 3.", "b5379cd918f045045c4093d107d105db31cc292fd9338e4cb09701bd50cb57ca", "05_trading-assumptions §3（為替・通貨）"),
        new(Assumptions, "### 4.", "055a1e1bea0d802312dd6d729ab50d8531c5eb854dbf3e2881b521c79307cf4b", "05_trading-assumptions §4（計算・判断の方針）"),
        new(Assumptions, "### 5.", "254ce68de32fce3a7feef107816a213094c73d4d628dc459ee95da13c04bba81", "05_trading-assumptions §5（リスク統制・取引ガードの既定値）"),
        new(Assumptions, "### 6.", "6993317915fda91db5358f44f5d692b76a7076a81345d1c17d3534158a1c1b16", "05_trading-assumptions §6（運用費用の上限）"),
        new(Assumptions, "### 6.1", "11abf433eae197b84c8e7c92169bbf031e26fe4205ede120ee201d7de088f6a4", "05_trading-assumptions §6.1（LLM 費用の対象範囲と単価前提）"),

        // --- ADR: `## 決定` 節のみ。`## 結果`・`## 関連`・追記履歴は対象外（IADR-0166 決定2） ---
        new($"{Adr}/ADR-0008_staged-gates-and-backtest.md", "## 決定", "ac7bfa4cef8c2fcff096fe7c2d3676a8981f82e4289db4967c3c3968c14a5009", "ADR-0008 決定"),
        new($"{Adr}/ADR-0016_short-selling-staged-release.md", "## 決定", "722792e44be9789fee19a30cb35d8d0a7ba10c35bb1362d2031c00cb0109be33", "ADR-0016 決定"),
        new($"{Adr}/ADR-0018_risk-defaults-sync-and-stage0-dd.md", "## 決定", "4820ff53ae1dff2d5932eb6df2c55be8a1e57954d4a9043329edb764d3598f5e", "ADR-0018 決定"),
        new($"{Adr}/ADR-0022_fx-rate-source-and-freshness.md", "## 決定", "907495fea4d0d225750e860d5ec4b489e49ee2ff9d23353a09c765762f88891c", "ADR-0022 決定"),
    ];

    /// <summary>まだ実測値を埋めていない項目のプレースホルダ。</summary>
    public const string Unset = "TBD";
}
