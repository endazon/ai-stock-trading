using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10, FR-19, FR-20, ADR-0003, ADR-0007: 発注前の決定的判定コア。
// 生成AIの判断がどうであれ、ここで違反と判定された注文は発注執行へ到達しない。
// 違反は最初の1件で打ち切らず全件列挙する（FR-11 監査のため）。
public static class RiskEvaluator
{
    public static OrderScreeningResult Evaluate(
        OrderIntent intent,
        RiskManagementSettings settings,
        PortfolioSnapshot snapshot,
        IManipulativeOrderPatternDetector? patternDetector = null)
    {
        var reasons = new List<RejectionReason>();
        // FR-10, FR-19, IADR-0004: エントリー判定は建玉効果（PositionEffect）で行う。売買方向（Side）ではない。
        // 信用有効化後はショートエントリー（Side == Sell の新規建て）が発生するため、Side == Buy で
        // エントリーを近似すると kill switch 含むエントリー専用制約をすり抜ける（Issue #25）。
        var isEntry = intent.PositionEffect == PositionEffect.Open;

        // 全停止スイッチ（kill switch）: 新規建て（エントリー）のみ停止する。
        // NFR フェイルセーフ（02_requirements: 新規発注停止。保有ポジションの損切り監視は最後まで維持）
        // および ADR-0003（損切りは機械的に執行）により、手仕舞い（Close）は止めない。
        if (isEntry && snapshot.KillSwitchEngaged)
        {
            reasons.Add(RejectionReason.KillSwitchActive);
        }

        // FR-10, ADR-0009: 取引の一時停止（pause）。kill switch と同じ位置・同じ判定（isEntry のみ）で新規建てを止める。
        // 日次損失ロックアウトとは別状態の「軽い統制」。手仕舞い（Close）・損切りは isEntry の短絡で止めない。
        if (isEntry && snapshot.TradingPaused)
        {
            reasons.Add(RejectionReason.TradingPaused);
        }

        // FR-20: 段階ゲート（動作モードと資金上限）
        if (intent.Mode == TradeMode.Live && settings.Stage.Mode != TradeMode.Live)
        {
            reasons.Add(RejectionReason.StageProhibitsLiveTrading);
        }

        // FR-20, ADR-0008, IADR-0005: 段階資金上限は「投入中資金（保有ポジションの取得額合計）＋当該注文額」で
        // 判定する。単一注文額のみで比較すると、上限内の注文を複数回通して累計で上限を超過できる（Issue #27）。
        // FR-10, FR-17, #257, IADR-0106: 金額の突き合わせは基準通貨（円）で行う。外貨建て銘柄の Notional
        //（ローカル通貨）を円建ての上限と比較すると、上限が桁で緩む（過大発注を招く向き）。
        if (isEntry && snapshot.InvestedCapital + intent.NotionalInBase > settings.Stage.CapitalCap)
        {
            reasons.Add(RejectionReason.StageCapitalCapExceeded);
        }

        // FR-19: 取引ガード
        if (!settings.Guard.EnabledProductTypes.Contains(intent.ProductType))
        {
            reasons.Add(RejectionReason.ProductTypeDisabled);
        }

        if (!settings.Guard.EnabledMarkets.Contains(intent.Market))
        {
            reasons.Add(RejectionReason.MarketDisabled);
        }

        // 禁止銘柄は銘柄コードと市場の両方で照合する（同一コードが別市場に存在し得るため）。
        if (settings.Guard.BannedSymbols.Any(b => b.Symbol == intent.Symbol && b.Market == intent.Market))
        {
            reasons.Add(RejectionReason.BannedSymbol);
        }

        // 差金決済防止は（銘柄コード, 市場）で照合する。禁止銘柄判定と対称にし、別市場の
        // 同一コード（例: 日本株 6902 と同名の米国ティッカー）の誤拒否を防ぐ（Issue #26）。
        if (isEntry
            && settings.Guard.PreventSameDayReentry
            && snapshot.SymbolsTradedToday.Contains((intent.Symbol, intent.Market)))
        {
            reasons.Add(RejectionReason.SameDayReentry);
        }

        // FR-19, IADR-0006: 相場操縦とみなされ得る発注パターンの禁止。ガード有効かつ検出器が注入された
        // ときにのみ判定する（検出アルゴリズム＝注文履歴統計は後続スライス）。エントリー/手仕舞いを問わず適用する。
        if (settings.Guard.ProhibitManipulativeOrderPatterns
            && patternDetector is not null
            && patternDetector.IsSuspectedManipulation(intent, snapshot))
        {
            reasons.Add(RejectionReason.ManipulativeOrderPattern);
        }

        // FR-10: リスク上限。金額系の上限は「新規発注（エントリー）の資金投入」を制限するもの。
        // フェイルセーフ（新規発注停止・損切り監視は維持）/ ADR-0003（損切りは機械的に執行）により、
        // 手仕舞い（売り）注文には適用しない。値上がりで時価が上限超過したポジションの全量手仕舞いや、
        // 当日の発注累計が上限近い状況での損切り売りがブロックされるのを防ぐ。
        if (isEntry && intent.NotionalInBase > settings.Limits.MaxOrderAmount)
        {
            reasons.Add(RejectionReason.PerOrderAmountExceeded);
        }

        if (isEntry && snapshot.DailyOrderedAmount + intent.NotionalInBase > settings.Limits.MaxDailyOrderAmount)
        {
            reasons.Add(RejectionReason.DailyOrderAmountExceeded);
        }

        if (isEntry && snapshot.OpenPositionCount >= settings.Limits.MaxOpenPositions)
        {
            reasons.Add(RejectionReason.MaxPositionsExceeded);
        }

        // 日次損失上限・最大DD 到達時も「新規発注停止・損切り監視は維持」（フェイルセーフ）。
        // 損失拡大局面での手仕舞い（売り）を止めないよう、エントリーにのみ適用する。
        // 日次損失は実現損益と含み損益（評価損益）の合算で判定する（IADR-0008, Issue #31）。実現ゼロでも
        // 含み損が大きいケースの検知遅れを防ぐデイリーストップ。手仕舞いは含み損を実現・縮小する方向のため対象外。
        var dailyLoss = snapshot.DailyRealizedPnl + snapshot.UnrealizedPnl;
        if (isEntry && dailyLoss <= -(snapshot.Capital * settings.Limits.DailyLossLimitRatio))
        {
            reasons.Add(RejectionReason.DailyLossLimitReached);
        }

        if (isEntry && snapshot.DrawdownRatio >= settings.Limits.MaxDrawdownRatio)
        {
            reasons.Add(RejectionReason.MaxDrawdownReached);
        }

        return reasons.Count > 0
            ? OrderScreeningResult.Reject(reasons)
            : OrderScreeningResult.Approve(intent.Quantity);
    }
}
