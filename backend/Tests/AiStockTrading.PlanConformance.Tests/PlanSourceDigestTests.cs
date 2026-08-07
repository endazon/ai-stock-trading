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
    private readonly ITestOutputHelper _output;

    public PlanSourceDigestTests(ITestOutputHelper output) => _output = output;

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
            _output.WriteLine(
                "notice: planning submodule が未 populate のため計画書ダイジェスト検査を skip した。"
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
}
