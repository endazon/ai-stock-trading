using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// NFR, #378, IADR-0166: 計画書 → <see cref="PlanRiskDefaults"/> の**人手転記**を検知する。
/// <para>
/// <see cref="PlanConformanceTests"/> は <see cref="PlanRiskDefaults"/> と実装の乖離を見るが、
/// その手前の「計画書 → <see cref="PlanRiskDefaults"/>」は人手であり無検査だった（#374 で実測）。
/// 本テストがその 1 ホップを見る。
/// </para>
/// </summary>
public class PlanSourceDigestTests
{
    // 計画書の節が最後の転記時点から変わっていないこと。
    //
    // **赤の意味**: 「値が間違っている」ではなく「計画側が変わった。PlanRiskDefaults を読み直して
    // 転記し、ダイジェストを更新せよ」である。
    //
    // **submodule 未取得なら skip する**（IADR-0166 決定4・fail-open）。ローカル環境差で CI を落とさない。
    // ただし **skip したことは必ず notice で出す** —— 「緑だが検査されていない」を可視化する
    // （check-doc-links.js と同じ規律）。
    [Fact]
    public void 計画書の該当節が最後の転記時点から変わっていない()
    {
        var root = PlanSourceReader.TryFindPlanningRoot();
        if (root is null)
        {
            // **`Assert.Skip` を使う**（`ITestOutputHelper` へ書くだけにしない）。合格したテストの
            // `ITestOutputHelper` 出力は VSTest 経路の既定では表示されず、`--verbosity normal`
            // （MSBuild 側の verbosity）でも出ない。それでは決定4 の「緑だが検査されていないことを
            // 隠さない」という規律が実効しない。`Assert.Skip` なら**実行サマリの Skipped 件数**として
            // 必ず現れる。
            Assert.Skip(
                "planning submodule が未 populate のため計画書ダイジェスト検査を skip した。"
                    + "この範囲は本実行では検査されていない（IADR-0166 決定4）。");
            return;
        }

        var probes = PlanSourceDigests.All.Select(d => PlanSourceReader.Probe(root, d)).ToList();

        // 節そのものが見つからない（見出しの改名・再編・ファイル移動）は、ハッシュ不一致とは別に報告する。
        // 「どこを直せばよいか」が違うためである。
        var missing = probes.Where(p => p.Problem is not null).Select(p => p.Problem!).ToArray();
        missing.Should().BeEmpty(
            "計画書の節を特定できなかった。見出しの改名・再編ならば PlanSourceDigests の "
                + "HeadingPrefix を追随させること。問題: {0}",
            string.Join(" / ", missing));

        var unset = probes
            .Where(p => p.Expected.Sha256 == PlanSourceDigests.Unset)
            .Select(p => $"{p.Expected.Citation} = \"{p.Actual}\"")
            .ToArray();
        unset.Should().BeEmpty(
            "ダイジェスト未設定の項目がある。実測値を PlanSourceDigests へ書き込むこと（初回導入時の手順）。"
                + "実測値: {0}",
            string.Join(" / ", unset));

        var changed = probes
            .Where(p => !p.Matches && p.Problem is null)
            .Select(p => $"{p.Expected.Citation}: 記録「{p.Expected.Sha256[..12]}…」/ 実際「{p.Actual![..12]}…」")
            .ToArray();

        changed.Should().BeEmpty(
            "計画書の該当節が、最後に PlanRiskDefaults へ転記した時点から変わっている。"
                + "**この赤は「値が間違っている」ではなく「読み直して転記せよ」の意味である。** "
                + "(1) 計画側の差分を読む (2) 必要なら PlanRiskDefaults を更新する "
                + "(3) PlanSourceDigests のハッシュを実測値へ更新する。"
                + "**(3) だけを行って緑に戻してはならない**（IADR-0166 決定1 の限界であり、レビューで見る）。"
                + "変わった節: {0}",
            string.Join(" / ", changed));
    }

