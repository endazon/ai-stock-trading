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
        IManipulativeOrderPatternDetector? patternDetector = null,
        ShortSellOrderContext? shortSellContext = null)
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
        // FR-10, FR-17, #257, IADR-0107: 金額の突き合わせは基準通貨（円）で行う。外貨建て銘柄の Notional
        //（ローカル通貨）を円建ての上限と比較すると、上限が桁で緩む（過大発注を招く向き）。
        if (isEntry && snapshot.InvestedCapital + intent.NotionalInBase > settings.Stage.CapitalCap)
        {
            reasons.Add(RejectionReason.StageCapitalCapExceeded);
        }

        // FR-19, ADR-0016 決定1, #332, IADR-0132: 取引ガード（商品種別は 現物 / 信用買い / 空売り の 3 値を
        // それぞれ独立に制御する。既定は現物のみ有効）。
        // 照合は**実効商品種別**で行う（決定3）。新規売り建てを Cash と申告してガードを迂回できないようにする。
        // 適用は**新規建てのみ**である（決定4）。無効な商品種別の建玉を手仕舞えないと、
        // FR-10 の不変条件「手仕舞い（Close）と損切りは止めない」（ADR-0009）に反する。
        // 例: 既定では空売りが無効であり、全注文へ適用すると空売り建玉の買戻し（Buy × Close）が拒否される。
        var effectiveProductType = ProductTypeResolver.Resolve(intent);
        if (isEntry && !ProductTypeResolver.IsEnabled(settings.Guard, effectiveProductType))
        {
            reasons.Add(RejectionReason.ProductTypeDisabled);
        }

        if (!settings.Guard.EnabledMarkets.Contains(intent.Market))
        {
            reasons.Add(RejectionReason.MarketDisabled);
        }

        // 禁止銘柄は銘柄コードと市場の両方で照合する（同一コードが別市場に存在し得るため）。
        // 照合規則は BannedSymbol.Matches が単一情報源（市場は厳密一致・コードは表記差を吸収。IADR-0132 決定6）。
        if (settings.Guard.BannedSymbols.Any(b => b.Matches(intent.Symbol, intent.Market)))
        {
            reasons.Add(RejectionReason.BannedSymbol);
        }

        // FR-19, #332, IADR-0132 決定5: 差金決済防止（同一銘柄の同日再エントリー禁止）は
        // **日本株の現物取引にのみ**適用する。本ガードは日本の差金決済規制（金商法 161 条の 2・
        // 06_daytrading-review §2.1）に対応するものであり、**米国株は信用口座（margin account）で運用するため
        // Good Faith Violation が発生しない**（05_trading-assumptions §5「米国口座の種別・決済」・FR-19 本文）。
        // 米国株の回転数は日次発注金額上限（equity の 150%/日）と保有建玉数上限（3）で管理する。
        // 信用（信用買い・空売り）は同一保証金での同日無制限回転が可能なため現物に限る（§5「差金決済防止」）。
        //
        // 照合は（銘柄コード, 市場）で行う。禁止銘柄判定と対称にし、別市場の
        // 同一コード（例: 日本株 6902 と同名の米国ティッカー）の誤拒否を防ぐ（Issue #26）。
        if (isEntry
            && settings.Guard.PreventSameDayReentry
            && intent.Market == Market.Japan
            && effectiveProductType == ProductType.Cash
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
        //
        // FR-10, #329, IADR-0130 決定1/2: 金額上限は equity 比で保持されており、判定時に equity から解決する。
        // equity は snapshot.Capital（＝前営業日終値時点の評価額・当日中は不変。計画 §5 注記）であり、
        // 日次損失上限・最大 DD と同一の基準を用いる（基準がばらけると「厳しい方が効く」の比較が成り立たない）。
        if (isEntry && intent.NotionalInBase > settings.Limits.MaxOrderAmountFor(snapshot.Capital))
        {
            reasons.Add(RejectionReason.PerOrderAmountExceeded);
        }

        // FR-10, #302, IADR-0130 決定4: 日次枠は**新規建ての発注代金の合計**で判定する。isEntry による
        // ゲート側の除外に加え、カウンタ側（DailyOrderedAmount の集計＝PortfolioProjection）も
        // 新規建てに限定してある。片側だけでは「拒否されないが枠は減る」状態が残る。
        if (isEntry
            && snapshot.DailyOrderedAmount + intent.NotionalInBase
                > settings.Limits.MaxDailyOrderAmountFor(snapshot.Capital))
        {
            reasons.Add(RejectionReason.DailyOrderAmountExceeded);
        }

        // FR-10, ADR-0016 決定9: 保有**建玉**数の上限（銘柄数では数えない）。
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

        // FR-10, UC-06, ADR-0016, #329 第 2 段階: 空売り（新規売り建て）専用の統制 8 規則。
        // 既存の統制に**上乗せ**して課す（置き換えではない）。空売りは損失に上限が無く、
        // 「損切りが機能すれば損失は限定される」という既存統制の前提が成り立たないためである。
        // 上限が競合する場合は常に厳しい方が効く（1 注文 25% と 1 銘柄 10% の両方が列挙される＝ AND）。
        //
        // #332, IADR-0132 決定2: 空売りの有効・無効は**取引ガードの商品種別**（3 値）が単一情報源である。
        // 専用フラグ（旧 ShortSellSettings.Enabled）と二重に持つと設定が食い違う。
        if (isEntry && ShortSellEvaluator.IsShortEntry(intent))
        {
            var shortSellEnabled = ProductTypeResolver.IsEnabled(settings.Guard, ProductType.ShortSell);
            reasons.AddRange(ShortSellEvaluator.Evaluate(
                intent, shortSellEnabled, settings.ShortSell.Limits, snapshot.Capital, shortSellContext));
        }

        return reasons.Count > 0
            ? OrderScreeningResult.Reject(reasons)
            : OrderScreeningResult.Approve(intent.Quantity);
    }
}
