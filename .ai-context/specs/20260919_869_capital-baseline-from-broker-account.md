---
title: 基準資金（equity）をブローカーの口座照会に由来させ、照会できないときは新規建てを拒否する
type: spec
status: accepted
related_ids: [FR-10, FR-19, FR-20, FR-04, FR-11, UC-06, SC-02, SC-03, ADR-0041, ADR-0016, ADR-0021, ADR-0025, ADR-0028, ADR-0009, IADR-0018, IADR-0036, IADR-0066, IADR-0130, IADR-0136, IADR-0146, IADR-0151, IADR-0152, IADR-0153, IADR-0162, IADR-0163, IADR-0246, IADR-0346, IADR-0350, IADR-0354]
author: claude (Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (決定 2)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 本文の括弧書き)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 注記)
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md (決定 3 の fail-closed の形)
  - planning:projects/ai-stock-trading/07_adr/ADR-0028_gfv-violation-clearing-and-reconciliation.md (未供給時の fail-closed)
---

# 仕様書: 基準資金をブローカーの口座照会に由来させる（#869）

## 起点

- #869。計画リポジトリの裁定 **ADR-0041 決定 2**（planning#642 / planning PR #643 で確定）。
- **計画の定義（隣接クローン `../project-planning` で原文を確認した）**:
  - `02_requirements/01_requirements.md` FR-10 本文の括弧書き: 「**判定に用いる equity は前営業日終値時点の USD 評価額とする**」。
    さらに 2026-09-19 追記「**equity の供給元はブローカーの口座照会とする**（ADR-0041 決定 2）…**照会できないときは新規建てを拒否する**
    （fail-closed。ADR-0016 決定 3・ADR-0028 と同じ形）」。
  - `06_technical/05_trading-assumptions.md` §5 注記（159〜162 行）: 「**判定に用いる equity は前営業日終値時点の USD 評価額**とする。
    日中の評価損益で上限を動かすと、含み益で上限が緩み含み損で締まるという逆方向の作用が起きる」
    ＋ 2026-09-19 追加「**供給元はブローカーの口座照会とする**」「**鮮度は日次でよい**」「**手仕舞い（Close）と損切りは止めない**」。
  - `07_adr/ADR-0041` 決定 2 と §結果 フォローアップ 3: **口座照会の供給元・鮮度の検査・照会できないときの通知は計画が定めない。**
  - `07_adr/ADR-0016` 決定 3: 「借株料を事前照会できない場合は空売り自体を行わない」（fail-closed の前例）。
  - `07_adr/ADR-0028`（97 行）: 「**未供給時の fail-closed（回数が供給されないとき新規建てを拒否する）は改めない**」。
- **実装の現況（本 PR 前）**: `PortfolioProjection.Project` が `Capital = initialCapital + realizedBeforeToday`
  （＝初期資金 ＋ 当日より前の実現損益）を返し、`PortfolioSnapshot.Capital` としてすべての比率上限の分母になっていた。
  **含み損益を含まず、ブローカーの口座残高と結ばれていない。**
- 新規 IADR: **IADR-0354**（本作業の設計判断——供給元・近似・鮮度・通知）。

## 射程

| 含む | 含まない（理由） |
| --- | --- |
| 基準資金の供給元をブローカーの口座照会（moomoo `TrdGetFunds` の `TotalAssets`・USD）に確定する | 実弾口座（`TrdEnv=real`）での実測 —— 接続は SIMULATE 固定（IADR-0016）。**kubectl も実発注も行わない** |
| 台帳から基準資金を導く経路を**構造的に**無くす（`PortfolioState` から `Capital` を削る） | ドローダウンの現在エクイティ・ピーク（IADR-0066）—— **別量**であり ADR-0041 決定 2 の射程外（母集合 #7） |
| 照会できないときの新規建て拒否（`CapitalBaselineUnavailable`） | 維持率（`MaintenanceMarginSnapshot.NetEquityUsd`）—— **日中に動くことが判定対象**の別量（母集合 #8） |
| 比率で保持している各上限（1 注文 25% / 1 日 150% / 段階の総資金比 / 日次損失 2% / 空売り 10% / 実弾解禁 $5,000）を新しい基準資金から解決する | 当日のうちの日次損失上限の緩み（ADR-0041 §結果 フォローアップ 5 が「定めない」としたもの） |
| 鮮度の検査（日次・最大経過日数）と通知の経路（既存の `OrderRejected` 通知へ載せる） | 報告書への基準資金の掲載 —— 報告書は equity を 1 箇所も読んでいない（母集合 #9） |

## 母集合: equity（基準資金）を読んでいる箇所

**引き方**（テスト・`obj` を除外。issue 本文の列挙は使わず、自分で走査した）:

```
grep -rn "snapshot\.Capital|state\.Capital|context\.Capital|status\.Capital" --include=*.cs backend | grep -v "/Tests/"
grep -rn "\.capital\b" frontend/src --include=*.ts --include=*.tsx | grep -v "\.test\.|contract-fixtures|capitalCapRatio|capitalGains"
grep -rn "equity|Equity" --include=*.cs backend | grep -v "/Tests/"
```