    // 表そのものの健全性（submodule に依存しないため常に走る）。
    [Fact]
    public void ダイジェスト表に重複が無くパスと見出しが埋まっている()
    {
        PlanSourceDigests.All.Should().NotBeEmpty();

        foreach (var d in PlanSourceDigests.All)
        {
            d.RelativePath.Should().NotBeNullOrWhiteSpace();
            d.HeadingPrefix.Should().StartWith("#", "節の前方一致は見出し記号で始める");
            d.Citation.Should().NotBeNullOrWhiteSpace("赤くなったとき人間がどの節かを特定できる必要がある");
        }

        PlanSourceDigests.All
            .Select(d => (d.RelativePath, d.HeadingPrefix))
            .Should().OnlyHaveUniqueItems("同じ節を二重に登録すると更新漏れが片方に残る");
    }

    // 🔴 **本表が `PlanRiskDefaults` の出典を網羅していること**（#459 で新設）。
    //
    // **【本テストが生まれた経緯】** 2026-08-08 の pin 前進（`a4616a8` → `c2998a6`）で、本表の 10 節が
    // `PlanRiskDefaults` の出典を**網羅していなかった**ことが分かった。**`Stage.Values` の出典は「FR-20」
    // だけであり、本表のどの節にも掛かっていなかった。** ほかに ADR-0021 決定4-1（`Guard.PreventSameDayReentry`）
    // と UC-06（`ShortSell.Limits`）も掛かっていなかった。
    //
    // **しかもその 3 文書は、いずれもその pin 前進で実際に変更されていた** —— それでも本表は
    // **1 件も赤くならなかった**。出典が本表に無ければ [[IADR-0166]] の 1 ホップはその行に掛からず、
    // `PlanConformanceTests`（表 ⇄ 実装）は緑のままなので、**穴は「赤くならないこと」として現れる。**
    // これは [[IADR-0166]] 自身が名付けた「緑だが検査されていない」そのものである。
    //
    // **本テストは、同じ穴が新しい行の追加で再発することを塞ぐ。** `PlanRiskDefaults` へ新しい出典の行を
    // 足したら、その出典の節を本表へ登録するまで赤くなる。
    [Fact]
    public void ダイジェスト表は計画確定値の出典を網羅している()
    {
        // 出典トークン（`ADR-xxxx` / `FR-xx` / `UC-xx` / `05_trading-assumptions §x`）→ 本表の Citation の前方一致。
        // **本表に無い出典を「覚えておく」場所ではなく、対応が取れることを機械的に主張する場所である。**
        (string Pattern, string CitationPrefix)[] coverage =
        [
            (@"05_trading-assumptions §1", "05_trading-assumptions §1"),
            (@"05_trading-assumptions §3", "05_trading-assumptions §3"),
            (@"05_trading-assumptions §4", "05_trading-assumptions §4"),
            (@"05_trading-assumptions §5", "05_trading-assumptions §5"),
            (@"05_trading-assumptions §6", "05_trading-assumptions §6"),
            (@"ADR-0008", "ADR-0008 決定"),
            (@"ADR-0016", "ADR-0016 決定"),
            (@"ADR-0018", "ADR-0018 決定"),
            (@"ADR-0021", "ADR-0021 決定"),
            (@"ADR-0022", "ADR-0022 決定"),
            (@"FR-\d+", "02_requirements §機能要求"),
            (@"UC-06", "UC-06"),
        ];

        // **免除は理由つきで明示する。** 出典として値を与えているのではなく、状態の注記として現れる ID。
        (string Token, string Reason)[] exemptions =
        [
            ("ADR-0026", "値の出典ではない。`ShortSell.BorrowRateCapAnnual` の注記が「単位が PoC 項目 9 待ちである」と述べているだけであり、20% という値は ADR-0016 決定3 由来である"),
        ];

        // 対応先の Citation が実在すること（対応表の側が腐るのを防ぐ）。
        foreach (var (pattern, citationPrefix) in coverage)
        {
            PlanSourceDigests.All
                .Should().Contain(
                    d => d.Citation.StartsWith(citationPrefix, StringComparison.Ordinal),
                    "出典パターン `{0}` の対応先「{1}」がダイジェスト表に無い", pattern, citationPrefix);
        }

        // `PlanRiskDefaults` の全行の出典（Citation）に現れる計画 ID が、いずれかのパターンで拾えること。
        var tokenPattern = new Regex(@"ADR-\d{4}|FR-\d+|UC-\d+|05_trading-assumptions §[\d.]+");
        var uncovered = new List<string>();

        foreach (var plan in PlanRiskDefaults.All)
        {
            foreach (var token in tokenPattern.Matches(plan.Citation).Select(m => m.Value).Distinct())
            {
                if (exemptions.Any(e => token.StartsWith(e.Token, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!coverage.Any(c => Regex.IsMatch(token, $"^(?:{c.Pattern})$")))
                {
                    uncovered.Add($"{plan.Key} の出典「{token}」");
                }
            }
        }

        uncovered.Should().BeEmpty(
            "**計画確定値の出典がダイジェスト表に掛かっていない。** その行は「計画側が変わったのに赤くならない」"
                + "状態にある（#459 で `Stage.Values` / `Guard.PreventSameDayReentry` / `ShortSell.Limits` に"
                + "実在した）。(1) 出典の節を PlanSourceDigests へ登録する か "
                + "(2) 値の出典ではないなら exemptions へ**理由つきで**足すこと。掛かっていない出典: {0}",
            string.Join(" / ", uncovered));
    }

    // 正規化の否定形（IADR-0166 決定3）: 行末空白・改行コードは吸収するが、**内容の差は吸収しない**。
    // 内容まで吸収する正規化を足すと検知漏れになるため、両方向を固定する。
    [Fact]
    public void 正規化は改行と行末空白だけを吸収し内容の差は吸収しない()
    {
        var a = PlanSourceReader.Normalize("### 1.\r\n値は 25% である   \r\n\r\n");
        var b = PlanSourceReader.Normalize("### 1.\n値は 25% である\n");
        a.Should().Be(b, "改行コードと行末空白は計画の内容ではない");

        var different = PlanSourceReader.Normalize("### 1.\n値は 30% である\n");
        PlanSourceReader.Sha256Hex(a).Should().NotBe(
            PlanSourceReader.Sha256Hex(different),
            "**内容の差は必ずハッシュに現れなければならない**（吸収したら検知漏れになる）");
    }

    // 節の切り出しの境界: 同位以上の見出しで打ち切り、下位見出しは節に含める。
    [Fact]
    public void 節は同位以上の見出しで打ち切り下位見出しは含める()
    {
        const string Md = """
            ## 前提条件の項目定義

            ### 1. 口座
            本文 A

            #### 1.1 細目
            本文 B

            ### 2. 手数料
            本文 C

            ## 変更履歴
            履歴
            """;

        var section = PlanSourceReader.TryExtractSection(Md, "### 1.");

        section.Should().NotBeNull();
        section.Should().Contain("本文 A").And.Contain("#### 1.1 細目").And.Contain("本文 B");
        section.Should().NotContain("本文 C", "同位（###）の次の見出しで打ち切る");
        section.Should().NotContain("履歴", "上位（##）の見出しでも打ち切る＝変更履歴は含めない（IADR-0166 決定2）");

        PlanSourceReader.TryExtractSection(Md, "### 9.").Should().BeNull("無い節は null（テスト側が理由を出す）");
    }

    // **前方一致の曖昧性**（#444 のレビュー指摘）: `"### 6."` は `"### 6.1 …"` の前方一致でもある。
    // 直後が区切り（空白）か行末であることまで要求しないと、**ファイル内の見出し出現順に依存した
    // 暗黙の前提**になる。出現順を逆にしても正しい節を選ぶことを固定する。
    [Fact]
    public void 見出しの前方一致は直後が区切りであることまで要求する()
    {
        // `### 6.1` を**先に**置く（実ファイルとは逆順）。前方一致だけだと `### 6.` がこちらに当たる。
        const string Md = """
            ### 6.1 LLM 費用の対象範囲
            細目の本文

            ### 6. 運用費用の上限
            本体の本文
            """;

        PlanSourceReader.TryExtractSection(Md, "### 6.")
            .Should().Contain("本体の本文").And.NotContain("細目の本文",
                "`### 6.` は `### 6.1` に一致してはならない（直後が区切りでない）");

        PlanSourceReader.TryExtractSection(Md, "### 6.1")
            .Should().Contain("細目の本文").And.NotContain("本体の本文");
    }
}
