namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// 計画書で確定した既定値テーブル（FR-10, FR-17, FR-19, FR-20）。
/// 出典は 06_technical/05_trading-assumptions の §5（リスク統制・取引ガード）・§1（税制）・§3（為替・通貨）・
/// §4（計算方針）・§6（運用費用上限）と ADR-0008 / ADR-0016 / ADR-0018 / ADR-0022。
/// <para>
/// 本テーブルが**計画側の単一情報源**である。実装値をここへ写してはならない（実装のスナップショットを
/// 固定するだけになり、計画との乖離を永久に検知できなくなる — #306 の再発）。
/// </para>
/// <para>
/// 収録するのは**実装から機械的に抽出できる値**（定数・設定フィールド・enum・型の有無）に限る。
/// 「厳しい方が効く」「kill switch 中でも手仕舞いは止めない」のような**振る舞いの規則**は値ではないため
/// 本表では扱わず、テスト仕様書（<c>docs/tests/</c>）の 3 点セット（境界値・プロパティベース・否定形）で
/// 担当 issue が検証する（IADR-0127 決定4）。
/// </para>
/// </summary>
public static class PlanRiskDefaults
{
    private const string Assumptions1 = "05_trading-assumptions §1";
    private const string Assumptions3 = "05_trading-assumptions §3";
    private const string Assumptions4 = "05_trading-assumptions §4";
    private const string Assumptions5 = "05_trading-assumptions §5";
    private const string Assumptions6 = "05_trading-assumptions §6";