走査の結果は **26 件（C#）＋ 4 件（TS）** であり、意味のある読み手に畳むと次の 10 件である。

| # | 読み手 | 何のために equity を読むか | 本 PR の影響 |
| --- | --- | --- | --- |
| 1 | `RiskEvaluator` 1 注文金額上限（`MaxOrderAmountFor`） | equity の 25% | **口座照会由来の基準資金から解決**。基準資金が無いときは評価せず `CapitalBaselineUnavailable` で拒否 |
| 2 | `RiskEvaluator` 1 日あたり発注金額上限（`MaxDailyOrderAmountFor`） | equity の 150%/日 | 同上 |
| 3 | `RiskEvaluator` 日次損失上限 | `dailyLoss <= -(equity × 2%)` | 同上 |
| 4 | `RiskEvaluator` 段階資金上限（`StageSettings.OrderableCapFor`） | 総資金比（Stage 2 ＝ 30%） | 同上 |
| 5 | `RiskEvaluator` 空売り統制（`ShortSellEvaluator`・1 銘柄 10%） | equity の 10% | 🔴 **［2026-09-19 是正 / #874 の監査］`null` をそのまま渡し、1 銘柄あたり上限だけを判定しない**（0 だと全注文で `ShortExposureExceeded` が立ち、起きていない事実を監査ログへ書く）。equity 非依存の規則はそのまま効く |
| 6 | `RiskEvaluator` 段階別商品種別（`StageProductPolicy`・空売り実弾解禁 $5,000） | equity ≥ $5,000 | 同上（0 は解禁条件を満たさない側） |
| 7 | `PortfolioValuation.DrawdownRatio` / `EquityHighWaterMark`（IADR-0066） | 台帳由来のエクイティ系列のピークと現在値 | 🔴 **変えない。** 比率上限の分母ではなく「ピークと現在の比」であり ADR-0041 決定 2 の射程外。ただし `PortfolioState.Capital` を廃したため、入力は新設の `LedgerEquity`（台帳由来・**統制の基準ではない**と型の上で明示）から採る |
| 8 | `MaintenanceMarginSnapshot.NetEquityUsd`（維持率） | 日中の純資産（維持率の分子） | 変えない。**日中に動くこと自体が判定対象**であり基準資金と別物（同型の注意書きが既に型にある）。供給元は未実装のまま |
| 9 | 報告書（`ReportService`） | —— | **該当なし**（走査で 0 件。報告書は equity を読んでいない） |
| 10 | 表示・サイジング（`RiskStatusService` / `SizingContextService` → `TradeDecisionService` / SC-02 / SC-03） | 実額の併記・サイジングの分母 | **`decimal?` にして「未供給」を表す**（05_screens の未供給表示規約・IADR-0162）。サイジングは残枠 null → 数量 0 ＝ 見送り |

**除外した検索ヒットとその理由**: `BacktestService`（`BacktestConfig.InitialCapital` はバックテストの初期現金であり実運用の統制ではない）、
`InformationCollectionService`（FINRA の `equity` は銘柄種別の語）、`SimulatorTradingDefaults` / `TradingDefaults.InitialCapital`
（**台帳射影の起点**であり、本 PR 後は DD 系列の起点としてのみ残る）。

## 設計

### 1. 供給元: moomoo の口座照会（`TrdGetFunds`）

- `IMoomooTradeClient.GetAccountEquityInBaseAsync` を新設し、`TrdGetFunds`（`Currency=Currency_USD` / `RefreshCache=true`）の
  `Funds.TotalAssets`（**資産純値**。現金 ＋ 建玉評価額 − 負債。**含み損益を含む**）を返す。応答に `TotalAssets` が無ければ `null`。
  🔴 **［2026-09-19 追記 / #874 の監査］応答が USD と名乗っていなければ採らない（通貨の欠落を含む）。**
  `Funds.currency` は protobuf の **optional**（required は `power` / `totalAssets` / `cash` / `marketVal` /
  `frozenCash` / `debtCash` / `avlWithdrawalCash` の 7 つ）であり、初稿の `HasCurrency && != USD` は
  **通貨未設定の応答を検証せず素通りさせていた**。3 通り（USD / 別通貨 / 欠落）を T-10-514 が固定する。
- `BrokerAccountState` に `EquityInBase`（`decimal?`）を足す。**既存の `SettledCashInBase` とは別物である**
  （あちらは GFV ガードの分母＝未決済を含まない現金で、moomoo に該当フィールドが無い〔IADR-0153 決定 4〕。
  こちらは口座全体の評価額であり、**専用フィールドが実在する**）。
- 観測は既存の `BrokerAvailabilityProbeService`（既定 5 分）が `BrokerAccountObserved` に載せて流す。**新しい巡回を作らない。**

### 2. 「前営業日終値時点」への写像（近似の所在）

`TotalAssets` は**照会した瞬間**の評価額であり、そのまま使うと日中の含み損益で上限が動く（計画 §5 注記が明示的に禁じた作用）。
そこで**日次の latch** を置く。

