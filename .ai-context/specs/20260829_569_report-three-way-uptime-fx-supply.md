---
title: 報告書の三者比較・OpenD 稼働率・為替差損益の供給経路（#569）
type: spec
status: review
related_ids: [FR-06, FR-15, FR-16, FR-20, UC-05, UC-06, ADR-0008, ADR-0022, IADR-0107, IADR-0140, IADR-0142, IADR-0149, IADR-0150, IADR-0152, IADR-0250, IADR-0251, IADR-0269, IADR-0271]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: 報告書の三者比較・OpenD 稼働率・為替差損益の供給経路（#569）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-06（報告書の階層管理）・FR-16（**数値はコード集計であり LLM に計算させない**）・FR-15（バックテスト）・FR-20（段階ゲート）
- ユースケース（UC）: UC-05（日報・月報）・UC-06（統制の可視化）
- 関連 ADR: ADR-0008（段階ゲート）・ADR-0022（為替レート源）
- 関連 IADR: [IADR-0250](../adr/IADR-0250_report-supply-seam-nullable-view-properties.md)（未供給と 0 を潰さない継ぎ目）・
  [IADR-0251](../adr/IADR-0251_report-numeric-aggregation-outside-llm-context.md)（集計は Domain の純関数・散文へ渡さない）・
  [IADR-0149](../adr/IADR-0149_stage1-trade-count-supply.md) 決定1（**実際に発注したアダプタの発注先**を使う）・
  [IADR-0150](../adr/IADR-0150_stage1-uptime-observation-and-session-hypotheses.md)（稼働分数の 2 仮説）・
  [IADR-0107](../adr/IADR-0107_base-currency-conversion.md) / [IADR-0152](../adr/IADR-0152_usd-base-currency-migration.md)（基準通貨 USD）
- 計画書リンク: `04_report-templates.md`（fixed）月報 §5 / §6.2・日報 §1・§数値の定義、`06_daytrading-review.md` §4.2

## 目的・背景

#338 で描画と集計（純関数）は実装済みだが、次の 3 項目は**供給側が存在しない**ため
報告書に常に「照会できませんでした / 供給されていません」と出る（IADR-0250 のフォローアップ 3 件）。

1. 三者比較（月報 §5）
2. OpenD 稼働率（日報 §1 の稼働率行・月報 §6.2 の分布）
3. 為替差損益（日報 §1・月報 §1 の独立行）

本作業は 1 と 2 の供給経路を作り、3 は**達成不能である根拠を確定して記録する**。

## 母集合の引き直し（規則 9・10。軸ごとの件数と除外理由）

> 規則 5「軸を 1 本で終わらせない」に従い 8 軸で引いた。走査は**パスの除外だけ**で行い
> （規則 3・4）、`--include`・行フィルタで絞っていない。数は**本仕様書を書く前**に取り、
> 本仕様書自身が母集合へ入る軸は自己参照を引き算して示す（規則 8）。

| # | 軸（検索語） | 生の件数（`git grep -ln`・追跡下のみ） | 採用 | 除外と理由 |
| --- | --- | --- | --- | --- |
| 1 | 未供給描画の**誤りの側**の文言 `供給されていません` / `照会できませんでした` | 34 ファイル（`backend` 21 ＋ `.ai-context` 11 ＋ `docs` 2） | `backend` 21（本文 3・ゴールデン 3・テスト 15） | `.ai-context` 11（**point-in-time の凍結記録**。後から表記を直すと当時の記述と食い違う）・`docs` 2（`blocked-tasks.md` と `functional/FR-10` は別項目の文言） |
| 2 | `Uptime` | 41 ファイル | Report 側 6・Risk 側 12 | Migrations の `*.Designer.cs` / `*ModelSnapshot.cs` 10（生成物。手で編集しない）・`OrderExecutionService/BrokerAvailabilityProbeOptions.cs`（観測の発火元であり本作業では触らない）・凍結記録 8・`docs` 2（実装後に追随する） |
| 3 | `ThreeWay` | 7 ファイル | 6（Domain 2・Draft 1・Renderer 1・テスト 2） | 凍結記録 1（`20260828_338`） |
| 4 | `FxTranslation` | 10 ファイル | **0**（3 は未達成のため 1 ファイルも触らない） | 全 10 —— 供給しないと決めたため（決定3） |
| 5 | `LedgerFill` | 32 ファイル | Risk 側 11・Report 側 2 | 凍結記録 13・`ReportAmountFormat.cs`（コメント内の参照のみ）・その他テスト |
| 6 | `PeriodTradeFill` | 24 ファイル | Report 側 15 | 凍結記録 9 |
| 7 | `AppendFill`（署名変更の追随先） | 47 箇所（`backend` のみ。本番 7・テスト 40） | 宣言 3 ＋ 呼び出し 1（`OrderExecutedLedgerHandler:32`）＝ 4。残る本番 3 はコメント内の言及 | テスト 40 箇所は**省略可能引数にすることで追随不要**（既定 `null`＝発注先不明＝どの段にも算入しない fail-safe） |
| 8 | 既存 EF マイグレーション（**1 バイトも変えない**対象） | `Migrations/*.cs` 21 本（実体 20 ＋ `ModelSnapshot` 1） | 追加のみ（新規 1 本） | 既存 20 本 ＋ ModelSnapshot は**追記される 1 箇所を除き**変更しない |

