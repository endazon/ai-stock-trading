namespace ReportService.Domain;

// FR-06, FR-07, #840, IADR-0352 決定 5: 報告書の**入力**の語彙。
//
// 報告書は多数の供給元（取引台帳・監査台帳・LLM）から入力を集めて組み立てる。供給が届かなかった入力は
// 各節が「照会できませんでした」「供給されていません」と書く（値を騙らない）が、**どの入力が欠けたまま
// 出来上がった報告書なのか**は本文を全部読まないと分からなかった。再起動直後の 401 で入力が広範に
// 欠けた報告書が、そのまま提示・確定まで進んだ（#840）。
//
// 本列挙は「欠けた入力」を記録・提示するための閉じた語彙である。**名前は永続化される**
// （`reports.UnsuppliedInputs`）ため、改名するときは読み出し側の互換を先に用意する。
public enum ReportInput
{
    /// <summary>期間の約定（取引台帳）。不達でも空列へ倒れるため、供給の成否は観測から判定する。</summary>
    Fills,

    /// <summary>維持率割れによる自動縮小の記録。</summary>
    MarginReductions,

    /// <summary>強制買戻し（推定）の記録。</summary>
    BuyInInferences,

    /// <summary>為替の情報源の状態（監査台帳）。</summary>
    FxSourceStatus,

    /// <summary>LLM 利用実績（監査台帳）。</summary>
    LlmUsage,

    /// <summary>借株料の記録（監査台帳）。</summary>
    BorrowFees,

    /// <summary>判断根拠の記録（監査台帳）。</summary>
    TradeRationales,

    /// <summary>建玉（取引台帳の射影）。</summary>
    OpenPositions,

    /// <summary>OpenD 稼働率（稼働観測ログ）。</summary>
    OpenDUptime,

    /// <summary>現在の運用段階（段階ゲート）。</summary>
    CurrentStage,

    /// <summary>為替差損益の期末レート（外部の為替レート源）。</summary>
    PeriodEndFxRate,

    /// <summary>散文（LLM ドラフト）。未供給＝プレースホルダ散文。</summary>
    Narrative,

    /// <summary>
    /// FR-11, ADR-0041 決定 1, #870, #859, IADR-0360: <b>手動売買の取り込み</b>（取引台帳）。
    /// 🔴 未供給は日報 §2-b が欠けるだけでなく、<b>在庫の畳み込みからも落ちる</b>
    /// ——実在しない建玉の評価損益が出得る。**空列（該当なし）へ倒さない。**
    /// </summary>
    DriftAdoptions,

    /// <summary>
    /// FR-07, UC-03, #839, IADR-0382: <b>上位方針</b>（方針階層の親の直近確定済み報告書。
    /// 日報→週報 / 週報→月報 / 月報→前月の月報）。
    /// 🔴 未供給＝<b>方針の連鎖が切れている</b>。方針文は「上位方針（…）は確定済みのものがないため
    /// 参照していません。」と書くが、それは本文を読まないと分からなかった（#839 の主訴）。
    /// 🔴 <b>見送り（リトライ）の対象にしない</b>——供給元は自リポジトリのストアであり、待っても増えない。
    /// </summary>
    ParentPolicy,

    /// <summary>
    /// FR-07, UC-03, #839, IADR-0382: <b>前期の確定済み方針</b>（同種別の直近確定済み。継続案の素）。
    /// 未供給＝継続する方針の実体が無い（方針文は「参照できる確定済みの…方針がありません。」になる）。
    /// 🔴 <b>月報は上位＝前期</b>（<c>ParentKind(Monthly) == Monthly</c>）であり、同じ事実を 2 回数えない。
    /// </summary>
    PreviousPolicy,
}

