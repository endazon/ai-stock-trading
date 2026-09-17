---
title: 1 日あたりの発注金額上限・段階資金・保有建玉数へ、未約定で生きている承認済み新規建て注文を算入する
type: spec
status: accepted
related_ids: [FR-05, FR-10, FR-19, FR-20, ADR-0009, ADR-0016, ADR-0040, IADR-0005, IADR-0008, IADR-0018, IADR-0067, IADR-0113, IADR-0117, IADR-0130, IADR-0136, IADR-0163, IADR-0211, IADR-0246, IADR-0346]
author: endazon (with Claude Code)
created: 2026-09-18
updated: 2026-09-18
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「新規建ての発注代金の合計で判定し、手仕舞い〔決済〕注文は算入しない」)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 1 日あたりの発注金額上限・保有建玉数の上限・Stage 2「発注可能額をシステム側の統制で 30% に制限する」)
---

# 仕様書: 未約定の承認済み新規建て注文を統制の入力へ算入する（#829）

## 起点

- #829（bug）。2026-09-17（SIMULATE・S2）に AAPL の新規買い（1 回約 28.3 万 USD）が 5 分ごとに承認され続けた。
- 原因: `PortfolioProjection.Project` が `DailyOrderedAmount`・`InvestedCapital`・`OpenPositionCount` を**約定（`trade_fills`）だけ**から畳み込む。承認済みで未終端の新規建て注文を数えない。
- 計画との乖離: 計画 FR-10 は 1 日あたりの上限を「**新規建ての発注代金の合計**で判定」と定める（約定額ではない）。§5 は Stage 2 の資金上限を「**発注可能額**」と呼ぶ。

## 実測（稼働クラスタ・読み取りのみ・2026-09-18）

`risk_management_svc` で `approved_orders` × `order_activity`（DecisionId 左結合）を `ApprovedAt >= 2026-09-17T13:00Z`・`PositionEffect=Open` で引いた（生の出力は PR 本文）。

| 状態（`order_activity.Status`） | 終端 | 件数 | 承認代金（USD） | 約定数量 |
| --- | --- | --- | --- | --- |
| 0 Accepted | 未終端 | 2 | 566,511.16 | 0 |
| 2 Filled | 終端 | 5 | 1,133,433.52 | 3,384 |
| 4 Cancelled | 終端 | 10 | 2,831,778.05 | 0 |

- `order_activity` を持たない承認は **0 件**（射影は稼働中に欠けていない）。
- 取消 10 件は承認から数秒〜30 秒で終端化している（取消の到達は実測で成立している）。
- 未終端の 2 件（15:49Z・15:54Z）は**翌日も Accepted のまま**。終端イベントが届かない行が実在する →「当日の取引日」で窓を切らないと恒久に枠を食う。

## 方式（IADR-0346）

1. **新しいポート `IWorkingEntryOrderSource`**（`Features/RiskManagement/`）: 承認済み・新規建て（`PositionEffect.Open`）・`order_activity` が**終端でない**（`TerminalAt is null`。行が無い＝未到達も未終端に倒す）注文を、`approvedAtOrAfter` 以降について返す。EF 実装は `approved_orders` 左結合 `order_activity`。InMemory 実装は 2 つの InMemory ストアを合成する。
2. **純関数 `PortfolioProjection.Project` に `workingEntries` 引数を足す**。各注文の残数量＝`max(0, 承認数量 − 同じ DecisionId の約定累計)`（約定は同じ `fills` から数える＝二重計上しない）。**承認時刻の市場の現地取引日が当日のものだけ**を算入する（約定と同じ `TradeDate` 規則）。
   - `DailyOrderedAmount` += 残数量 × 承認価格 × `FxRateToBase`（基準通貨）
   - `InvestedCapital` += 同額
   - `OpenPositionCount` += 建玉の無い（銘柄, 市場）の異なり数
   - `SymbolsTradedToday` は**変えない**（理由は IADR-0346 決定 4）
3. **`LedgerPortfolioStateProvider` は `IWorkingEntryOrderSource` を必須依存で受ける**（IADR-0163 決定 2: 不在が統制の無効を意味する依存は必須にする）。窓の下限は `now − 2 日`（当日判定は Project が行う。下限は走査量の上限にすぎない）。
4. **見送り（`OrderDispatchForgone`）を注文アクティビティの終端として射影する**: 発注されなかった承認が当日枠を食い続けないよう、リスク管理が `OrderDispatchForgone` を購読し `IOrderActivityStore.RecordForgone` で `Status=Rejected`・`TerminalAt=OccurredAt` にする（行が無ければ Intent から終端行を作る＝到着順序に依存しない）。既に終端の行は変えない。