> **自己参照の引き算（規則 8）**: 上の数は `git grep`（**追跡下のファイルのみ**）で取っており、
> 本仕様書は走査時点で未追跡であるため母集合に入っていない。コミット後に軸 1〜4 は
> 本仕様書ぶん **+1** される（例: 軸 1 は 34 → 35）。**値はコミットで固定する。**

**除外の総則**: `.ai-context/adr/` と `.ai-context/specs/` は凍結記録であり、
本作業の是正対象に含めない（`traceability.repo.md` の除外規定と同じ理由）。

## 3 項目の達成可否（先に結論を置く）

| 項目 | 判定 | 根拠 |
| --- | --- | --- |
| 1 三者比較 | **達成（バックテスト列を除く）** | SIMULATE / 実弾の列は台帳の約定から期間集計できる。**バックテスト列は供給元が 1 つも無い**（下記） |
| 2 OpenD 稼働率 | **達成** | 権威源 `stage1_session_uptime` は永続化済み。期間照会 API を足せば引ける |
| 3 為替差損益 | **未達成（構造的に不能）** | 認識時・期末の **JPY/USD レート**がどこにも記録されておらず、照会経路も無い（下記） |

### 3 が達成できない根拠（ファイル:行）

- `backend/Services/ReportService/Domain/FxTranslationSummary.cs:19-22` —— 集計の入力
  `FxTranslationEntry(AmountBase, RateAtRecognition, RateAtPeriodEnd)` は
  「**基準通貨（USD）建ての金額**を、**認識時のレート**と**期末のレート**の両方で**円換算**」する。
  すなわち必要なレートは **1 USD あたりの円**である。
- `backend/Shared/AiStockTrading.Shared.Contracts/Trading/Currency.cs:23` —— 基準通貨は **USD**、
  表示通貨は JPY（同ファイル冒頭のコメント）。為替差損益は **USD 建て金額の円換算**で生じる。
- 🔴 **台帳は「換算前の外貨額と換算レート」を実は保持している。** issue の前提は文言としては誤りである:
  - `backend/Services/RiskManagementService/Features/RiskManagement/LedgerFill.cs:19-24` ——
    `Price` は**銘柄の市場の通貨（ローカル通貨）**、`FxRateToBase` は**適用した換算レート**。
  - `backend/Services/RiskManagementService/Infrastructure/Persistence/PersistenceRows.cs:107-111` ——
    `ApprovedOrderRow.FxRateToBase` として**永続化済み**（2026-07-27 のマイグレーション `AddApprovedOrderFxRate`）。
  - 落ちているのは `backend/Services/ReportService/Infrastructure/ExternalServices/HttpPeriodFillSource.cs:70-72`
    が `Price * FxRateToBase` を掛けてから `PeriodTradeFill` へ入れている点だけであり、**schema 変更では直らない**。
- 🔴 **本当に無いのは JPY/USD レートである。**
  - `FxRateToBase` は **ローカル通貨 → USD** のレートであり、
    `backend/Shared/AiStockTrading.Shared.Contracts/Ports/IFxRateSource.cs:20-24` の契約により
    **基準通貨（米国株）では外部へ問い合わせず必ず 1 を返す**。為替差損益を生むのはまさにその
    USD 建て金額であり、その認識時の円レートは**どこにも記録されない**。
  - 期末レートの照会経路も無い: `TradeDecisionService` は HTTP エンドポイントを 1 つも公開していない
    （`grep -n "MapGet\|MapPost" backend/Services/TradeDecisionService/**` が 0 件）。
  - 監査台帳から復元することもできない: `backend/Shared/AiStockTrading.Shared.Contracts/Events/FxRateSourceUsed.cs:20-25`
    は **どの源を使ったか**だけを持ち、**レートの値を持たない**。
- したがって供給には **推定**が要る。issue が明記するとおり為替差損益は**税務にも効く数値**であり、
  推定値を実績として出さない。**本作業では供給しない**（IADR-0271 決定4）。

## 決定（実装方針）

### 決定1: OpenD 稼働率は `stage1_session_uptime` を期間照会する新エンドポイントで供給する

- Risk: `IStage1TradingDayObservationStore.GetSessionUptimesBetween(from, to)` を足し、
  `GET /risk-controls/session-uptime?from&to`（**OwnerOrService**・IADR-0051 と同型）で返す。
  `from`・`to` の省略・逆順は **400**（`/buy-in-inferences` と同じ向き。黙って空を返すと「稼働率 0%」として載り得る）。
- 稼働率は Domain の純関数 `Stage1SessionHypotheses.UptimeRatio(uptime)` が返す
  **2 仮説の最小値**とする。`ratio >= 0.50` ⟺ `MeetsUptimeThreshold(uptime)` が
  **恒等に成り立つ**ため、報告書側の `OpenDUptimeAggregator.IsCounted(ratio)` と権威源の判定が食い違わない。