// FR-06, #840, IADR-0352 決定 5: 入力の表示名・種別ごとの適用・永続化形式（純関数）。
public static class ReportInputs
{
    /// <summary>
    /// その種別の報告書が**実際に使う**入力か。使わない入力の欠落を警告に混ぜない
    /// （週報は建玉を描かないのに「建玉が未供給」と出れば、読み手は要らない差し戻しをする）。
    /// <para>
    /// 🔴 根拠は <see cref="ReportRenderer"/> と ReportDraftService の種別分岐である。**節を種別へ足したら
    /// ここも足す**（足し忘れは「欠けているのに警告が出ない」側へ倒れる。ReportInputsTests が現状を固定する）。
    /// </para>
    /// </summary>
    public static bool AppliesTo(ReportInput input, ReportKind kind) => input switch
    {
        ReportInput.Fills or ReportInput.Narrative => true,
        // 🔴 日報 §2-b を描くのは日報だけだが、**在庫の畳み込みは全種別が行う**（欠けると週報・月報も
        // 実在しない建玉の評価損益を出す）。したがって全種別が使う入力である。
        ReportInput.DriftAdoptions => true,
        // 🔴 #839: 方針連鎖（月報 → 週報 → 日報）は全種別が持つ。月報の上位は前月の月報である。
        ReportInput.ParentPolicy or ReportInput.PreviousPolicy => true,
        // 日報 §2 の明細・週報 §3 のハイライトが根拠を転記する。月報の内訳は根拠を描かない。
        ReportInput.TradeRationales => kind is ReportKind.Daily or ReportKind.Weekly,
        // 日報 §3 だけが建玉を持つ。
        ReportInput.OpenPositions => kind == ReportKind.Daily,
        // 月報 §5 の三者比較だけが段階を使う。
        ReportInput.CurrentStage => kind == ReportKind.Monthly,
        // リスク統制の記録の子節・サマリの独立行は日報と月報にだけ出る（週報は計画が求めていない）。
        ReportInput.MarginReductions
            or ReportInput.BuyInInferences
            or ReportInput.FxSourceStatus
            or ReportInput.LlmUsage
            or ReportInput.BorrowFees
            or ReportInput.OpenDUptime
            or ReportInput.PeriodEndFxRate => kind is ReportKind.Daily or ReportKind.Monthly,
        _ => false,
    };

    /// <summary>利用者へ見せる名前（Discord の通知・`/report show`）。コード定数であり外部入力を含まない。</summary>
    public static string Label(ReportInput input) => input switch
    {
        ReportInput.Fills => "期間の約定",
        ReportInput.MarginReductions => "自動縮小の記録",
        ReportInput.BuyInInferences => "強制買戻し（推定）の記録",
        ReportInput.FxSourceStatus => "為替の情報源の状態",
        ReportInput.LlmUsage => "LLM 利用実績",
        ReportInput.BorrowFees => "借株料の記録",
        ReportInput.TradeRationales => "判断根拠",
        ReportInput.OpenPositions => "建玉",
        ReportInput.OpenDUptime => "OpenD 稼働率",
        ReportInput.CurrentStage => "運用段階",
        ReportInput.PeriodEndFxRate => "為替差損益の期末レート",
        ReportInput.Narrative => "散文（LLM）",
        ReportInput.DriftAdoptions => "手動売買の取り込み",
        ReportInput.ParentPolicy => "上位方針（親の確定済み報告書）",
        ReportInput.PreviousPolicy => "前期の確定済み方針",
        _ => input.ToString(),
    };

    /// <summary>表示名の列（宣言順・重複なし）。</summary>
    public static IReadOnlyList<string> Labels(IEnumerable<ReportInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return [.. Normalize(inputs).Select(Label)];
    }

    /// <summary>永続化形式（列挙名のカンマ区切り・宣言順）。空なら <c>null</c>（＝列は NULL のまま）。</summary>
    public static string? Serialize(IEnumerable<ReportInput>? inputs)
    {
        var normalized = Normalize(inputs ?? []);
        return normalized.Count == 0 ? null : string.Join(',', normalized);
    }

    /// <summary>
    /// 永続化形式を読む。NULL・空は空列（既存行・未供給なし）。
    /// **解釈できない要素は捨てる**（将来の語彙を古い版が読んでも落ちない。捨てた分は警告が減る側であり、
    /// 本文の節ごとの「照会できませんでした」は残っている）。
    /// </summary>
    public static IReadOnlyList<ReportInput> Parse(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
            return [];

        var parsed = new List<ReportInput>();
        foreach (var token in serialized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<ReportInput>(token, ignoreCase: false, out var input) && Enum.IsDefined(input))
                parsed.Add(input);
        }

        return Normalize(parsed);
    }

    private static List<ReportInput> Normalize(IEnumerable<ReportInput> inputs) =>
        [.. inputs.Distinct().OrderBy(i => (int)i)];
}
