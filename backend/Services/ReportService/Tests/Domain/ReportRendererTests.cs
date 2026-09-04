using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06/16, 04_report-templates, IADR-0032: 報告書テンプレートへの組み立て（純関数・日報/週報/月報）を検証する。
// 数値は PnlSummary（コード集計値）から埋まり、散文・方針が挿入されることを確認する。
public class ReportRendererTests
{
    private static PnlSummary Pnl() => new(
        RealizedPnlGross: 2_000m, TotalCost: 100m, TaxWithheld: 380m, RealizedPnlNet: 1_520m,
        UnrealizedPnl: -300m, TradeCount: 3, RealizingTradeCount: 4, WinningTradeCount: 3);

    private static ReportView View(ReportKind kind, string periodLabel, string narrative = "散文テスト", string policy = "方針テスト") => new()
    {
        Kind = kind,
        PeriodKey = $"{kind.ToString().ToLowerInvariant()}-{periodLabel}",
        PeriodLabel = periodLabel,
        Markets = ["JP", "US"],
        AssumptionsVersion = 2,
        BasedOn = "weekly-2026-W28",
        Pnl = Pnl(),
        BuyCount = 2,
        SellCount = 1,
        PolicySummary = policy,
        Narrative = narrative,
    };

    // --- 日報（#86 からの回帰維持） ---