- 1 取引日に複数の発注先の行があるときは **OpenD を経由する発注先（`MoomooSimulate` / `MoomooReal`）**
  の最大値を採る。**内蔵 `paper` の行は除外する**——外部へ一度も発注せず OpenD を経由しないため、
  その稼働は「OpenD 稼働率」ではない。除外の結果その日の行が無くなれば**その日は現れない**（0% とは書かない）。
- 累計算入日数は `GetQualifiedTradingDayCount()`（**発注先の許可制まで含む権威判定**）をそのまま返す。

### 決定2: 三者比較は「台帳の約定 × 実際の発注先」と「現在の段階」から Domain の純関数で組む

- Risk 台帳へ **実際に発注したアダプタの発注先**（`OrderExecuted.Provider`）を記録する
  （`TradeFillRow.Provider`・**nullable**）。**EF マイグレーション 1 本を追加する。**
  - **`ApprovedOrderRow.Mode`（承認 Intent の発注先）で代用しない。** 実行アダプタの発注先は
    構成（`BrokerSelection.ToBrokerProvider()`・`backend/Services/OrderExecutionService/Infrastructure/ExternalServices/BrokerSelection.cs:62-67`）
    から解決され、**`intent.Mode` とは独立に決まる**。IADR-0149 決定1 が同じ理由で実際の発注先を選んでいる。
  - **既存行は `null`（発注先不明）のままにする。推定で埋めない。** `null` の約定は
    SIMULATE 列にも実弾列にも算入しない（どちらかへ寄せると比較の意味が壊れる）。
- Report: `PeriodTradeFill.Provider`（nullable）を足し、`HttpPeriodFillSource` が素通しする。
- Report: `GET /risk-controls/stage-gate` を **OwnerOrService** へ移し（読み取り専用・IADR-0051 と同型）、
  `IStageProgressSource` で現在段階を引く。
  - **段階が要るのは「空欄」と「0」を区別するためである。** 計画は「Stage 1 の間は実弾列、
    Stage 0 の間は SIMULATE 列も空欄」と定める。段階を知らずに「その期間に約定 0 件」を
    0 と書くと、**まだ到達していない段**を「走らせた結果 0 だった」と読ませてしまう。
- 集計は `ReportService.Domain.ThreeWayComparisonAggregator`（純関数）に閉じる。
  `ReportNarrativeContext` へは渡さない（FR-16 / IADR-0251）。
- **バックテスト列は常に `null`（該当なし＝まだ走らせていない）**とする。これは事実である:
  `backend/Services/BacktestService/Program.cs:27-29` が「**DB もメッセージバスも持たない**」
  「本番戦略（`IBacktestStrategy` 実装）はまだ存在せず、実行する対象が無い」と明記しており、
  `BacktestEvaluated`（`backend/Shared/AiStockTrading.Shared.Contracts/Events/BacktestEvaluated.cs:9-15`）は
  そもそも**勝率・平均損益・取引件数を運ばない**。

### 決定3: 為替差損益は供給しない（未達成として記録する）

前掲の根拠により、推定なしでは供給できない。**既存データの扱いは「未供給」で確定する**
（推定で埋めない）。必要な変更は「認識時点の **JPY/USD** レートを承認台帳へ persist すること」と
「**期末レートの照会経路**を作ること」の 2 点であり、いずれも発注経路と契約（`OrderIntent`）へ及ぶため
本 issue の射程（報告書の供給経路）を超える。IADR-0271 決定4 に残し、計画側へ環流する。

## 受け入れ基準

1. 月報 §6（三者比較）に SIMULATE / 実弾の実値が出る。段に到達していない列は「該当なし」、
   到達済みで約定 0 件の列は「0 件」と**書き分かれる**
2. 日報 §1 の稼働率行と月報 §5（稼働率分布）に実値が出る
3. 供給が落ちたとき（未注入・非 2xx・timeout・例外・不正応答）は「照会できませんでした」に戻る
   ——**否定形テストと対の肯定形**を対で置く
4. ゴールデン（`daily-supplied.md` / `monthly-supplied.md` ほか）が更新されている
5. 既存 EF マイグレーションは 1 バイトも変わらず、`has-pending-model-changes` が「モデルへの変更なし」
6. カバレッジ床 0.83 を割らない

## テスト方針（3 点セット＋出口の全文）

- **境界値**: 稼働率 50% ちょうど（算入）・49.99%（非算入）・分母 0（週末）・仮説の最小値が効くこと
- **プロパティベース**: `IsCounted(UptimeRatio(u)) == MeetsUptimeThreshold(u)` が全入力で成り立つこと
- **否定形＋対の肯定形**: 供給を落とすと「照会できませんでした」へ戻り、供給すると実値が出ること
- **出口の全文**: 結線は `ReportTemplateGoldenTests` の全文ゴールデンで固定する
  （「呼ばれたこと」と「出口へ出たこと」は別の事実。#563 / IADR-0269 決定1 の型）
