using System.Globalization;
using System.Reflection;
using AiStockTrading.Backtest.Domain;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// 実装側の既定値スナップショット（FR-10, FR-17, FR-19, FR-20）。
/// <para>
/// すべての値を**実装から機械的に導出**する（定数・設定フィールド・enum の列挙・型の有無）。
/// 手で書き写すと <see cref="PlanRiskDefaults"/> と同じ紙の上の一致になり、
/// 既知逸脱の陳腐化検知（IADR-0127 検査3）が働かなくなる。
/// </para>
/// <para>
/// まだ実装が存在しない概念は、**その概念が置かれるべき型の有無をリフレクションで調べる**。
/// これにより担当 issue が型を追加した時点でスナップショットの値が変わり、既知逸脱の登録簿が
/// 更新されない限りテストが失敗する。
/// </para>
/// </summary>
public static class ActualDefaults
{
    /// <summary>空売り統制の値を保持する想定の型名（ADR-0016）。#329 が追加する。</summary>
    private const string ShortSellingLimitsTypeName = "ShortSellingLimits";

    /// <summary>発注先（Broker Provider）の 3 値を表す想定の enum 名（FR-20）。#334 が追加する。</summary>
    private const string BrokerProviderTypeName = "BrokerProvider";

    /// <summary>為替レート源の構成型（ADR-0022 / IADR-0107 / IADR-0112）。internal のため完全名で解決する。</summary>
    private const string FxOptionsTypeName =
        "AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters.FxOptions";

    /// <summary>為替レート源の選択（provider 識別子の定数を持つ）。internal のため完全名で解決する。</summary>
    private const string FxRateSourceFactoryTypeName =
        "AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters.FxRateSourceFactory";

    /// <summary>
    /// provider 名の集合を公開する実装側メンバ名（<c>FxRateSourceFactory.ProviderNames</c>）。
    /// 抽出は**このメンバだけ**を読む（IADR-0135 決定6）。
    /// </summary>
    internal const string FxProviderNamesMemberName = "ProviderNames";

    /// <summary>「未接続（no-op）」を表す provider 名。情報源ではないため集合に数えない。</summary>
    private const string FxProviderNone = "none";

    /// <summary>
    /// ADR-0016 決定10 の空売り拒否理由 9 種（2026-08-04 に 7 種から改訂。#374）。
    /// <para>
    /// 本配列は**計画が名指しする候補名**であり、実装に定義されているものだけが抽出値へ現れる
    /// （<see cref="PresentEnumNames{TEnum}"/>）。候補を落とすと、実装に有っても計画適合検査からは
    /// 見えなくなる——`StopOrderRequired` は #329 で実装済みでありながら本配列に無かったため、
    /// 計画側が 9 種へ改訂されるまで抽出値に現れていなかった（IADR-0134 決定3）。
    /// </para>
    /// </summary>
    private static readonly string[] ShortSellRejectionReasons =
    [
        "BorrowCostExceeded",
        "BorrowUnavailable",
        "BuyInBanned",
        "DividendRecordDateNear",
        "MaintenanceMarginBreach",
        "ShortExposureExceeded",
        "ShortPriceFloorBreach",
        "ShortSellDisabled",
        "StopOrderRequired",
    ];

