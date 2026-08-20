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
        ShortSellOrderContext? shortSellContext = null,
        StageProductPolicy.StageReleaseContext? stageRelease = null,
        BuyInBanSupply? buyInBan = null)
    {
        var reasons = new List<RejectionReason>();
        // FR-10, FR-19, IADR-0004: エントリー判定は建玉効果（PositionEffect）で行う。売買方向（Side）ではない。
        // 信用有効化後はショートエントリー（Side == Sell の新規建て）が発生するため、Side == Buy で
        // エントリーを近似すると kill switch 含むエントリー専用制約をすり抜ける（Issue #25）。
        var isEntry = intent.PositionEffect == PositionEffect.Open;

        // FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153: **口座種別はブローカーへ照会した結果を正とする。**
        // 利用者の設定値（Guard.ConfiguredAccountType）は食い違いの検知にのみ使う。
        //
        // **供給が無い（照会失敗・種別不明）ときは新規建てを止める（フェイルクローズ）。**
        // 「不明なら信用口座とみなす」に倒してはならない——現金口座なのに GFV 回避ガードが無効のまま回転させると
        // 3 回の Good Faith Violation で **90 日の口座制限**に至り、しかもそれは事後にしか分からない
        // （ADR-0021 79 行。決定3 が防ごうとしている当の事故である）。
        //
        // **手仕舞い（Close）・損切りは止めない**（isEntry の短絡）。ADR-0009 の不変条件であり、
        // 口座種別が分からないことを理由に建玉を閉じられなくするのは統制ではなく事故である。
        var observedAccount = snapshot.Account;
        var accountVerified = observedAccount is not null
            && observedAccount.AccountType == settings.Guard.ConfiguredAccountType;
        if (isEntry
            && AccountTypePolicy.RequiresVerifiedAccount(intent.Mode)
            && !accountVerified)
        {
            reasons.Add(RejectionReason.BrokerAccountTypeUnverified);
        }

        // 以降の口座種別依存の統制は**照会結果**（設定値ではない）で切り替える。照会結果が無ければ null であり、
        // 各統制は「口座種別が確定していない」側の扱いをする（上の BrokerAccountTypeUnverified が新規建てを止めている）。
        var accountType = observedAccount?.AccountType;

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

        // FR-20, #334, IADR-0140 決定5: 段階ゲート（既定の発注先と資金上限）。
        // 発注先の**設定**（RiskManagementSettings.BrokerProvider）は段階と独立に変更でき、Stage 1 のまま
        // moomoo REAL を選ぶ操作も計画上は保存できる（05_screens「保存を妨げないが警告を表示する」）。
        // しかし**実弾の注文そのものは段階が実弾を既定としない限り止める**。計画は保存の可否について述べており、
        // 発注の可否については FR-20 本文の「段階ごとの動作モード…を強制できる」が生きているため、安全側に倒す。
        if (intent.Mode == BrokerProvider.MoomooReal && settings.Stage.Mode != BrokerProvider.MoomooReal)
        {
            reasons.Add(RejectionReason.StageProhibitsLiveTrading);
        }

        // FR-20, ADR-0008, IADR-0005: 段階の発注可能額は「投入中資金（保有ポジションの取得額合計）＋当該注文額」で
        // 判定する。単一注文額のみで比較すると、上限内の注文を複数回通して累計で上限を超過できる（Issue #27）。
        // FR-10, FR-17, #257, #364, IADR-0107/0152: 金額の突き合わせは基準通貨（USD）で行う。非基準通貨建て銘柄の
        // Notional（ローカル通貨）を基準通貨建ての上限と比較すると、上限が桁でずれる。
        // **#364 で失敗モードの向きが反転した**（IADR-0152 決定1）。JPY 基準では非基準通貨（USD）のレートが 1 より
        // 大きく、換算漏れは「上限が約 150 倍に緩む」＝**過大発注**を招いた（#257 の実測事故）。USD 基準では非基準
        // 通貨（JPY）のレートが 1 より小さいため、換算漏れは「名目額が桁で大きく見える」＝**過剰拘束**（発注が
        // 止まる）に倒れる。**いずれにせよ換算は必須である**——安全側へ倒れることは、換算を省いてよい理由にならない。
        // FR-20, #333, IADR-0136: 段階の発注可能額は**総資金比**で保持されており、判定時に equity から解決する
        // （Stage 2 ＝ 総資金の 30%。計画 §5）。equity は FR-10 の金額上限と同じ snapshot.Capital を用いる
        // （基準がばらけると「厳しい方が効く」の比較が成り立たない）。
        if (isEntry
            && snapshot.InvestedCapital + intent.NotionalInBase
                > settings.Stage.OrderableCapFor(snapshot.Capital))
        {
            reasons.Add(RejectionReason.StageCapitalCapExceeded);
        }

        // FR-19, ADR-0016 決定1, #332, IADR-0132: 取引ガード（商品種別は 現物 / 信用買い / 空売り の 3 値を
        // それぞれ独立に制御する。既定は現物のみ有効）。
        // 照合は**実効商品種別**で行う（決定3）。新規売り建てを Cash と申告してガードを迂回できないようにする。
        // 適用は**新規建てのみ**である（決定4）。無効な商品種別の建玉を手仕舞えないと、
        // FR-10 の不変条件「手仕舞い（Close）と損切りは止めない」（ADR-0009）に反する。
        // 例: 既定では空売りが無効であり、全注文へ適用すると空売り建玉の買戻し（Buy × Close）が拒否される。
        //
        // #375, ADR-0021 決定4-4/決定5: 照合する有効・無効は**口座種別を加味した実効値**である
        // （利用者設定 ∩ 口座が対応する種別）。現金口座では信用買い・空売りが**口座の能力として成立しない**ため、
        // 設定で有効になっていても通さない。設定側の遮断（RiskSettingsService.UpdateGuard）と二重に置くのは、
        // 口座種別を切り替えた後に古い設定が残る経路・API を直に叩く経路を塞ぐためである（多層防御）。
        var effectiveProductType = ProductTypeResolver.Resolve(intent);
        if (isEntry && !ProductTypeResolver.IsEnabled(settings.Guard, accountType, effectiveProductType))
        {
            reasons.Add(RejectionReason.ProductTypeDisabled);
        }

        // FR-20, ADR-0016 決定8/決定14, #333, IADR-0139: **段階別の商品種別強制**。
        // Stage 1 は 3 種すべてを検証・Stage 2 は現物のみ・Stage 3 で信用買いと空売りを解禁する
        // （空売りはさらに equity $5,000 以上 かつ 空売りを含む戦略での Stage 0 再充足を要する）。
        // 上の取引ガード（設定値）とは**別の規則**であり、両方を満たす必要がある（常に厳しい方が効く）。
        // **適用は新規建てのみ**（planning#179 の裁定）。手仕舞い・損切りは止めない——
        // 段階を上げる前に建てた建玉を閉じられないと ADR-0009 の不変条件に反する。
        // 照合は実効商品種別で行う（申告値で段階制約を迂回できないようにする。IADR-0132 決定3 と同じ規律）。
        if (isEntry
            && StageProductPolicy.Evaluate(
                settings.Stage.Stage, effectiveProductType, snapshot.Capital, stageRelease) is { } stageReason)
        {
            reasons.Add(stageReason);
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

        // FR-19, #332, #375, IADR-0132 決定5, ADR-0021 決定4-1: 差金決済防止（同一銘柄の同日再エントリー禁止）は
        // **現物**に限り、かつ**適用範囲が口座種別に依存する**。
        //
        //   - 日本株の現物: 日本の差金決済規制（金商法 161 条の 2・06_daytrading-review §2.1）。**口座種別に依存しない**
        //   - 米国株の現物: **現金口座でのみ**適用する。Good Faith Violation は現金口座で発生する（ADR-0021 決定4-1）。
        //     信用口座（既定）では売却代金を決済前に再利用できるため発生せず、回転数は日次発注金額上限
        //     （equity の 150%/日）と保有建玉数上限（3）で管理する
        //
        // **#332 の是正（日本株現物への限定）は巻き戻していない。** 本 issue が足したのは口座種別による分岐だけである。
        // 信用（信用買い・空売り）は同一保証金での同日無制限回転が可能なため現物に限る（§5「差金決済防止」）。
        // 判定の単一情報源は AccountTypePolicy.AppliesSameDayReentry である。
        //
        // 照合は（銘柄コード, 市場）で行う。禁止銘柄判定と対称にし、別市場の
        // 同一コード（例: 日本株 6902 と同名の米国ティッカー）の誤拒否を防ぐ（Issue #26）。
        if (isEntry
            && settings.Guard.PreventSameDayReentry
            && AccountTypePolicy.AppliesSameDayReentry(intent.Market, effectiveProductType, accountType)
            && snapshot.SymbolsTradedToday.Contains((intent.Symbol, intent.Market)))
        {
            reasons.Add(RejectionReason.SameDayReentry);
        }

        // FR-19, #375, ADR-0021 決定4-2/決定4-3: **現金口座でのみ**加わる 2 統制。
        // 判定は照会結果（accountType）で行う。信用口座・口座種別不明では評価しない
        // （不明は BrokerAccountTypeUnverified が既に新規建てを止めている）。
        if (isEntry && accountType == AccountType.Cash)
        {
            // 決定4-2（最重要）: **未決済資金による買付を発注前に拒否する。**
            // GFV は違反しても即座には拒否されず（ブローカーが後から判定する）、3 回目で口座が 90 日間制限される。
            // **不可逆な結果に対して事後の検知は統制にならない**（ADR-0021 107 行）ため、発注前に自前で防ぐ以外に手段がない。
            //
            // 判定は**当日の新規建て累計 ＋ 本注文**で行う。1 件ずつの比較では、決済済み資金の範囲内の注文を
            // 複数回通して累計で超過できる（Issue #27 と同じ穴）。DailyOrderedAmount は新規建てのみを積んでおり
            // （IADR-0130 決定4）、現金口座では新規建て＝現物買付であるため、そのまま「当日の現金の払い出し」になる。
            // ブローカーが約定時点で現金を引き落としていれば二重計上になり得るが、**過剰拘束（発注が止まる）側**である。
            //
            // **決済済み資金が供給されない（null）ときも拒否する。** 「残高が分からないから通す」は統制にならない。
            // moomoo API には決済済み資金の専用フィールドが存在しない（IADR-0153 決定4 の実測）ため、
            // 現時点で本値の供給元は無く、現金口座の買付は常に止まる（安全側）。
            //
            // **判定式は AccountTypePolicy.ExceedsSettledCash が単一情報源である**（#425 / ADR-0025 決定2 /
            // IADR-0165 決定1）。同じ述語を事後の計数（GoodFaithViolationDetection）も呼ぶ——
            // 計数の対象は「本ガードが拒否しようとする事象」と同じでなければならない。
            if (intent.Side == TradeSide.Buy
                && AccountTypePolicy.ExceedsSettledCash(
                    observedAccount!.SettledCashInBase, snapshot.DailyOrderedAmount + intent.NotionalInBase))
            {
                reasons.Add(RejectionReason.CashAccountSettlementHold);
            }

            // 決定4-3, #425, ADR-0025 決定2: GFV 発生回数が停止基準（2 件）に達していれば新規建てを止める
            // （3 回目の手前で止める）。**未供給（null）でも止める**——2 件に達していないことを確認できないためである。
            //
            // 件数は**自前計数**である（snapshot.GoodFaithViolations）。ブローカー照会（Account）には載せない——
            // 同じ欄に載せると「ブローカーの GFV カウンタの写し」と読まれるが、**自前で数えられるのは
            // 自らのガードをすり抜けた買付だけであり、両者が一致する保証はない**（ADR-0025 §理由）。
            if (AccountTypePolicy.BlocksForGoodFaithViolations(snapshot.GoodFaithViolations))
            {
                reasons.Add(RejectionReason.GoodFaithViolationLimitReached);
            }
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
        //
        // #375, ADR-0021 決定5: **現金口座では ADR-0016 の全決定が適用対象外**である。株を借りられないため
        // 空売り自体が成立せず、借株料・維持率・権利確定日といった空売り専用の拒否理由を返しても実態と食い違う。
        // これは「空売りが無効に設定されている」（ShortSellDisabled）とは**別の状態**であり、設定ではなく
        // **口座の能力**の問題である（同 ADR 99 行）。現金口座での空売り新規建ては ProductTypeDisabled で止まる。
        // **口座種別が不明なら評価する**——空売り統制はそれ自体がフェイルクローズであり、省く方が緩む側になる。
        if (isEntry
            && ShortSellEvaluator.IsShortEntry(intent)
            && AccountTypePolicy.AppliesShortSellControls(accountType))
        {
            var shortSellEnabled = ProductTypeResolver.IsEnabled(settings.Guard, accountType, ProductType.ShortSell);
            reasons.AddRange(ShortSellEvaluator.Evaluate(
                intent, shortSellEnabled, settings.ShortSell.Limits, snapshot.Capital, shortSellContext));

            // FR-10, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159 決定5: 強制買戻し由来の 30 日禁止は
            // **文脈が組めなくても単独で判定できる唯一の統制**である（供給されるのは期限という 1 つの日付だけであり、
            // 借株可否・維持率・エクスポージャのような未供給の値を発明する必要が無い）。
            // 借株照会の供給元が無い現況では shortSellContext が null であり、上の Evaluate は
            // BorrowUnavailable を立てて**そこで打ち切る**ため、禁止期間中であることが監査ログに残らない。
            // **二重計上しない**——文脈が組める日が来て両方から立っても理由は 1 件である。
            if (buyInBan is { } ban
                && BuyInBanPolicy.IsBanned(ban.Today, ban.BanUntil)
                && !reasons.Contains(RejectionReason.BuyInBanned))
            {
                reasons.Add(RejectionReason.BuyInBanned);
            }
        }

        return reasons.Count > 0
            ? OrderScreeningResult.Reject(reasons)
            : OrderScreeningResult.Approve(intent.Quantity);
    }
}
