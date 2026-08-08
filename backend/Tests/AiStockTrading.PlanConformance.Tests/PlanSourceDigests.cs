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
    private const string Requirements = "projects/ai-stock-trading/02_requirements/01_requirements.md";
    private const string UseCases = "projects/ai-stock-trading/03_usecases/01_usecases.md";
    private const string Adr = "projects/ai-stock-trading/07_adr";

    // **2026-08-08（#459・pin `a4616a8` → `c2998a6`）に 4 件を更新した。**
    // 10 件のうち §1 / §4 / §5 / ADR-0016 決定が変化し、§3 / §6 / §6.1 / ADR-0008 / ADR-0018 /
    // ADR-0022 は同一であった。**変化した 4 件はすべて差分を読み、PlanRiskDefaults へ突き合わせた**
    // （結果は docs/specs/20260808_459_planning-pin-advance.md 検査1・検査2）。
    // **値の変更は 1 件も無く、注記を 4 件直した。** 更新の根拠を残すのは、本表が
    // 「読まずにハッシュだけ更新する」ことを技術的には許してしまうためである（下の注意書き）。
    public static IReadOnlyList<PlanSourceDigest> All { get; } =
    [
        // --- 05_trading-assumptions: PlanRiskDefaults が引用する節 ---
        // §1: 譲渡益税率の**値は不変**（20.315%）。米国株の譲渡益にも同率である旨と、§4 の判定に
        //     用いる旨が追記された（2026-08-07 確定）。
        new(Assumptions, "### 1.", "b31290d9cb2045bbae3835799bc089e488615f06f0eb07720fb3b48087715f88", "05_trading-assumptions §1（口座・税制）"),
        new(Assumptions, "### 3.", "b5379cd918f045045c4093d107d105db31cc292fd9338e4cb09701bd50cb57ca", "05_trading-assumptions §3（為替・通貨）"),
        // §4: 倍率 2・基準（往復費用＋税）とも**不変**。税の基準の明示（解いたしきい値 2.684·C）と
        //     「解が無い領域では見送る」が追記された。いずれも IADR-0173 / IADR-0177 で実装済み。
        new(Assumptions, "### 4.", "6f0d881f386e5b4ab0dfa239e31fe80e590b89f68ee6f43f592fa128ca0bed6f", "05_trading-assumptions §4（計算・判断の方針）"),
        // §5: **既存行の値は 1 件も変わっていない。** 変化は注記の追記と新規 2 行
        //     （維持率の判定に用いる等号＝ `≦`・借株料の累計）。等号は #459 / IADR-0178 で追随した。
        new(Assumptions, "### 5.", "e1b348cf6e74f43b7f382fca1b892d9a723ffd00b4de2487b85b41da8000cb83", "05_trading-assumptions §5（リスク統制・取引ガードの既定値）"),
        new(Assumptions, "### 6.", "6993317915fda91db5358f44f5d692b76a7076a81345d1c17d3534158a1c1b16", "05_trading-assumptions §6（運用費用の上限）"),
        new(Assumptions, "### 6.1", "11abf433eae197b84c8e7c92169bbf031e26fe4205ede120ee201d7de088f6a4", "05_trading-assumptions §6.1（LLM 費用の対象範囲と単価前提）"),

        // --- ADR: `## 決定` 節のみ。`## 結果`・`## 関連`・追記履歴は対象外（IADR-0166 決定2） ---
        new($"{Adr}/ADR-0008_staged-gates-and-backtest.md", "## 決定", "ac7bfa4cef8c2fcff096fe7c2d3676a8981f82e4289db4967c3c3968c14a5009", "ADR-0008 決定"),
        // ADR-0016: `Proposed` → `Accepted`。裁定待ち 10 点が 2026-08-07 に確定した。**値の変更は無い。**
        //     10 点のうち 7 点は実装の追認（IADR-0159 / IADR-0160）、1 点は実装不要、
        //     1 点（Q6 の等号）は #459 で追随、1 点（Q9 の照会 API）は issue へ切り出した。
        new($"{Adr}/ADR-0016_short-selling-staged-release.md", "## 決定", "eaebe75e8df47a8817548a8b8d3bac2b9c4a6e913f4a7108e892976cd641d422", "ADR-0016 決定"),
        new($"{Adr}/ADR-0018_risk-defaults-sync-and-stage0-dd.md", "## 決定", "4820ff53ae1dff2d5932eb6df2c55be8a1e57954d4a9043329edb764d3598f5e", "ADR-0018 決定"),
        new($"{Adr}/ADR-0022_fx-rate-source-and-freshness.md", "## 決定", "907495fea4d0d225750e860d5ec4b489e49ee2ff9d23353a09c765762f88891c", "ADR-0022 決定"),

        // --- 🔴 2026-08-08（#459）に追加した 3 節。**本表が PlanRiskDefaults の出典を網羅していなかった。**
        //
        //     【本作業が見つけた穴】 PlanRiskDefaults の 45 行が挙げる出典を機械的に数えたところ、
        //     **3 文書が本表に無かった**（`02_requirements`・`03_usecases`・ADR-0021）。とくに
        //     **`Stage.Values` の出典は「FR-20」だけであり、本表のどの節にも掛かっていなかった。**
        //     しかも**その 3 文書はいずれも本 pin 前進（a4616a8 → c2998a6）で変更されている** ——
        //     `02_requirements` は +21 行（FR-10 の維持率・FR-21 の新設）、`03_usecases` は +5 行
        //     （UC-06 の等号）、ADR-0021 は +21 行（決定4-5 の GFV 解除条件）。
        //     **本表は 1 件も赤くならなかった。** 今回は人手で 3 文書を読んで値が動いていないことを
        //     確かめたが、**「人が見たから大丈夫」は本機構が置き換えようとしているもの**である。
        //
        //     出典が本表に無ければ、[[IADR-0166]] の 1 ホップはその行に**掛からない**。
        //     `PlanConformanceTests`（表 ⇄ 実装）は緑のままなので、**穴は「赤くならないこと」として現れる**。
        new(Requirements, "## 機能要求", "fa973f38b9a279ac7784195f34e5a8ec6de007ba35ca4089d1e8c85b942f8e94", "02_requirements §機能要求（FR-10 / FR-19 / FR-20 の表）"),
        // 見出しは `### UC-06: …` のためコロンまで含める（直後が区切りである必要がある。#444 の指摘）。
        new(UseCases, "### UC-06:", "93f676311991a30757b085c6d83bfe82c6878f4e2db6d18b99f15c338b3dd704", "UC-06（設定変更・一時停止・緊急停止）"),
        new($"{Adr}/ADR-0021_us-account-type-dual-support.md", "## 決定", "107a5118a06b29818625462a224b988717286385f94d9074cac04a842c3be94a", "ADR-0021 決定"),
    ];

    /// <summary>まだ実測値を埋めていない項目のプレースホルダ。</summary>
    public const string Unset = "TBD";
}
