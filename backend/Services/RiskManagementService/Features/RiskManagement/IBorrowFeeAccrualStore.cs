using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-11, SC-03, UC-06, #465, ADR-0027, IADR-0183:
// **借株料の日次計上の台帳。**
//
// ADR-0016 決定15 は SC-03 と報告書へ「借株料の累計」の表示を求めていたが、
// **その値をどう作るかを定めていなかった**。ADR-0027 が 5 点（計上のタイミング・単位・期間の境界・
// 料率変動・供給元）を確定させたため、記録側を先行して実装する（同 §結果）。
//
// **本ストアは 2 つの台帳を持つ。**「計上できた日」と「料率が取得できなかった日」である。
// 決定4 が「0 として計上しない」と明示しており、**両者を同じ行で表すと区別が失われる**。
public interface IBorrowFeeAccrualStore
{
    /// <summary>
    /// 1 日分の計上を記録する（**建玉 × 取引日で 1 件**・冪等）。
    /// <para>
    /// 主キーは (Symbol, Market, TradingDay) であり、**同じ日を二度計上しても金額が狂わない**。
    /// 既に同じ日の行があれば**書き換えない**（最初に計上した料率・金額を正とする ——
    /// 後から別の料率で上書きすると、監査に残した日次の内訳と台帳が食い違う）。
    /// </para>
    /// </summary>
    /// <returns>新たに記録したなら <c>true</c>。既に同じ日の行があったなら <c>false</c>。</returns>
    bool Record(BorrowFeeAccrual accrual);

    /// <summary>
    /// **料率が取得できなかった日**を記録する（建玉 × 取引日で 1 件・冪等）。
    /// <para>
    /// 🔴 <b>0 円の計上として記録してはならない</b>（ADR-0027 決定4）。
    /// 0 を積むと「その日は費用が発生しなかった」と読め、**Stage 1 の「借株料は 1 円も掛かっていない」という
    /// 誤読が構造的に起こる**。
    /// </para>
    /// </summary>
    /// <returns>新たに記録したなら <c>true</c>。既に同じ日の行があったなら <c>false</c>。</returns>
    bool RecordUnavailable(BorrowFeeUnavailableDay day);

    /// <summary>期間 [fromInclusive, toInclusive] に**帰属する**計上（**按分しない**。決定3）。</summary>
    IReadOnlyList<BorrowFeeAccrual> GetAccrualsBetween(DateOnly fromInclusive, DateOnly toInclusive);

    /// <summary>期間 [fromInclusive, toInclusive] の**未供給の日**。</summary>
    IReadOnlyList<BorrowFeeUnavailableDay> GetUnavailableDaysBetween(DateOnly fromInclusive, DateOnly toInclusive);
}

// FR-10, #465, ADR-0027 決定2, IADR-0183: 建玉の一次識別子。
//
// 本リポジトリの建玉は符号付き在庫の射影（<c>PortfolioProjection</c>）で (銘柄, 市場) ごとに畳まれており、
// **建玉ごと**（決定2）の実装上の写像はこの組である。決定2 は「銘柄・口座へは**合算で導出する**」と定めるため、
// **銘柄別・口座全体の値を別に積むストアを作らない。**
public readonly record struct BorrowFeePositionKey(string Symbol, Market Market);