## 母集合

走査（2026-09-18・本ブランチ作成直後）:

- 軸 1: `grep -rn "DailyOrderedAmount\|InvestedCapital\|OpenPositionCount\|SymbolsTradedToday" backend --include=*.cs`（テスト除外）
- 軸 2: `grep -rn "PortfolioProjection.Project(\|new LedgerPortfolioStateProvider(" backend --include=*.cs`
- 軸 3: `grep -rn ": IOrderActivityStore\|: IPortfolioLedgerStore" backend --include=*.cs`

| 箇所 | 種別 | 扱い |
| --- | --- | --- |
| `Features/RiskManagement/PortfolioProjection.cs` `Project` | カウンタ | **変更**（`workingEntries` を算入） |
| `Infrastructure/ExternalServices/LedgerPortfolioStateProvider.cs` | 供給 | **変更**（必須依存で注文源を受け Project へ渡す） |
| `Program.cs` の `IPortfolioStateProvider` 登録（2 分岐） | 配線 | **変更**（`IWorkingEntryOrderSource` を登録・注入） |
| `Domain/RiskEvaluator.cs`（段階資金・日次枠・保有建玉数・現金口座の決済済み資金） | ゲート | 変更なし —— 入力が変わるだけ。現金口座の `ExceedsSettledCash` も未約定の買付を含む方が安全側（買付の払い出し予定） |
| `GetSizingContext/SizingContextService.cs` | サイジング文脈 | 変更なし —— 残枠が未約定分だけ減る（望ましい。承認の手前で数量が縮む） |
| `GetRiskStatus/RiskStatusService.cs` / `RiskStatusView.cs` | 表示 | 変更なし —— 「当日発注累計」は発注代金の意味になる（計画の定義どおり） |
| `PortfolioSnapshotBuilder.cs` | 合成 | 変更なし（素通し） |
| `Hosted/ObservedDrawdownRefreshService.cs`・`Hosted/QuoteRefreshService.cs` | DD・時価 | 対象外 —— `Project` を呼ばない／DD だけを使う（呼び出しは `LedgerPortfolioStateProvider` 経由で変更の影響なし） |
| `PortfolioProjection.ProjectOpenPositions` | 建玉射影（損切り検知・自動縮小） | 対象外 —— 保有の事実であり、未約定を建玉として渡すと損切り・決済が存在しない建玉へ向く |
| `IOrderActivityStore` 実装（`EfOrderActivityStore` / `InMemoryOrderActivityStore`） | 射影 | **変更**（`RecordForgone` を追加） |
| `Infrastructure/Steps/OrderActivityProjectionHandlers.cs` | 購読 | **変更**（`OrderDispatchForgoneActivityHandler` を追加） |
| `IPortfolioLedgerStore` 実装・テスト替え玉（`FakeLedger` ×2） | 台帳 | 対象外 —— 台帳の契約は変えない（新ポートを分けた理由は IADR-0346 決定 1） |
| テスト: `LedgerPortfolioStateProviderTests`（6 箇所）・`MoomooFillControlRegressionTests`（1 箇所） | 構築 | **変更**（必須依存の追加に追随） |
| テスト: `RejectionSeparationTests.見送りはリスク管理では購読しない_発注経路を作らない` | 構造（IADR-0211 決定3） | **変更**（「購読しない」→「購読は注文アクティビティの終端化 1 つだけ・依存は `IOrderActivityStore` のみ」へ限定。軸 5: 全テスト実行の赤で発見——軸 1〜3 の走査語では引けなかった） |
| `order_activity` の読み手（`git grep "OrderActivities\|order_activity"` のテスト・マイグレーション除外） | 集計 | 対象外 —— `EfOrderActivitySource`（相場操縦検知）と本件の注文源だけ。監査・報告書は読まない（見送りに `Rejected` を与えても FR-05 の別集計は崩れない。IADR-0346 決定5） |
| `docs/functional/FR-10_risk-controls.md` §統制の入力は「約定」であるという前提 | 仕様書 | **変更**（前提が「約定＋未終端の新規建て」へ変わる） |
| `docs/tests/FR-10_risk-controls-tests.md` 金額系の上限表 | テスト仕様書 | **追記**（T-10-333〜T-10-338） |
| `docs/tests/FR-10_risk-guard-core-tests.md` T-10-70 / T-10-71 | テスト仕様書 | **追随**（改名したテストと反転した期待。軸 4: `git grep` で旧テスト名を引いて発見） |