- `account_equity_days(trading_day PK, equity_in_base, observed_at)` に**その取引日の最後の観測**を持つ（upsert・逆行する観測は無視）。
- 判定に使う基準資金 ＝ **`trading_day < 当日` のうち最新の行**。取引日は米国東部時間の暦日（`TradingDay.Of(instant, Market.UnitedStates)`）。
- **近似である点**: 「前営業日**終値時点**」ではなく「**前取引日の最後の観測時点**」である。巡回が生きていれば最後の観測は
  当該取引日の 23:5x ET であり、**終値（16:00 ET）以後・翌セッション開始前**のため評価額は終値ベースと一致する。
  プロセスが夕方に落ちていた等で最後の観測がセッション中だった場合のみ、その時点の評価額になる（**値を騙らないため IADR-0354 に記録する**）。
- **永続である理由**: 非永続にすると再起動のたびに「前日の行」が消え、**翌日の取引日境界まで新規建てが丸一日止まる**。
  口座種別の観測（非永続・30 分失効）と設計が違うのは「いまの値」と「昨日の値」の違いである。
- 🔴 **［2026-09-19 追記 / #874 の監査］暦日で数えることの帰結**: 巡回に市場カレンダーのゲートが無いため**土日にも行が作られる**。
  **週末に口座照会が 1 回でも成功していれば月曜の寄り付きから供給され、成功していなければ月曜は一日中止まる。**
  月曜の基準は「金曜の終値」ではなく「**日曜の観測**」の行になる。埋め合わせ手順は
  [基準資金の供給が無いときの Runbook](../../docs/operations/capital-baseline-seed-runbook.md)。

### 3. 鮮度（計画が定めていないため実装判断）

- 基準資金の行の `observed_at` が **`Risk:CapitalBaseline:MaxAge`（既定 4 日）** より古ければ `null`（＝照会できていない）。
- 4 日の根拠: 計画は「鮮度は日次でよい」と定める。3 連休を挟むと金曜の観測を火曜に使うことになり経過は最大でおよそ 3.1 日である。
  4 日はこれを通し、**巡回が 1 営業週にわたり死んでいる状態は通さない**。
- 建玉観測の 60 分（IADR-0350 決定 1）・口座種別の 30 分（IADR-0153 決定 3）とは**揃えない**（計画が「別の量である」と明記）。

### 4. fail-closed（ADR-0016 決定 3・ADR-0028 と同じ形）

- `PortfolioSnapshot.Capital` を `decimal?` にする。**`null` ＝ 口座を照会できていない。**
- `RiskEvaluator`: `isEntry && Capital is null` → `RejectionReason.CapitalBaselineUnavailable`（序数 29・末尾追加。IADR-0134 決定 2）。
- 🔴 **手仕舞い（Close）・損切りは止めない** —— 既存の `isEntry` 短絡に乗せる（新しい作法を作らない）。
- 台帳からの導出経路は**型から消す**: `PortfolioState.Capital` を廃し、DD 専用の `LedgerEquity` に置き換える。
  これにより「台帳から基準資金を導く」コードはコンパイル単位に存在しなくなる。

### 5. 通知（計画が定めていないため実装判断）

- **専用の通知経路を新設しない。** 拒否は既存の `OrderRejected` → `NotificationService`（Discord）で理由名が出る。
  同型の観測不能（`BrokerAccountTypeUnverified` / `InformationSourceDegraded`）も専用通知を持たず、
  **ここだけ非対称にすると「新しい規律を作らない」に反する。**
- あわせて `/status`（SC-03）と SC-02 の実額併記で「未供給」を表示する（05_screens の未供給表示規約・IADR-0162）。

## テスト（`docs/tests/FR-10_risk-controls-tests.md` に T-10-508 から追記）

| ID | 何を固定するか |
| --- | --- |
| T-10-508 | 基準資金が口座照会由来の値で解決される（1 注文上限＝その 25%） |
| T-10-509 | 照会できないとき新規建てが拒否される（`CapitalBaselineUnavailable`） |
| T-10-510 | 同じ状況で手仕舞い・損切りは通る |
| T-10-511 | 鮮度切れ（既定 4 日超）は照会できていないのと同じ扱い／境界ちょうどは通る |
| T-10-512 | 当日中に届いた観測は基準資金を動かさない（前取引日の行だけを見る） |
| T-10-513 | 台帳の実現損益は基準資金を動かさない（台帳由来の経路が無いことの回帰） |
| T-10-514 | 口座照会の応答が **USD と名乗るときだけ**評価額を採る（別通貨・**通貨欠落**は未供給） |

## 受け入れ基準（#869 より）

1. 基準資金がブローカーの口座照会に由来する。台帳から導く経路が無い。
2. 口座を照会できないとき新規建てが拒否される（手仕舞い・損切りは通る）。
3. 比率で保持している各上限が、新しい基準資金から解決される。
4. 既存の統制テストが定義の変更に合わせて更新されている（期待値を変える理由を PR 本文に書く）。