    public static IReadOnlyDictionary<string, string> Snapshot()
    {
        var limits = TradingDefaults.CreateRiskLimits();
        var guard = TradingDefaults.CreateGuardSettings();
        var policy = TradingDefaults.CreateStagePolicy();
        // FR-17: 全体前提条件（§1 税制・§6 運用費用上限）の既定は本ファクトリが単一の出所である。
        var assumptions = TradingAssumptionsDefaults.Create();

        return new Dictionary<string, string>
        {
            // 資金・金額系。#329, IADR-0130: 初期資金は**通貨つきの権威値**（USD）、金額系 2 値は equity 比で保持する。
            // 通貨を実装側の定数から採るのは、値だけを直して単位を直さない中途半端な追随を素通しさせないため。
            ["Capital.Initial"] = CurrencyAmount(TradingDefaults.EquityCurrency, TradingDefaults.InitialEquityUsd),
            ["RiskLimits.MaxOrderAmount"] = EquityRatio(limits.MaxOrderAmountRatio),
            ["RiskLimits.MaxDailyOrderAmount"] = EquityRatioPerDay(limits.MaxDailyOrderAmountRatio),
            ["RiskLimits.MaxOpenPositions"] = Number(limits.MaxOpenPositions),

            // 損失・サイジング系。equity 比の比率で保持している。
            ["RiskLimits.DailyLossLimitRatio"] = EquityRatio(limits.DailyLossLimitRatio),
            ["RiskLimits.PerTradeRiskRatio"] = EquityRatio(limits.PerTradeRiskRatio),
            ["RiskLimits.MaxDrawdownRatio"] = EquityRatio(limits.MaxDrawdownRatio),
            ["RiskLimits.LosingStreakThreshold"] = Number(limits.LosingStreakThreshold),
            ["RiskLimits.LosingStreakSizeFactor"] = Number(limits.LosingStreakSizeFactor),

            // 取引ガード。
            ["ProductType.Values"] = EnumNames<ProductType>(),
            ["Market.Values"] = EnumNames<Market>(),
            ["Guard.EnabledProductTypes"] = Sorted(guard.EnabledProductTypes.Select(p => p.ToString())),
            ["Guard.EnabledMarkets"] = Sorted(guard.EnabledMarkets.Select(m => m.ToString())),
            ["Guard.BannedSymbols"] = Sorted(guard.BannedSymbols.Select(b => $"{b.Symbol}/{b.Market}")),
            ["Guard.PreventSameDayReentry"] = guard.PreventSameDayReentry.ToString(),
            ["Guard.ProhibitManipulativeOrderPatterns"] = guard.ProhibitManipulativeOrderPatterns.ToString(),

            // 空売り統制。専用型が未追加なら型の不在をそのまま値とする。
            ["ShortSell.Limits"] = DescribeTypeWithMembers(ShortSellingLimitsTypeName),
            ["RejectionReason.ShortSellReasons"] = PresentEnumNames<RejectionReason>(ShortSellRejectionReasons),

            // 段階ゲートと発注先。
            ["BrokerProvider.Values"] = DescribeEnumValues(BrokerProviderTypeName),
            ["Stage.Values"] = EnumNames<TradingStage>(),
            ["Stage.Initial"] = TradingDefaults.CreateStageSettings().Stage.ToString(),
            ["Stage.Stage1BrokerProvider"] = policy.SettingsFor(TradingStage.Stage1Simulate).Mode.ToString(),
            ["Stage.Stage2OrderableCapRatio"] =
                FixedAmount(policy.SettingsFor(TradingStage.Stage2MinimalLive).CapitalCap),
            ["Stage.WithdrawalDrawdownMultiple"] = Number(policy.WithdrawalDrawdownMultiple),

            // 為替レート源と鮮度（ADR-0022）。実装型は TradeDecisionService.Infrastructure の internal であり、
            // 名前で解決してリフレクションで読む（InternalsVisibleTo を増やさない。IADR-0128 / IADR-0135）。
            ["Fx.RateSourceProviders"] = FxProviderNames(),
            ["Fx.StaleRateWarningDays"] = FxOptionsDays("DefaultStaleRateWarningDays"),
            ["Fx.MaxRateAgeDays"] = FxOptionsDays("DefaultMaxRateAgeDays"),

            // Stage 0 合格判定の DD 許容値。**運用の DD 停止ライン（RiskLimits.MaxDrawdownRatio）ではない**。
            // ADR-0018 決定2 が問題視するのはこちらであり、別フィールドから抽出すると
            // 「たまたま計画と一致している値」を見て本来の逸脱を素通しする（PR #350 の指摘）。
            ["Stage0GateCriteria.MaxDrawdownTolerance"] = Ratio(Stage0GateCriteria.Default.MaxDrawdownTolerance),

            // 全体前提条件（§1 / §6）。計画が名指しするのは「譲渡益税率」と「月次の費用上限 4 値」であり、
            // いずれも TradingAssumptions の対応フィールドが抽出元である。
            ["Assumptions.CapitalGainsTaxRate"] = RealizedGainRatio(assumptions.CapitalGainsTaxRate),
            ["Assumptions.MinimumExpectedProfitMultiple"] = MinimumExpectedProfit(assumptions),
            ["CostLimits.Total"] = MonthlyAmount(assumptions.CostLimits.Total),
            ["CostLimits.Llm"] = MonthlyAmount(assumptions.CostLimits.Llm),
            ["CostLimits.Infrastructure"] = MonthlyAmount(assumptions.CostLimits.Infrastructure),
            ["CostLimits.Data"] = MonthlyAmount(assumptions.CostLimits.Data),
        };
    }

