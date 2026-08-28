using CostControlService.Domain;

namespace CostControlService.Application.Ports;

// NFR（費用）, IADR-0027/0034: 月次費用台帳（追記・月×カテゴリ別集計）。Month は "yyyy-MM"。
public interface ICostLedger
{
    /// <summary>
    /// 費用を追記し、当該月の LLM 累計（計上前/計上後）を<b>原子的に</b>返す（IADR-0034）。
    /// 並行計上でもしきい値遷移を高々 1 回に保つため、実装は月単位で直列化する（EF=トランザクション＋アドバイザリロック / InMemory=ロック）。
    /// <para>
    /// 前提: 呼び出し側は<b>アンビエント（既に開始済みの）トランザクション内から呼ばない</b>こと。EF 実装は自前で
    /// トランザクションを開始するため、入れ子になると例外になる（#79 の自動計上連携時に留意）。
    /// </para>
    /// </summary>
    LlmCostRecordOutcome Record(string month, CostCategory category, decimal amount, DateTimeOffset at);

    /// <summary>指定月・カテゴリの累計費用。</summary>
    decimal GetMonthlyTotal(string month, CostCategory category);

    /// <summary>指定月の全カテゴリ累計費用。</summary>
    decimal GetMonthlyTotalAll(string month);

    /// <summary>
    /// NFR（費用）, 05_trading-assumptions §6.1, #347: 指定月のカテゴリ別内訳。
    /// <para>
    /// 月報の「当月の LLM 利用実績」へ**対象外の費用も含めて**実績を供給するための口である
    /// （抑制はしないが記録はする。#282 の「報告書散文費用の計上漏れ→過少申告」の再発防止）。
    /// 計上のない月・カテゴリはキーを含めない（0 円の行を作らない）。
    /// </para>
    /// </summary>
    IReadOnlyDictionary<CostCategory, decimal> GetMonthlyTotals(string month);
}

// NFR（費用）, IADR-0034: 1 回の計上における当該月 LLM 累計（計上前・計上後）。しきい値上方遷移の判定入力（計上と不可分）。
public readonly record struct LlmCostRecordOutcome(decimal LlmTotalBefore, decimal LlmTotalAfter);
