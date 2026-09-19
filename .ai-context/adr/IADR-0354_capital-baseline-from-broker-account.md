---
title: IADR-0354 統制上限の基準資金はブローカーの口座照会（TrdGetFunds の資産純値）に由来させ、前取引日の最後の観測を日次で latch し、照会できないときは新規建てを拒否する
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-10, FR-11, FR-19, FR-20, UC-06, SC-02, SC-03, ADR-0009, ADR-0016, ADR-0021, ADR-0025, ADR-0028, ADR-0041, IADR-0005, IADR-0008, ADR-0003, IADR-0018, IADR-0036, IADR-0066, IADR-0107, IADR-0130, IADR-0134, IADR-0136, IADR-0146, IADR-0148, IADR-0151, IADR-0152, IADR-0153, IADR-0162, IADR-0163, IADR-0165, IADR-0246, IADR-0327, IADR-0346, IADR-0350]
author: claude (Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (決定 2・§結果 フォローアップ 3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 注記)
---

# IADR-0354: 基準資金をブローカーの口座照会に由来させる

- 状態: Accepted
- 日付: 2026-09-19
- 決定者: claude（起票 #869。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: **FR-10**（統制値の判定基準）・FR-19（口座照会）・FR-20（段階）・FR-04（サイジング）・
  FR-11（監査）・SC-02 / SC-03（表示）・UC-06
- 計画の裁定: **ADR-0041 決定 2**（環流 planning#642・起案 PR planning#643）。
  「[05_trading-assumptions] の注記が定める『判定に用いる equity は前営業日終値時点の USD 評価額』の**供給元を、
  ブローカーの口座照会と確定する**。台帳（初期資金 ＋ 実現損益）から導くのをやめる」
  「**照会できないときは新規建てを拒否する（fail-closed）**」「**手仕舞い・損切りは止めない**」「**鮮度は日次でよい**」。
- 対象 Issue: #869
- 関連する実装仕様書: [20260919_869_capital-baseline-from-broker-account](../specs/20260919_869_capital-baseline-from-broker-account.md)
- 関連 IADR: [IADR-0130](IADR-0130_equity-ratio-risk-limits.md)（統制上限の equity 比化。**決定 2 の「equity＝前営業日終値時点・
  当日中は不変」を覆さない。供給元だけを替える**）、[IADR-0153](IADR-0153_broker-account-type-supply-and-fail-closed.md)
  （口座照会の供給経路と fail-closed の作法。**同じ観測イベントに相乗りする**）、
  [IADR-0066](IADR-0066_market-valuation-supply-and-gate.md)（DD のピーク再計算。**変えない**）、
  [IADR-0018](IADR-0018_portfolio-ledger-projection.md) / [IADR-0036](IADR-0036_unrealized-pnl-valuation.md)（台帳射影）、
  [IADR-0350](IADR-0350_owner-approved-ledger-drift-adoption.md)（乖離の取り込み。本 IADR が「基準資金の側から塞ぐ」相手）

## 計画が定めていないこと（本 IADR が決める範囲）

ADR-0041 §結果 フォローアップ 3 が明記している。

> 🔴 **口座照会の供給元・鮮度の検査・照会できないときの通知を定める。本 ADR は「口座照会へ寄せる」
> 「照会できなければ新規建てを拒否する」までを定め、経路の設計は定めない。**

したがって **①供給元の具体（どのフィールドか）②「前営業日終値時点」への写像 ③鮮度 ④通知** の 4 点が実装判断である。

## 🔴 実測

### 実測 1 — 計画と実装の食い違いは実在した

| 出典 | 値 |
| --- | --- |
| 計画 FR-10 本文の括弧書き | **判定に用いる equity は前営業日終値時点の USD 評価額** |
| 計画 05_trading-assumptions §5 注記 | 同上（日中の評価損益で上限を動かさない理由つき） |
| 実装（`PortfolioProjection.Project`・本 PR 前） | `Capital = initialCapital + realizedBeforeToday` |

**前営業日終値時点の評価額は含み損益を含む。実装は含まない。** ブローカーの口座残高とも結ばれていなかった。

### 実測 2 — moomoo API に「口座の評価額」は**実在する**（決済済み資金とは違う）

[IADR-0153](IADR-0153_broker-account-type-supply-and-fail-closed.md) 決定 4 は **決済済み資金**（settled cash）の
専用フィールドが `TrdCommon.Funds` の 42 プロパティに**無い**ことを実測した。**本件は別のフィールドである。**

- SDK（`moomoo-api` 10.8.6808 / `MMAPI4Net.dll`）の走査で **`TotalAssets`（資産純値）・`SecuritiesAssets`・`MarketVal`・
  `Cash`・`Currency`・`Power`・`AvlWithdrawalCash`** の各シンボルと、要求側の **`RefreshCache` / `Currency_USD`** を確認した。
- 照会 API `TrdGetFunds` は SDK・接続シームともに存在し、**応答コールバック `OnReply_GetFunds` が no-op のまま
  放置されていた**（未使用のため）。本 PR で `Complete` へ繋いだ。

### 実測 3 — 「照会できないとき止める」は既に 2 か所にある

`RejectionReason.BrokerAccountTypeUnverified`（ADR-0021 決定 3・IADR-0153）と
`GoodFaithViolationLimitReached` の未供給時拒否（ADR-0025 決定 2・IADR-0165）。
**いずれも専用の通知経路を持たず、`OrderRejected` → `NotificationService`（Discord）に理由名として載る。**

## 決定

### 決定 1: 供給元は `TrdGetFunds` の `Funds.TotalAssets`（USD）とする

- `IMoomooTradeClient.GetAccountEquityInBaseAsync` を新設し、`Currency=Currency_USD` / `RefreshCache=true` で照会する。
  **応答の通貨も確かめる**（`Funds.Currency` が USD でなければ `null`）——要求だけを信じると JPY 建ての数値を
  USD の統制上限の分母に据えることになり、桁が 2 つずれる。
- 値は `BrokerAccountState.EquityInBase` に載せ、**既存の口座観測（`BrokerAccountObserved`・既定 5 分の巡回）に相乗りする**。
  新しい巡回・新しいイベントを作らない（IADR-0153 の経路をそのまま使う）。
- 🔴 **買付余力（`Power`）で代替しない。** 信用で自己資金の 2 倍になり、**統制が黙って 2 倍に緩む**
  （ADR-0016 決定 6「buying power を基準にしない」と同じ論拠）。出金可能額（`AvlWithdrawalCash`）も別概念である。
- 🔴 **評価額が取れなくても口座種別は捨てない。** 欄を分けてあり、捨てると口座種別依存の統制（ADR-0021 決定 4 の 5 統制）
  まで一緒に沈黙する。

### 決定 2: 「前営業日終値時点」への写像は**取引日ごとの latch** とする（近似であることを明示する）

- `account_equity_days(trading_day PK, equity_in_base, observed_at)` に**その取引日で最後に観測できた評価額**を持つ。
- 判定に使う基準資金は **`trading_day < 当日` のうち最新の行**。取引日は**米国東部時間の暦日**で数える
  （`TradingDay.Of(instant, Market.UnitedStates)`）——「前営業日終値」は市場の現地時刻に属する概念であり、
  JST で数えると米国セッションの途中で基準が入れ替わる（#249 / IADR-0246 が日次統制について是正したのと同じ誤り）。
- 🔴 **近似であり、値を騙らない。** 持っているのは「前取引日の**最後の観測**時点の評価額」であって
  「前営業日**終値**時点の評価額」そのものではない。
  - 巡回が生きていれば最後の観測は当該取引日の 23:5x ET であり、**終値（16:00 ET）以後・翌セッション開始前**のため
    ブローカーの評価は終値ベースで固定されており、値は一致する。
  - **プロセスが夕方に落ちていた場合だけ**、最後の観測がセッション中となり、その時点の評価額（日中の含み損益を含む）になる。
    この誤差は**日をまたいで固定される**（当日中は不変という性質は保たれる）。
  - より厳密にするには「終値後の時刻窓でのみ latch する」設計が要るが、**窓の間にプロセスが落ちていると
    基準資金が丸一日供給されない**（fail-closed で取引が止まる）。可用性と精度の釣り合いで latch を採る。
- **当日の行は判定に使わない。** 計画 §5 注記が「日中の評価損益で上限を動かすと、含み益で上限が緩み含み損で締まるという
  逆方向の作用が起きる」として明示的に禁じている。
- 🔴 **永続（EF・Risk 専有 DB）とする。** 口座種別の観測（非永続・30 分失効。IADR-0153 決定 3）と設計が違うのは、
  あちらが「いまの値」でこちらが「**昨日の値**」だからである。非永続にすると再起動のたびに前日の行が消え、
  **次の取引日境界まで新規建てが丸一日止まる**。

### 決定 3: 台帳から基準資金を導く経路は**型から消す**

- `PortfolioState.Capital` を廃止し、DD 専用の `LedgerEquity`（初期資金 ＋ 累計実現損益 ＋ 含み）へ置き換えた。
  `PortfolioSnapshot.Capital` は `ICapitalBaselineStore` だけから供給される。
- 🔴 **「使わない」ではなく「作れない」にした。** 同名の項目が射影側に残っていると、次に触る者が黙って結び直せる
  （受け入れ基準「台帳から導く経路が無い」を構造で満たす）。
- **ドローダウン（IADR-0066）は変えない。** DD は比率上限の**分母**ではなく「ピークと現在エクイティの比」であり、
  ADR-0041 決定 2 の射程外である。`LedgerEquity` はその入力としてのみ残る。
- **維持率（`MaintenanceMarginSnapshot.NetEquityUsd`）も変えない。** **日中に動くこと自体が判定対象**の別量であり、
  基準資金と 1 つにまとめると日中に維持率が落ちても判定が動かない（IADR-0133 が既に分離の理由を書いている）。

### 決定 4: 鮮度は「当日より前の取引日」＋「経過 4 日以内」とする

- `Risk:CapitalBaseline:MaxAge`（既定 **4 日**）。超えたら `null`＝照会できていない扱い。
- **4 日の根拠**: 計画は「鮮度は日次でよい」とだけ定める。3 連休を挟むと金曜の観測を火曜に使うことになり、
  経過は最大およそ **3.1 日**である。4 日はこれを通し、**巡回が 1 営業週にわたり死んでいる状態は通さない**。
- 🔴 **建玉観測の 60 分（IADR-0350 決定 1）・口座種別の 30 分（IADR-0153 決定 3）とは揃えない。**
  計画が「一方は『いまの建玉』、他方は『前営業日終値の残高』であり別の量である」と明記している。
- **値そのものを構成から注入する口は作らない**（`SimulatorProfileOptions` が上限値を構成から与えない規律と同じ。IADR-0108 決定 2/5）。
  0 以下の設定は既定へ倒す（書き損じで統制が常時 fail-closed になり運用が止まるのを避ける）。

### 決定 5: 通知は**専用経路を新設せず**、既存の拒否記録に載せる

- 新しい拒否理由 `RejectionReason.CapitalBaselineUnavailable`（**序数 29・末尾追加**。IADR-0134 決定 2）を足す。
  これは `OrderRejected` → `NotificationService`（Discord）で**理由名として通知される既存の経路**に自動で載る。
  クラス分類は **B**（取引を止めている状態そのものの記録。`BrokerAccountTypeUnverified` と同じ）。
- 🔴 **専用通知を作らないのは、同型の観測不能（口座種別未確認・情報収集の縮退）が専用通知を持たないからである。**
  ここだけ非対称にすると、ADR-0041 が「新しい規律を作らない」と言った趣旨に反する。
- あわせて `/status`（SC-03）と SC-02 の実額併記に**未供給**を表示する（05_screens「供給が無い値の表示規約」・IADR-0162）。
  **0 で埋めない**——「上限 0」と「上限が分からない」は別の事実である（IADR-0148 の規律）。

### 決定 6: 未供給のとき、比率上限の拒否理由は**立てない**（ただし equity 以外の軸を持つ統制は最も厳しい側で評価する）

- `PerOrderAmountExceeded` / `DailyOrderAmountExceeded` / `DailyLossLimitReached` / `StageCapitalCapExceeded` は
  **評価しない**。分母が無いのに「超過した」と記録すると、**監査ログが起きていない事実を主張する**。
- `StageProductPolicy`（空売り実弾解禁の $5,000）と `ShortSellEvaluator`（1 銘柄 10%）には **0 を渡す**。
  これらは equity 以外の軸（段階 × 商品種別・借株可否・株価下限）が主であり、評価を飛ばすと
  equity と無関係の違反まで記録から落ちる。0 は「解禁条件を満たさない」＝最も厳しい側である。

## 理由

- **供給元を実態の側へ移すと、ずれが構造的に生じない**（ADR-0041 §理由）。台帳から導く限り、システム外の売買のたびに
  基準資金がずれ、そのずれを直す手段がまた要る。
- **fail-closed を新設しないのは、計画が既に同じ形を 2 か所で持っているからである**（ADR-0016 決定 3・ADR-0028）。
- **latch を日次にするのは、計画が禁じた「日中に上限が動く」作用を避けるためである。** ブローカーの `TotalAssets` は
  照会した瞬間の値であり、そのまま分母にすると含み益で上限が緩む。
- **型から消すのは、規約より構造のほうが強いからである。** 「台帳から導かない」を文章で守らせると、次の改修で静かに戻る。

## 結果

- 良い影響:
  - 基準資金が**含み損益を含む実態の値**になり、計画の定義（前営業日終値時点の USD 評価額）と一致した。
  - システム外の売買による台帳の乖離が、**翌営業日に基準資金へ自動で反映される**（ADR-0041 決定 1 の副作用を塞ぐ）。
  - 台帳から基準資金を導くコードが**コンパイル単位に存在しない**。
- 悪い影響 / トレードオフ:
  - 🔴 **口座照会が通らないと新規建てが止まる**（照会の可用性が統制の可用性になる。ADR-0041 が受け入れた代償）。
  - 🔴 **latch は近似である**（決定 2）。プロセスが前取引日の夕方に落ちていた場合、基準資金は日中の評価額になる。
  - 🔴 **配備直後の 1 取引日は基準資金が無い**（前取引日の行がまだ無いため新規建てが止まる）。
    運用は巡回が 1 取引日ぶん回るのを待つ。**手仕舞い・損切りは動く。**
  - **当日のうちは日次損失上限の判定が緩いまま**（ADR-0041 決定 1 が受け入れた既知の緩み。本 IADR は塞がない）。
- フォローアップ:
  1. **実弾 / SIMULATE で `TrdGetFunds` の応答形を 1 回観測する**（`TotalAssets` の有無・`Currency` の値）。
     本 PR は SIMULATE 固定（IADR-0016）であり、**live 実測は行っていない**。応答に値が無ければ基準資金は未供給＝
     新規建てが止まる（安全側）ため、配備後の最初の巡回でログ（`口座照会の応答に資産純値…`）を確認する。
  2. 決定 2 の近似（最後の観測 vs 終値）を実運用で測り、ずれが大きければ「終値後の時刻窓」を足すかを判断する。
  3. 報告書（FR-06 / FR-16）へ基準資金と観測時刻を出すかは定めない（現状、報告書は equity を 1 箇所も読んでいない）。