    private static string Number(decimal value) =>
        value.ToString("0.############", CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>equity に対する割合として保持されている値。</summary>
    private static string EquityRatio(decimal ratio) => $"equity ratio {ratio.ToString("0.00", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// equity に対する割合のうち、**1 日あたり**として保持されている値（#329・計画 §5）。
    /// 期間を表現に含めることで、1 注文あたりの割合との取り違えを検知可能にする。
    /// </summary>
    private static string EquityRatioPerDay(decimal ratio) => $"{EquityRatio(ratio)} per day";

    /// <summary>
    /// 通貨つきの金額（#329・IADR-0130 決定3）。通貨は実装側の定数から採り、
    /// 「値だけ直して単位が旧のまま」という追随漏れを検知可能にする。
    /// </summary>
    private static string CurrencyAmount(Currency currency, decimal amount) =>
        $"{currency.ToString().ToUpperInvariant()} {Number(amount)}";

    /// <summary>基準を持たない純粋な割合（バックテスト結果に対する比率など）。</summary>
    private static string Ratio(decimal ratio) => $"ratio {ratio.ToString("0.00", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// 基準通貨（円）建ての固定額として保持されている値。計画は equity 比での保持を求めており、
    /// 単位・基準を含めて表現することで「割合か固定額か」の取り違えを検知可能にする。
    /// </summary>
    private static string FixedAmount(decimal amount) =>
        $"JPY {amount.ToString("0.############", CultureInfo.InvariantCulture)} (fixed amount)";

    /// <summary>
    /// 譲渡益（実現益）に対する税率として保持されている値。基準（何に対する率か）を含めることで、
    /// 配当課税・源泉徴収率など別基準の率との取り違えを検知可能にする。
    /// </summary>
    private static string RealizedGainRatio(decimal ratio) =>
        $"realized gain ratio {ratio.ToString("0.############", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// 最小期待利益の「倍率」と「基準」を組にした値。倍率は
    /// <see cref="TradingAssumptionsDefaults.MinimumExpectedProfitMultiple"/>、基準は
    /// <see cref="CostCalculator.MinimumViableProfit"/> の実際の計算結果から導出する。
    /// <para>
    /// 倍率だけを見ると「2 へ直したが基準に税を含めない」という中途半端な追随を素通しする。
    /// 計画（§4）は<b>往復費用＋税</b>の 2 倍を求めているため、基準も検知対象に含める。
    /// </para>
    /// </summary>
    private static string MinimumExpectedProfit(TradingAssumptions assumptions) =>
        $"{assumptions.MinimumExpectedProfitMultiple.ToString("0.############", CultureInfo.InvariantCulture)}x "
            + $"of ({MinimumProfitBasis(assumptions)})";

    /// <summary>
    /// 最小期待利益の基準に税が含まれるかを、実装を実際に呼び出して判定する（手書きで断定しない）。
    /// 税率と手数料が非ゼロの前提条件で <see cref="CostCalculator.MinimumViableProfit"/> を評価し、
    /// 「往復費用 × 倍率」と一致すれば税は基準に入っていない。上回れば入っている。
    /// </summary>
    private static string MinimumProfitBasis(TradingAssumptions assumptions)
    {
        // 既定の手数料・為替スプレッドは「未登録＝0」であり、そのままでは往復費用が 0 になって
        // 税の有無が差として現れない。判定用に手数料と税率を非ゼロへ置いた前提条件で評価する。
        var probe = assumptions with
        {
            CapitalGainsTaxRate = 0.2m,
            UnitedStatesCommission = new CommissionSchedule(Rate: 0.001m, Minimum: 0m, Cap: 0m),
        };
        const decimal Notional = 100_000m;

        var roundTrip = CostCalculator.EstimateRoundTripCost(probe, Market.UnitedStates, Notional);
        var minimum = CostCalculator.MinimumViableProfit(probe, Market.UnitedStates, Notional);
        return minimum > roundTrip * probe.MinimumExpectedProfitMultiple
            ? "round-trip cost + tax"
            : "round-trip cost";
    }

    /// <summary>
    /// 月あたりの基準通貨（円）建て上限額。単位（円）と期間（月）を含めることで、
    /// 割合・一回限りの金額・別通貨との取り違えを検知可能にする。
    /// </summary>
    private static string MonthlyAmount(decimal amount) =>
        $"JPY {amount.ToString("0.############", CultureInfo.InvariantCulture)} per month";

    private static string Sorted(IEnumerable<string> values) =>
        string.Join(", ", values.OrderBy(v => v, StringComparer.Ordinal));

    private static string EnumNames<TEnum>() where TEnum : struct, Enum => Sorted(Enum.GetNames<TEnum>());

    /// <summary>指定した名前のうち、実際に enum に定義されているものだけを返す。</summary>
    private static string PresentEnumNames<TEnum>(IEnumerable<string> candidates) where TEnum : struct, Enum
    {
        var defined = Enum.GetNames<TEnum>().ToHashSet(StringComparer.Ordinal);
        var present = candidates.Where(defined.Contains).ToArray();
        return present.Length == 0
            ? $"(none of the {typeof(TEnum).Name} members defined)"
            : Sorted(present);
    }

    /// <summary>
    /// 実装が選択できる為替レート源（provider 識別子）の集合。
    /// <para>
    /// 計画（ADR-0022 決定1・2）は日銀を第一・FRED をフォールバックとする 2 源を求めるが、
    /// <b>順位そのものは値ではなく振る舞い</b>（切り替え条件・記録・通知）であるため集合として比較する
    /// （IADR-0127 決定4 / IADR-0135 決定2）。
    /// </para>
    /// </summary>
    private static string FxProviderNames() =>
        FxProviderNamesFrom(FindTradeDecisionType(FxRateSourceFactoryTypeName));

    /// <summary>
    /// provider 識別子の集合を、型の <see cref="FxProviderNamesMemberName"/> メンバ**だけ**から読む
    /// （IADR-0135 決定6 / #378）。「未接続」を表す <see cref="FxProviderNone"/> は情報源ではないので除く。
    /// <para>
    /// 型の <c>public const string</c> を全件収集する形は採らない。その形では、実装が
    /// provider と無関係な定数（<c>SectionName</c>・ログ接頭辞・構成キー名など）を足した瞬間に
    /// <b>黙って provider として数えられ</b>、計画適合検査が誤った実際値のまま緑になる。
    /// 統制の検証機構が誤った値で緑になるのは最も悪い失敗モードである。
    /// </para>
    /// <para>
    /// 逆向き（実装が受け付けるのに集合に無い）は実装側で塞いである。
    /// <c>FxRateSourceFactory</c> の分岐は同メンバを関門として通るため、集合に無い名前は到達しない
    /// （<c>FxRateSourceFactoryTests.公開するprovider集合は実装が実際に受け付ける集合と一致する</c>）。
    /// </para>
    /// </summary>
    /// <param name="type">
    /// 読み取り対象の型。<c>null</c> は型の不在。**引数で受けるのは検査可能性のため**であり、
    /// 「無関係な定数を混入させない」ことを偽の型に対して実証できるようにしている。
    /// </param>
    internal static string FxProviderNamesFrom(Type? type)
    {
        if (type is null)
        {
            return "(type FxRateSourceFactory not found)";
        }

        var member = type.GetField(FxProviderNamesMemberName, BindingFlags.Public | BindingFlags.Static);
        if (member?.GetValue(null) is not IEnumerable<string> declared)
        {
            return $"(FxRateSourceFactory.{FxProviderNamesMemberName} not found)";
        }

        var names = declared
            .Where(v => !string.IsNullOrWhiteSpace(v)
                && !string.Equals(v, FxProviderNone, StringComparison.Ordinal))
            .ToArray();

        return names.Length == 0 ? "(no fx provider names declared)" : Sorted(names);
    }

    /// <summary>
    /// <c>FxOptions</c> が持つ日数の定数（<c>public const int</c>）を単位つきで返す。
    /// 定数が無ければ**不在をそのまま値とする**（担当 issue が追加した時点で値が変わり、
    /// 既知逸脱の登録が更新されない限りテストが失敗する）。
    /// </summary>
    private static string FxOptionsDays(string constantName)
    {
        var type = FindTradeDecisionType(FxOptionsTypeName);
        if (type is null)
        {
            return "(type FxOptions not found)";
        }

        var field = type.GetField(constantName, BindingFlags.Public | BindingFlags.Static);
        return field?.GetRawConstantValue() is int days
            ? $"{Number(days)} days"
            : $"(FxOptions.{constantName} not found)";
    }

    /// <summary>
    /// TradeDecisionService.Infrastructure の型を完全名で解決する。同アセンブリの実装型は
    /// <c>internal</c> のまま据え置かれているため（IADR-0128）、<c>typeof</c> ではなく名前で引く。
    /// </summary>
    private static Type? FindTradeDecisionType(string fullName) =>
        typeof(DecisionOptionsLoader).Assembly.GetType(fullName, throwOnError: false);

    /// <summary>参照アセンブリ群から型を名前で探す（名前空間は問わない）。</summary>
    private static Type? FindType(string typeName) =>
        new[]
        {
            typeof(TradingDefaults).Assembly,
            typeof(RejectionReason).Assembly,
        }
        .SelectMany(a => a.GetTypes())
        .FirstOrDefault(t => t.Name == typeName);

    private static string DescribeTypeWithMembers(string typeName)
    {
        var type = FindType(typeName);
        if (type is null)
        {
            return $"(type {typeName} not found)";
        }

        var members = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);
        return $"type {typeName} with members: {Sorted(members)}";
    }

    private static string DescribeEnumValues(string typeName)
    {
        var type = FindType(typeName);
        if (type is null)
        {
            return $"(type {typeName} not found)";
        }

        return type.IsEnum ? Sorted(Enum.GetNames(type)) : $"(type {typeName} is not an enum)";
    }
}