    [Fact]
    public void 日報_フロントマターと当日サマリの数値を定義どおり生成する()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, "2026-07-10"));

        md.Should().StartWith("---\n");
        md.Should().Contain("report_type: daily");
        md.Should().Contain("period: 2026-07-10");
        md.Should().Contain("assumptions_version: v2");
        md.Should().Contain("markets: [JP, US]");
        md.Should().Contain("# 日報 2026-07-10");
        md.Should().Contain("## 1. 当日サマリ");
        md.Should().Contain("実現損益（税引後・費用込み） | +1,520.00 USD");
        md.Should().Contain("評価損益（税引前・参考） | -300.00 USD");
        md.Should().Contain("取引回数（買/売/決済） | 2 / 1 / 4");
        md.Should().Contain("源泉徴収税額 | +380.00 USD");
        // #563, IADR-0269, ADR-0030 決定1・決定5: 日報の §2 / §3 は取引履歴・ポジション一覧が占め、
        // 散文は §5（市況・特記事項）・方針は計画どおり §7 へ置く（§6 振り返りは未実装の見出しとして出る）。
        md.Should().Contain("## 2. 取引履歴（全明細）");
        md.Should().Contain("## 3. ポジション一覧（当日終了時点）");
        md.Should().Contain("## 5. 市況・特記事項");
        md.Should().Contain("## 6. 振り返り（週次目標との照合）");
        md.Should().Contain("## 7. 翌営業日の方針");
        // 日報は勝率行を持たない。
        md.Should().NotContain("勝率");
    }

    // --- 週報 ---

    [Fact]
    public void 週報_週間サマリと勝率と翌週方針を生成する()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Weekly, "2026-W28"));

        md.Should().Contain("report_type: weekly");
        md.Should().Contain("period: 2026-W28");
        md.Should().Contain("# 週報 2026-W28");
        md.Should().Contain("## 1. 週間サマリ");
        md.Should().Contain("週間実現損益（税引後・費用込み） | +1,520.00 USD");
        md.Should().Contain("勝率（勝ち取引/全決済取引） | 75%（3/4）"); // 04_report-templates の <n%（n/n）> 形式
        md.Should().Contain("週次目標に対する達成 | （データ連携後）"); // 目標データ連携は後続
        md.Should().Contain("## 4. 振り返りと評価");
        md.Should().Contain("## 6. 翌週の方針");
    }

    // --- 月報 ---

    [Fact]
    public void 月報_月間サマリと翌月方針を生成する()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly, "2026-07"));

        md.Should().Contain("report_type: monthly");
        md.Should().Contain("period: 2026-07");
        md.Should().Contain("# 月報 2026-07");
        md.Should().Contain("## 1. 月間サマリ");
        md.Should().Contain("月間実現損益（税引後・費用込み） | +1,520.00 USD");
        // 04_report-templates の月間サマリ行構成。データ依存行は後続連携で埋める（形式は保つ）。
        md.Should().Contain("総資産（月初 → 月末） | （データ連携後）");
        md.Should().Contain("年初来累計損益 | （データ連携後）");
        md.Should().Contain("## 8. 翌月の方針・投資方針");
    }

    [Fact]
    public void 決済ゼロの勝率はハイフン表記()
    {
        var view = View(ReportKind.Weekly, "2026-W28") with
        {
            Pnl = new PnlSummary(0m, 0m, 0m, 0m, 0m, 0, 0, 0),
        };

        var md = ReportRenderer.RenderMarkdown(view);

        md.Should().Contain("勝率（勝ち取引/全決済取引） | -（0/0）");
    }

    [Fact]
    public void 散文と方針が空でも見出しと既定文で生成される()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, "2026-07-10", narrative: "", policy: ""));

        md.Should().Contain("（散文ドラフトなし）");
        md.Should().Contain("（方針未設定）");
    }

    // --- 維持率割れによる自動縮小（#330・記録先 3/4: 日報・月報） ---

    private static MaintenanceMarginReductionExecuted Reduction(
        DateTimeOffset executedAt, decimal? ratioAfter = 0.4504m) => new(
        Guid.NewGuid(), RatioBefore: 0.40m, Threshold: 0.40m, RecoveryTarget: 0.45m, RatioAfter: ratioAfter,
        [new MaintenanceMarginReductionItem(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, 112, 100m, 3_360m)],
        executedAt);

    // T-10-201: 日報は**発動の有無・決済した建玉・決済前後の維持率**を表 7 列で載せる
    //（04_report-templates 日報 §4・FR-10・UC-06）。
    [Fact]
    public void 日報は維持率割れの自動縮小を7列の表で記載する()
    {
        var view = View(ReportKind.Daily, "2026-08-04") with
        {
            MarginReductions =
            [
                Reduction(new DateTimeOffset(2026, 8, 4, 14, 30, 0, TimeSpan.Zero)),
                Reduction(new DateTimeOffset(2026, 8, 4, 12, 5, 0, TimeSpan.Zero)),
            ],
        };

        var md = ReportRenderer.RenderMarkdown(view);

        md.Should().Contain("### 維持率割れによる自動縮小（当日）");
        md.Should().Contain("**発動の有無**: あり — 2 回");
        md.Should().Contain("| # | 時刻 | 決済前の維持率 | 閾値 | 回復目標（閾値+5pt） | 決済した建玉（銘柄・方向・数量・必要証拠金） | 決済後の維持率 |");
        md.Should().Contain("| 1 | 12:05 |", "発動は時刻の昇順で並べる");
        md.Should().Contain("40.0%").And.Contain("45.0%").And.Contain("45.0%");
        md.Should().Contain("AAPL / ショート / 112 株 / 3,360.00 USD");
    }

    // T-10-202: 発動が無い日も「なし」と**明記する**（計画: 空欄と「なし」を区別する）。
    [Fact]
    public void 日報は発動が無い日もなしと明記する()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(ReportKind.Daily, "2026-08-04") with { MarginReductions = [] });

        md.Should().Contain("**発動の有無**: なし");
    }

    // T-10-203（否定形）: **照会できなかったものを「なし」と書かない**（発動を隠したのと同じ結果になるため）。
    [Fact]
    public void 記録を照会できないときはなしと書かず要確認と記す()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(ReportKind.Daily, "2026-08-04") with { MarginReductions = null });

        md.Should().Contain("記録を照会できませんでした（要確認）");
        md.Should().NotContain("**発動の有無**: なし");
    }

    // T-10-204: 月報は**当月の発動回数**のみを記載する（明細は日報に委ねる。04_report-templates 月報 §6）。
    // 発動が無い月も「0 件」と明記する。
    [Fact]
    public void 月報は当月の発動回数を記載し発動が無い月も0件と明記する()
    {
        var withEvents = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly, "2026-08") with
        {
            MarginReductions = [Reduction(new DateTimeOffset(2026, 8, 4, 14, 30, 0, TimeSpan.Zero))],
        });
        var empty = ReportRenderer.RenderMarkdown(
            View(ReportKind.Monthly, "2026-08") with { MarginReductions = [] });

        withEvents.Should().Contain("### 維持率割れによる自動縮小（当月）").And.Contain("**発動回数: 1 件**");
        withEvents.Should().NotContain("| # | 時刻 |", "個々の内容は該当日報を参照する（月報は回数のみ）");
        empty.Should().Contain("**発動回数: 0 件**");
    }

    // T-10-205: 全量決済で建玉が無くなった場合、決済後の維持率は「0%」ではなく「建玉なし」と記す。
    [Fact]
    public void 全量決済後の維持率は建玉なしと記す()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, "2026-08-04") with
        {
            MarginReductions = [Reduction(new DateTimeOffset(2026, 8, 4, 14, 30, 0, TimeSpan.Zero), ratioAfter: null)],
        });

        md.Should().Contain("建玉なし");
    }

    // 週報は計画が記載を求めていないため節を作らない（求められていない節を勝手に増やさない）。
    [Fact]
    public void 週報には自動縮小の節を作らない()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(ReportKind.Weekly, "2026-W32") with { MarginReductions = [] });

        md.Should().NotContain("維持率割れによる自動縮小");
    }

    // --- 強制買戻し（推定）（#419 / ADR-0016 決定4 の 2026-08-06 改訂・決定15 の 2026-08-06 追記） ---

    private static BuyInInferred Inference(DateTimeOffset inferredAt) => new(
        Guid.NewGuid(), "GME", Market.UnitedStates,
        LedgerShortQuantity: 100, BrokerShortQuantity: 0, InFlightCloseQuantity: 0,
        UnexplainedQuantity: 100, NewlyInferredQuantity: 100,
        CoveringFills: [], BanUntil: new DateOnly(2026, 9, 6),
        ObservedAt: inferredAt, InferredAt: inferredAt);

    // T-10-235: FR-10, FR-06, ADR-0016 決定4/決定15 —— 日報は当日の**発生有無**を載せ、
    // **必ず「推定」と明示する**（イベント検知ではないため、確定した事実として扱わせない）。
    [Fact]
    public void 日報は強制買戻しを推定と明示して発生有無を記載する()
    {
        var view = View(ReportKind.Daily, "2026-08-07") with
        {
            BuyInInferences = [Inference(new DateTimeOffset(2026, 8, 7, 14, 30, 0, TimeSpan.Zero))],
        };

        var md = ReportRenderer.RenderMarkdown(view);

        md.Should().Contain("### 強制買戻し（推定）（当日）");
        md.Should().Contain("**発生の有無（推定）**: **あり — 強制買戻しと推定 1 件**");
        md.Should().Contain("推定", "決定4 の改訂は『強制買戻しと推定』と明示することを求めている");
        md.Should().Contain("確定した事実として扱わないでください");
        md.Should().Contain("GME/UnitedStates").And.Contain("2026-09-06");
    }

    // T-10-236: 推定が無い日は「なし（当日の推定は 0 件）」と明記する（空欄と区別する）。
    [Fact]
    public void 日報は推定が無い日もなしと明記する()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(ReportKind.Daily, "2026-08-07") with { BuyInInferences = [] });

        md.Should().Contain("**発生の有無（推定）**: なし");
    }

    // T-10-237（**否定形・決定15**）: **供給が無い間は 0 件・「なし」と書かない。**
    // 「強制買戻しは起きていない」と読めるためである（決定15 が名指しで禁じた向き）。
    [Fact]
    public void 供給が無いときは0件ともなしとも書かず未供給と明記する()
    {
        var daily = ReportRenderer.RenderMarkdown(
            View(ReportKind.Daily, "2026-08-07") with { BuyInInferences = null });
        var monthly = ReportRenderer.RenderMarkdown(
            View(ReportKind.Monthly, "2026-08") with { BuyInInferences = null });

        daily.Should().Contain("記録を照会できませんでした（供給元がありません）");
        daily.Should().Contain("**0 件ではありません。**");
        daily.Should().NotContain("**発生の有無（推定）**: なし");
        monthly.Should().Contain("記録を照会できませんでした（供給元がありません）");
        monthly.Should().NotContain("強制買戻し（推定）0 件");
    }

    // T-10-238（**決定15**）: 月報は当月の**発生回数**を「強制買戻し（推定）N 件」と明示する。
    // **集計元は推定の件数**であり、`BuyInBanned`（拒否理由）の件数ではない——
    // 1 回の強制買戻しに対し禁止期間 30 日のあいだ拒否は何度でも起こり得るため、実際より大きな数字が載る。
    [Fact]
    public void 月報は強制買戻しの発生回数を推定と明示して記載する()
    {
        var withEvents = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly, "2026-08") with
        {
            BuyInInferences = [Inference(new DateTimeOffset(2026, 8, 7, 14, 30, 0, TimeSpan.Zero))],
        });
        var empty = ReportRenderer.RenderMarkdown(
            View(ReportKind.Monthly, "2026-08") with { BuyInInferences = [] });

        withEvents.Should().Contain("### 強制買戻し（推定）（当月）");
        withEvents.Should().Contain("**強制買戻し（推定）1 件**");
        withEvents.Should().Contain("**推定**であり確定した事実ではありません");
        withEvents.Should().NotContain("| # | 銘柄 |", "個々の内容は該当日報を参照する（月報は回数のみ）");
        empty.Should().Contain("**強制買戻し（推定）0 件**", "供給がある上での 0 件は事実として書いてよい");
    }

    // T-10-239（**構造・決定15**）: 報告書の描画入力（ReportView）は**拒否理由に関する情報を一切持たない**。
    // これにより「`BuyInBanned` の拒否件数を発生回数として報告する」取り違えが構造的に起こり得ない
    //（決定15 が明示的に禁じた誤り）。入口が生えたら本テストが赤くなる。
    [Fact]
    public void 報告書の入力は拒否理由由来の件数を持たない()
    {
        var propertyTypes = typeof(ReportView).GetProperties()
            .Select(p => p.PropertyType.FullName ?? p.PropertyType.Name)
            .ToList();

        propertyTypes.Should().NotContain(
            t => t.Contains("RejectionReason", StringComparison.Ordinal),
            "強制買戻しの発生回数の集計元は推定の件数である（BuyInBanned の拒否件数ではない・ADR-0016 決定15）");

        typeof(ReportView).GetProperties().Select(p => p.Name).Should().NotContain(
            n => n.Contains("Rejection", StringComparison.OrdinalIgnoreCase)
                || n.Contains("BuyInBanned", StringComparison.OrdinalIgnoreCase));
    }

    // 週報は計画が記載を求めていないため節を作らない。
    [Fact]
    public void 週報には強制買戻しの節を作らない()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(ReportKind.Weekly, "2026-W32") with { BuyInInferences = [] });

        md.Should().NotContain("強制買戻し");
    }

    // --- ADR-0030（節番号・節順は計画を正とする）/ IADR-0291 ---

    // FR-06, FR-07, FR-16, ADR-0030 決定1・決定2: 週報の 6 節が計画の番号・並び順どおりに昇順で出る。
    // **未実装の §2・§3・§5 があっても番号を詰めない。**
    [Fact]
    public void 週報の節は計画の番号と並び順で昇順に出る()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Weekly, "2026-W28"));

        AssertAscendingSections(md,
            "## 1. 週間サマリ",
            "## 2. 日別推移",
            "## 3. ハイライト取引",
            "## 4. 振り返りと評価",
            "## 5. リスク・費用レビュー",
            "## 6. 翌週の方針");
    }

    // FR-06, FR-07, FR-16, ADR-0030 決定1・決定2・決定4: 月報の 8 節が計画の番号・並び順どおりに昇順で出る。
    // **三者比較は §2・§3 が未実装でも §5 のまま**（決定2）であり、稼働率分布は §6 の子節 §6.2 である（決定4）。
    [Fact]
    public void 月報の節は計画の番号と並び順で昇順に出る()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly, "2026-08"));

        AssertAscendingSections(md,
            "## 1. 月間サマリ",
            "## 2. 週別・市場別の内訳",
            "## 3. 税金レビュー",
            "## 4. 総括と評価",
            "## 5. バックテスト / SIMULATE / 実弾の三者比較",
            "## 6. リスク統制と前提条件の見直し",
            "### 6.1 空売りの記録（当月）",
            "### 6.2 当月の OpenD 稼働率分布",
            "## 7. 当月の LLM 利用実績",
            "## 8. 翌月の方針・投資方針");
    }

    // 🔴 ADR-0030 決定4 の**否定形**: 稼働率分布を親節へ昇格させない。
    // 親節から切り離すと、稼働率の数字を見て前提を見直すという読みの筋が切れる。
    [Fact]
    public void 稼働率分布を独立した親節にしない()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly, "2026-08"));

        md.Should().NotContain("## 5. 当月の OpenD 稼働率分布");
        md.Should().NotContain("\n## 当月の OpenD 稼働率分布");
    }

    // 🔴 ADR-0030 決定3: 未実装の節は**見出しごと**出し、本文に未実装である旨を記す。
    // 見出しを落として番号だけ飛ばす形は採らない（番号の飛びは計画書を読んでいない利用者に何も伝えない）。
    //
    // 🔴 #615, IADR-0305, IADR-0306: **母集合は実装が進むたびに縮む。** 週報 §2・§3（スライス a）・§5（b）・
    // 月報 §2（c）は実体化したので行から外した。**外し忘れても赤くならなかった**——本文の切り出しが
    // 「見出し以降の全文」だったため、§2・§3 の行は**まだ未実装だった §5 の文言を拾って緑になっていた**（実測）。
    // 下の切り出しは**次の `## ` 見出しまで**に閉じ、同じ形の空振りを起こさないようにしている。
    // **残る 2 行は #615 の対象外**（月報 §3＝前提整備不足／日報 §6＝週次目標の参照値が無い）。
    [Theory]
    [InlineData(ReportKind.Monthly, "2026-08", "## 3. 税金レビュー", "年初来累積の権威源")]
    [InlineData(ReportKind.Daily, "2026-07-10", "## 6. 振り返り（週次目標との照合）", "週次目標の参照値")]
    public void 未実装の節は見出しごと出して未実装であることを本文に書く(
        ReportKind kind, string periodLabel, string heading, string reasonFragment)
    {
        var md = ReportRenderer.RenderMarkdown(View(kind, periodLabel));

        md.Should().Contain(heading);

        // 🔴 **当該節の中だけを見る。** 見出し以降の全文を見ると、後続の未実装節の文言で緑になる。
        var start = md.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var next = md.IndexOf("\n## ", start, StringComparison.Ordinal);
        var body = next < 0 ? md[start..] : md[start..next];
        body.Should().Contain("**本節は未実装です**");
        body.Should().Contain(reasonFragment);
        // 🔴 **「該当なし」「0 件」と読ませない。** 未供給と 0 を区別する本サービスの規律を節の不在にも当てる。
        body.Should().Contain("**「該当なし」「0 件」ではありません。**");
    }

    // ADR-0030 決定5 の**否定形**: 日報の市況・特記事項と振り返りを 1 節へ統合しない。
    [Fact]
    public void 日報の市況と振り返りを統合しない()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, "2026-07-10"));

        md.Should().NotContain("市況・振り返り");
    }

    // 節が昇順に出ることを主張する。🔴 **-1 を許さない**（IndexOf は見つからないと -1 を返すため、
    // 実在を先に主張しないと「節が消えた」状態で順序の検査が通ってしまう）。
    private static void AssertAscendingSections(string markdown, params string[] headings)
    {
        var indexes = new List<int>();
        foreach (var heading in headings)
        {
            var at = markdown.IndexOf(heading, StringComparison.Ordinal);
            at.Should().BeGreaterThanOrEqualTo(0, $"{heading} が本文に無い");
            indexes.Add(at);
        }

        indexes.Should().BeInAscendingOrder();
    }
}
