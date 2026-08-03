using System.Globalization;
using System.Reflection;
using AiStockTrading.Backtest.Domain;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// 実装側の既定値スナップショット（FR-10, FR-19, FR-20）。
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

    /// <summary>ADR-0016 決定10 の空売り拒否理由 7 種。</summary>
    private static readonly string[] ShortSellRejectionReasons =
    [
        "BorrowCostExceeded",
        "BorrowUnavailable",
        "DividendRecordDateNear",
        "MaintenanceMarginBreach",
        "ShortExposureExceeded",
        "ShortPriceFloorBreach",
        "ShortSellDisabled",
    ];

    public static IReadOnlyDictionary<string, string> Snapshot()
    {
        var limits = TradingDefaults.CreateRiskLimits();
        var guard = TradingDefaults.CreateGuardSettings();
        var policy = TradingDefaults.CreateStagePolicy();

        return new Dictionary<string, string>
        {
            // 資金・金額系。現行は基準通貨（円）の固定額で保持している。
            ["Capital.Initial"] = FixedAmount(TradingDefaults.InitialCapital),
            ["RiskLimits.MaxOrderAmount"] = FixedAmount(limits.MaxOrderAmount),
            ["RiskLimits.MaxDailyOrderAmount"] = FixedAmount(limits.MaxDailyOrderAmount),
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
            ["Stage.Stage1BrokerProvider"] = policy.SettingsFor(TradingStage.Stage1Paper).Mode.ToString(),
            ["Stage.Stage2OrderableCapRatio"] =
                FixedAmount(policy.SettingsFor(TradingStage.Stage2MinimalLive).CapitalCap),
            ["Stage.WithdrawalDrawdownMultiple"] = Number(policy.WithdrawalDrawdownMultiple),

            // Stage 0 合格判定の DD 許容値。**運用の DD 停止ライン（RiskLimits.MaxDrawdownRatio）ではない**。
            // ADR-0018 決定2 が問題視するのはこちらであり、別フィールドから抽出すると
            // 「たまたま計画と一致している値」を見て本来の逸脱を素通しする（PR #350 の指摘）。
            ["Stage0GateCriteria.MaxDrawdownTolerance"] = Ratio(Stage0GateCriteria.Default.MaxDrawdownTolerance),
        };
    }

    private static string Number(decimal value) =>
        value.ToString("0.############", CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>equity に対する割合として保持されている値。</summary>
    private static string EquityRatio(decimal ratio) => $"equity ratio {ratio.ToString("0.00", CultureInfo.InvariantCulture)}";

    /// <summary>基準を持たない純粋な割合（バックテスト結果に対する比率など）。</summary>
    private static string Ratio(decimal ratio) => $"ratio {ratio.ToString("0.00", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// 基準通貨（円）建ての固定額として保持されている値。計画は equity 比での保持を求めており、
    /// 単位・基準を含めて表現することで「割合か固定額か」の取り違えを検知可能にする。
    /// </summary>
    private static string FixedAmount(decimal amount) =>
        $"JPY {amount.ToString("0.############", CultureInfo.InvariantCulture)} (fixed amount)";

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