    public static IReadOnlyList<PlanDefault> All { get; } =
    [
        // --- 資金と金額系の統制上限 3 値（equity 比で保持し、固定額では持たない） ---
        new("Capital.Initial", "USD 3000", Assumptions5),
        new("RiskLimits.MaxOrderAmount", "equity ratio 0.25", Assumptions5),
        new("RiskLimits.MaxDailyOrderAmount", "equity ratio 1.50 per day", Assumptions5),
        new("RiskLimits.MaxOpenPositions", "3", $"{Assumptions5} / ADR-0016 決定9"),

        // --- 損失・サイジング系（ADR-0018 で確定単一値へ同期） ---
        new("RiskLimits.DailyLossLimitRatio", "equity ratio 0.02", $"{Assumptions5} / ADR-0018"),
        new("RiskLimits.PerTradeRiskRatio", "equity ratio 0.01", $"{Assumptions5} / ADR-0018"),
        new("RiskLimits.MaxDrawdownRatio", "equity ratio 0.10", $"{Assumptions5} / ADR-0018"),
        new("RiskLimits.LosingStreakThreshold", "5", $"{Assumptions5} / ADR-0018"),
        new("RiskLimits.LosingStreakSizeFactor", "0.5", $"{Assumptions5} / ADR-0018"),

        // --- 取引ガード（FR-19。商品種別は 3 値で独立制御） ---
        new("ProductType.Values", "Cash, MarginLong, ShortSell", $"{Assumptions5} / ADR-0016 決定8"),
        new("Market.Values", "Japan, UnitedStates", Assumptions5),
        // 本行は「設定の既定値（いずれも現物のみ有効）」であり、段階別の可否
        // （Stage 1＝3 種／Stage 2＝現物のみ／Stage 3＝条件付き全種）は**別の規則**である。
        // 段階×商品種別が結線される時点（#332 / #333 / #334）で、既定値と段階別強制を
        // 混同しないこと。段階別の強制は値ではなく振る舞いのため、3 点セットのテストで検証する。
        new("Guard.EnabledProductTypes", "Cash", Assumptions5),
        new("Guard.EnabledMarkets", "Japan, UnitedStates", Assumptions5),
        // 比較は序数順に正規化するため、登録順ではなく昇順で記す。
        new("Guard.BannedSymbols", "6457/Japan, 6502/Japan, 6902/Japan", Assumptions5),
        // 適用範囲は 2026-08-06 に条件付きへ改められた（ADR-0021 決定4-1・環流 project-planning#220）。
        // 旧注記は「日本株現物」のみとしており、**現金口座の米国株にも適用される**ことが落ちていた。
        // 注記は飾りではなく、読み手が「この値は本当に計画どおりか」を確かめる唯一の手掛かりである。
        // **実装側の適用範囲が計画に追随しているかは #380 の担当**であり、ここでは注記のみを正す（#445）。
        new(
            "Guard.PreventSameDayReentry",
            "True",
            $"{Assumptions5}（適用範囲は `現物 && （日本市場 ‖ 現金口座）`。FR-19 / ADR-0021 決定4-1）"),
        new("Guard.ProhibitManipulativeOrderPatterns", "True", Assumptions5),

        // --- 空売り専用統制（ADR-0016）。値の集合は専用型 ShortSellingLimits が保持する想定 ---
        // **本行は「型の形」だけを見る。値は下の 7 行が見る**（#445・IADR-0172 決定1）。
        // 役割が違うため併存させる —— 本行はメンバの**増減**を、下の 7 行は**値の逸脱**を捕まえる。
        new(
            "ShortSell.Limits",
            "type ShortSellingLimits with members: BorrowRateCapAnnual, BuyInBanDurationDays, "
                + "ExposureRatioCap, MaintenanceMarginThreshold, MaintenanceRecoveryTargetOffset, "
                + "PerSymbolCapRatio, PriceFloorUsd",
            "ADR-0016 決定2,3,4,7,9 / UC-06"),

        // --- 空売り統制の**値**（#445 で追加）。
        //
        //     【本作業が見つけた穴】 2026-08-07 まで、上の `ShortSell.Limits` 行は
        //     `DescribeTypeWithMembers`（= プロパティ名の一覧）で抽出されており、**値は一度も
        //     比較されていなかった**。実測: `PerSymbolCapRatio` を 0.10 → 0.50（equity の 5 割）へ
        //     変えても PlanConformance / PlanSourceDigest はいずれも緑のままであった。
        //     「間違っていた」のではなく**「間違っても気付けなかった」**——[[IADR-0166]] が名付けた
        //     「緑だが検査されていない」そのものである。
        //
        //     値はいずれも単位つきで正規化する（無次元の数値どうしの取り違えを防ぐ。Fx.* と同じ作法）。 ---
        new("ShortSell.PerSymbolCapRatio", "equity ratio 0.10", $"{Assumptions5} / ADR-0016 決定2(a)"),
        // ⚠️ **値は 20% のままだが、統制としての位置づけは 2026-08-06 に変わっている**（#459 で注記を追随）。
        // **一次ゲートは `IsShortPermit`（借株可否）へ移った** —— 前提とした「借りにくい銘柄ほど借株料が
        // 高い」は実測で成り立たず（借株在庫が 20 倍以上開いても `ShortFeeRate` は 1.5 のまま）、本上限は
        // **「発火しない既知の統制」として残置**されている（拒否理由は `BorrowUnavailable`）。
        //
        // 🔴 **さらに `ShortFeeRate` の単位が未確定である**（ADR-0026 の PoC 項目 9・期限 2026-08-31）。
        // `1.5` を年率 1.5% と読めば本上限は発火せず、比率（150%）と読めば**全銘柄で発火し空売りが
        // 1 件も通らない**。**同じ実測値が、単位の読み方ひとつで統制の帰結を反転させる。**
        // 注記を「危険度を弾く一次の閾値」と読めるまま置くと、**値が計画どおりであることを確認しても
        // 統制の実態を誤解する**——注記は飾りではなく、値の意味を確かめる唯一の手掛かりである（#445 結論4）。
        new(
            "ShortSell.BorrowRateCapAnnual",
            "annual rate 0.20",
            $"{Assumptions5} / ADR-0016 決定3（2026-08-06 以降は残置の統制。単位は ADR-0026 PoC 項目 9 待ち）"),
        // 自前の閾値である。実効値は規制要求（max($5.00 ÷ 株価, 30%)）との**厳しい方**であり、
        // その選択は振る舞いの規則のため 3 点セットのテストが担当する（IADR-0127 決定4）。
        new("ShortSell.MaintenanceMarginThreshold", "ratio 0.40", $"{Assumptions5} / ADR-0016 決定7"),
        new(
            "ShortSell.MaintenanceRecoveryTargetOffset",
            "ratio 0.05",
            $"{Assumptions5}（適用される閾値 + 5 ポイント。利用者裁定 2026-08-02）"),
        // **下の Regulatory.FixedMaintenancePerShare と同額（$5.00）だが別の概念である。**
        // 本行は「空売り対象の株価下限」、あちらは「規制の固定維持証拠金」。1 行にまとめると、
        // 片方が変わったときにもう片方の検査が黙って消える（IADR-0172 決定2）。
        new("ShortSell.PriceFloorUsd", "USD 5.00", $"{Assumptions5} / ADR-0016 決定7"),
        new("ShortSell.ExposureRatioCap", "gross position ratio 0.50", $"{Assumptions5} / ADR-0016 決定9"),
        new("ShortSell.BuyInBanDurationDays", "30 days", "ADR-0016 決定4"),

        // --- 規制側の維持率（FINRA Rule 4210(c)）。自前の閾値とは**別の情報源**であり、
        //     「厳しい方を採る」の一方の入力である。自前だけを検査すると、規制側を緩めても気付けない。 ---
        new("Regulatory.MaintenanceMarginFloor", "ratio 0.30", $"{Assumptions5}（規制側の下限。ADR-0016 決定7）"),
        new(
            "Regulatory.FixedMaintenancePerShare",
            "USD 5.00",
            $"{Assumptions5}（規制側の実効維持率 max($5.00 ÷ 株価, 30%) の固定額）"),
        // **2026-08-07 確定（利用者裁定 質問票 第 13 回 Q4-3）。確定から #445 まで一度も検査されていなかった。**
        // 自前の 40% と厳しい方を採るため実効的には常に 40% が効くが、それでも持つ理由は計画に明記が
        // ある —— 空売り側の式で代用している状態を解消し、将来 40% を見直す際に正しい規制下限を残すため。
        //
        // ⚠️ **計画の「実効値は変わらない」は計画側から見た話であり、実装にとっては緩む向きの是正である**
        // （#459 で注記を追随）。実装は 4210(c)(3)（空売り側）の式を信用買いへ代用していたため、
        // $6.00 の信用買いの適用閾値は 83.3% → 40%、$10.00 は 50% → 40% になった。
        // **2026-08-07 の裁定（質問票 第 14 回 Q4）がこの是正を追認している** —— 4210(c)(1) と (c)(3) は
        // 別の建玉に対する別の規制であり、片方の式を他方へ流用することに根拠が無いためである。
        new(
            "Regulatory.MarginLongMaintenanceMargin",
            "ratio 0.25",
            $"{Assumptions5}（信用買いの規制維持率。FINRA Rule 4210(c)(1)。2026-08-07 確定・同日に是正を追認）"),
        // 2026-08-04 改訂: 7 種 → 9 種（`StopOrderRequired` の追認・`BuyInBanned` の新設）。
        // `BuyInBanned` を `BorrowUnavailable` へ写像してはならない（決定10 の 2026-08-04 追記）。
        new(
            "RejectionReason.ShortSellReasons",
            "BorrowCostExceeded, BorrowUnavailable, BuyInBanned, DividendRecordDateNear, "
                + "MaintenanceMarginBreach, ShortExposureExceeded, ShortPriceFloorBreach, "
                + "ShortSellDisabled, StopOrderRequired",
            "ADR-0016 決定10（9 種。いずれもクラス A。2026-08-04 に 7 種から改訂）"),

        // --- 段階ゲートと発注先（FR-20。段階と発注先は独立した 2 軸） ---
        new("BrokerProvider.Values", "InternalPaper, MoomooReal, MoomooSimulate", $"{Assumptions5} / FR-20"),
        new("Stage.Values", "Stage0Verification, Stage1Simulate, Stage2MinimalLive, Stage3ScaledLive", "FR-20"),
        new("Stage.Initial", "Stage0Verification", Assumptions5),
        new("Stage.Stage1BrokerProvider", "MoomooSimulate", $"{Assumptions5} / FR-20"),
        new("Stage.Stage2OrderableCapRatio", "total funds ratio 0.30", Assumptions5),
        new("Stage.WithdrawalDrawdownMultiple", "1.5", "ADR-0008"),
        // 空売りの実弾解禁は Stage 3 **かつ** 自己資金 $5,000 以上の 2 条件である。
        // 段階のほうは Stage.Values / StageProductPolicy が持つため、ここでは金額条件だけを収録する（#445）。
        new(
            "Stage.ShortSellLiveReleaseEquity",
            "USD 5000.00",
            $"{Assumptions5} / ADR-0016 決定8（1 銘柄あたり上限 equity の 10% が $500 以上であることと等価）"),
        // ADR-0018 決定2 が名指しするのは Stage 0 合格判定の許容値（Stage0GateCriteria）であり、
        // 運用の DD 停止ライン（RiskLimits.MaxDrawdownRatio）とは**別のフィールド**である。
        // 両者を取り違えると「たまたま計画と一致している別の値」を見て逸脱を見逃す。
        new("Stage0GateCriteria.MaxDrawdownTolerance", "ratio 0.10", "ADR-0018 決定2"),

        // --- 為替レート源と鮮度（FR-10, FR-17。ADR-0022。§3 の確定行 ＋ §5「為替レートの鮮度による縮退」） ---
        // 計画は 2026-08-04 に、実装が独自に持っていた FxOptions.DefaultMaxRateAgeDays = 14 を
        // 「警告 5 日（2026-08-07 改訂。起案時は 3 日）／絶対上限 30 日」へ置き換えた（ADR-0022 決定4・5）。値の**根拠が計画側へ移った**ため、
        // 情報源の都合（FRED の週次公表）から逆算された 14 は計画からの逸脱になる。
        //
        // 収録するのは値だけである。優先順位そのもの（第一＝日銀／フォールバック＝FRED）・切り替えの通知・
        // 縮退の段階（5 日超は続行して警告／30 日超で新規建て停止・手仕舞いは止めない）は**振る舞いの規則**で
        // あり、テスト仕様書の 3 点セットで担当 issue（#381）が検証する（IADR-0127 決定4）。
        // 日数は単位つきで正規化する。無次元の件数（RiskLimits.MaxOpenPositions の "3"）との取り違えを防ぐ。
        new("Fx.RateSourceProviders", "boj, fred", $"{Assumptions3} / ADR-0022 決定1・2"),
        new("Fx.StaleRateWarningDays", "5 days", $"{Assumptions3} / {Assumptions5} / ADR-0022 決定4"),
        new("Fx.MaxRateAgeDays", "30 days", $"{Assumptions3} / {Assumptions5} / ADR-0022 決定5"),

        // --- 全体前提条件の確定値（FR-17）。§5 と同じく利用者決定であり、実装は
        //     TradingAssumptionsDefaults.Create() が保持する。
        //
        //     **対象化は節単位ではなく行単位である**（IADR-0135 決定1）。旧コメントは「§2/§3 は『要確認』の
        //     ため確定値を持たず対象外」としていたが、これは 2 点で実態と食い違っていた——(a) §3 の
        //     「基準通貨（判定）＝USD」は 2026-07-31 の利用者決定であり当初から確定値であった、(b) 2026-08-04 の
        //     ADR-0022 が §3 へ為替レートの取得元と鮮度の確定値を追加した。節を単位に除外すると、節の中の
        //     1 行が確定するたびに前提が崩れる。
        //
        //     現時点の行単位の扱い:
        //       - §2（手数料・取引諸費用）: 全行が「要確認」（口座開設後に登録）のため**対象なし**。
        //       - §3（為替・通貨）: 上の Fx 3 行が対象。残りのうち「基準通貨（判定）＝USD」は
        //         Capital.Initial の通貨（TradingDefaults.EquityCurrency）として既に検知対象であり二重に持たない。
        //         「基準通貨（表示）＝JPY」「為替評価方法」は表示・計算の規則であり値ではない。 ---
        // **税率は市場を問わず単一である。** 計画は 2026-08-07 に「**米国株の譲渡益にも同率を用いる**」を
        // 明記した（#459 で注記を追随）。実装は `TradingAssumptions.CapitalGainsTaxRate` の 1 値を
        // 市場に依らず適用しており**もともと追随していたが、注記からはそう読めなかった** ——
        // §1 の下段「米国株配当の源泉徴収（米国 10% → 残額に国内 20.315%）」は**配当の行**であり
        // 譲渡益には及ばない。**隣接する行に別税率が並んでいることが取り違えの経路である。**
        new(
            "Assumptions.CapitalGainsTaxRate",
            "realized gain ratio 0.20315",
            $"{Assumptions1}（20.315% ＝ 所得税 15.315% ＋ 住民税 5%。**米国株の譲渡益にも同率**。2026-08-07 確定）"),
        // 倍率と**基準**の両方を正規化に含める。基準を含めないと「倍率だけ 2 へ直し、税を基準に
        // 含めないまま」という中途半端な追随を素通しする（計画は「往復費用**＋税**の 2 倍」）。
        //
        // 計画は 2026-08-07 に税の基準を「費用控除後の期待利益」と定め、**解いたしきい値 `2.684·C`** を
        // 明記した（IADR-0173）。さらに 2026-08-08 に **`倍率 × 税率 ≥ 1` の領域は解が無く見送る**ことを
        // 定めた（IADR-0177）。**倍率 2 と基準そのものは 2026-07-23 の裁定から変わっていない** ——
        // 本行が固定するのはその 2 つであり、しきい値 2.684 は**倍率と税率から導かれる従属値**である
        // （数値をここへ写すと、税率を変えたときに 2 か所を直す形になり、片方が残る）。
        new(
            "Assumptions.MinimumExpectedProfitMultiple",
            "2x of (round-trip cost + tax)",
            $"{Assumptions4}（利用者決定 2026-07-23。税の基準は 2026-08-07・解無しの扱いは 2026-08-08 確定）"),
        // 月次費用上限は円建ての「月あたりの上限額」であり、割合でも一回限りの金額でもない。
        new("CostLimits.Total", "JPY 20000 per month", $"{Assumptions6}（LLM＋インフラ＋データの合計）"),
        new("CostLimits.Llm", "JPY 15000 per month", $"{Assumptions6}（対象は取引判断サイクルのみ。§6.1）"),
        new("CostLimits.Infrastructure", "JPY 5000 per month", Assumptions6),
        new("CostLimits.Data", "JPY 0 per month", $"{Assumptions6}（有料情報源の導入時に総枠内で配分）"),
    ];

    public static IReadOnlyDictionary<string, PlanDefault> ByKey { get; } =
        All.ToDictionary(d => d.Key);
}