除外の理由は表の「扱い」列のとおり。

## 受け入れ基準 → テスト

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| AC1 | 未約定の承認済み新規建て 2 件が当日発注累計に算入され、3 件目が上限を超えるなら `DailyOrderAmountExceeded` で拒否される | `MoomooFillControlRegressionTests.未約定の承認済み新規建てが日次枠を消費し上限を超える3件目は拒否される` ＋ `PortfolioProjectionTests.未約定の承認済み新規建ては承認価格で当日発注累計と段階資金と保有建玉数に算入する` ＋ `LedgerPortfolioStateProviderTests.未約定の承認済み新規建ては当日発注累計に算入される` |
| AC2 | 約定ゼロで取消された新規建ては枠を返す | `MoomooFillControlRegressionTests.約定ゼロで取り消された新規建ては枠を返す` ＋ `WorkingEntryOrderSourceTests`（取消済みは返さない） |
| AC3 | 部分約定は「約定分＋残数量」を 1 回だけ数える（約定が進んでも合計は変わらない） | `PortfolioProjectionTests.部分約定の注文は約定分と残数量を一度ずつ数える`・`別の注文の約定は残数量を減らさない` ＋ `MoomooFillControlRegressionTests.部分約定でも約定分と残数量の合計は発注代金のまま変わらない`（旧 `部分約定でも統制の入力は約定分だけ進む` の期待値を改定） |
| AC4 | 決済（Close）の承認は算入しない | `WorkingEntryOrderSourceTests.EF実装は新規建てかつ未終端で下限以降の承認だけを返す`・`InMemory実装もEF実装と同じ注文を返す`（決済を返さない） |
| AC5 | 前取引日の未終端注文は算入しない（市場ごとの取引日境界） | `PortfolioProjectionTests.承認時刻の市場の現地取引日が当日の未終端注文だけを算入する`（米国 ET 境界・日本 JST 境界の Theory） |
| AC6 | 非基準通貨の注文は承認時レートで基準通貨へ換算して算入する | `PortfolioProjectionTests.外貨建ての未約定注文は承認時レートで基準通貨へ換算する` ＋ `WorkingEntryOrderSourceTests.EF実装はレート未記録の承認をレート1として返す` |
| AC7 | 段階資金（`InvestedCapital`）にも同額が入り、保有建玉数は建玉の無い銘柄だけ増える | `PortfolioProjectionTests.未約定の新規建ては建玉の無い銘柄だけ保有建玉数を増やす` |
| AC8 | 見送り（`OrderDispatchForgone`）は注文を終端にし、枠を返す（承認より先に届いても・既に終端なら変えない） | `WorkingEntryOrderSourceTests.見送りは生きている注文を拒否として終端にする`・`見送りが承認より先に届いても終端の行が残る`・`既に終端の注文は見送りで変えない`（InMemory / EF の Theory） ＋ `MoomooFillControlRegressionTests.見送られた新規建ては枠を返す` |
| AC9 | 注文源の EF 実装は Open・未終端・下限以降だけを返し、`order_activity` が無い承認も未終端として返す。終端後の遅着非終端イベントで生き返らない | `WorkingEntryOrderSourceTests.EF実装は新規建てかつ未終端で下限以降の承認だけを返す` |
| AC10 | 注文源を渡さなければ従来どおり（純関数の既定） | `PortfolioProjectionTests.注文源を渡さなければ約定だけを数える_回帰` ＋ 既存 `PortfolioProjectionTests` 全件が緑のまま |

同日再エントリーの既存回帰（`MoomooFillControlRegressionTests` の旧 `約定が台帳へ届くまで統制は拘束せず届いた後は同日再エントリーを拒否する`）は、「未約定は発注枠を消費しない」の期待を反転し、改名した（`未約定の間も発注代金が枠を消費し約定が届いても二重に数えず同日再エントリーは約定後に拒否する`）。テスト仕様書の旧名の参照（T-10-70 / T-10-71）も追随した。

## 検証

`dotnet build backend/backend.slnx -c Release`（警告 0）・`RiskManagementService.Tests`・`dotnet format backend/backend.slnx --verify-no-changes`・文書系検査器（PR 本文に実出力）。

## 付随

- スキーマ変更なし（`order_activity.Status` は既存の整数列。マイグレーション不要）。
- 配備: risk-management イメージの再ビルドと rollout。OpenD の再起動は不要。
