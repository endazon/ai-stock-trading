---
title: 作業仕様書 — 画面の再実装（SC-03 空売り表示項目・SC-01 §2 収集パラメータ・運用段階と発注先の表示規約）
type: spec
status: done
related_ids:
  - SC-01
  - SC-02
  - SC-03
  - FR-10
  - FR-12
  - FR-13
  - FR-19
  - FR-20
  - UC-06
  - IADR-0154
  - IADR-0155
author: endazon (with Claude Code)
created: 2026-08-06
updated: 2026-08-06
related_specs:
  - "./20260718_frontend-settings-screen.md"
  - "./20260718_106_frontend-risk-settings-and-controls.md"
  - "./20260805_334_broker-provider-axis.md"
  - "./20260805_389_frontend-backend-contract-drift.md"
  - "./20260805_362_sc02-ratio-input.md"
  - "../adr/IADR-0154_supply-availability-declared-by-server.md"
  - "../adr/IADR-0155_sc01-collection-parameters-supply.md"
  - "../adr/IADR-0146_backend-response-contract-fixtures.md"
  - "../adr/IADR-0133_maintenance-margin-auto-reduce.md"
  - "../screens/20260718_SC-01_settings.md"
  - "../screens/20260718_SC-02_risk-settings.md"
  - "../screens/20260718_SC-03_control-status.md"
  - "../DEFINITION_OF_DONE.md"
---

# 作業仕様書: 画面の再実装（#340）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（リスク統制・空売り統制）・**FR-12**（内蔵 `paper`）・**FR-13**（設定変更）・
  **FR-19**（取引ガード）・**FR-20**（運用段階と発注先）
- ユースケース（UC）: **UC-06**（設定変更・一時停止・緊急停止／代替フロー「現在状態の参照」）
- 画面（SC）: **SC-01**（設定）・**SC-02**（リスク設定）・**SC-03**（統制状態参照）
- 計画書リンク: `planning/projects/ai-stock-trading/05_screens/01_screens.md`（fixed・2026-08-02 改定）
  - 「運用段階（Stage）と発注先（Broker Provider）の表示規約（共通）」
  - SC-01 §2 収集パラメータ（FR-13）／ SC-02 ／ SC-03 主要素（空売り関連の現況・維持率割れ自動縮小の現況）
- 関連 ADR（計画）: ADR-0008（段階ゲート）・ADR-0009（統制の優先順位）・**ADR-0016**（空売り。決定 3 / 7 / 9 / 15）
- 関連 ADR（実装）: [IADR-0084](../adr/IADR-0084_frontend-risk-settings-and-control-status.md)（画面の feature/route 分割）・
  [IADR-0086](../adr/IADR-0086_frontend-guard-edit-ui.md) 決定4（enum は数値で往来）・
  [IADR-0133](../adr/IADR-0133_maintenance-margin-auto-reduce.md)（維持率割れ自動縮小）・
  [IADR-0134](../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定2（序数不変）・
  [IADR-0140](../adr/IADR-0140_broker-provider-axis.md)・[IADR-0141](../adr/IADR-0141_live-switch-explicit-confirmation.md)・
  [IADR-0142](../adr/IADR-0142_stage1-simulate-only-aggregation.md)・
  [IADR-0146](../adr/IADR-0146_backend-response-contract-fixtures.md)（契約フィクスチャ）・
  [IADR-0151](../adr/IADR-0151_risk-limit-percent-input-and-bounds.md)
- 起点 issue: [#340](https://github.com/endazon/ai-stock-trading/issues/340)
- 本作業で起こす実装 ADR: **IADR-0154**（供給可否をサーバが宣言する）・**IADR-0155**（SC-01 §2 の収集パラメータ）

## 背景と問題

計画の画面設計（05_screens・fixed 2026-08-02）が大改定され、既存実装との差分が残っている。
**全面作り直しではなく、更新後の画面仕様との差分を埋める**のが本作業である。

着手前の実測（`develop` 9b0f96f 時点）で、既に実装済みのものは次のとおりである（**重複実装しない**）。

| 計画の要求 | 実装状況 | 実装箇所 |
| --- | --- | --- |
| `paper` 警告バナー（SC-01/02/03 上部・必須 2 文言） | **実装済み**（#334） | `shared/PaperModeBanner.tsx` |
| 統制カード類の `paper・参考値` ラベル | **実装済み**（#334） | `sc03-controls/ControlStatusPage.tsx` |
| 段階と発注先を 1 行に混ぜない | **実装済み**（#334） | SC-02 `StageView` / SC-03 `StatusView` |
| 発注先の変更（3 値・理由必須・監査・版） | **実装済み**（#334・IADR-0141） | SC-02 `BrokerProviderForm` |
| 実弾切替の警告モーダル 4 点＋二重確認 | **実装済み**（#334・IADR-0141） | SC-02 `LiveSwitchWarningModal` |
| 発注先の変更履歴（SC-02 / SC-03） | **実装済み**（#334） | SC-02 `HistoryView` / SC-03 `ProviderHistoryView` |
| Stage 1 進捗の集計範囲・除外営業日の併記 | **実装済み**（#334・IADR-0142） | SC-03 `Stage1ProgressView` |
| リスク上限の百分率入力・実額併記・値域検証 | **実装済み**（#362 / #408・IADR-0151） | SC-02 `LimitField` |

未実装として残っているのは次の 3 群である。

1. **SC-03 の空売り関連の表示項目**（ADR-0016 決定 15 / 05_screens 2026-08-01 追加）
   —— 維持率（**画面最上位**）・空売り比率・保有ポジションの建玉方向・借株料の累計。
2. **SC-03 の維持率割れ自動縮小の現況**（05_screens 2026-08-02 追加）
   —— 閾値と回復目標（閾値 + 5 ポイント）の併記・直近の発動履歴・「動かす」統制としての視覚的区別・
   縮小対象は必要証拠金の降順。
3. **SC-01 §2 収集パラメータ**（FR-13）—— 変動閾値・収集間隔の閲覧・変更。

あわせて 3 統制を**優先順位順**に並べ**優先統制を明示**する要求（ADR-0009）は、現状 `dl` の並びと
`activeControl` の表示で部分的に満たされているが、**順位そのものが画面に書かれていない**。

## この作業の最重要の制約 —— 供給が無い値を作らない

SC-03 が表示を求める値の一部は**バックエンドに供給元が無い**。実測（`develop` 9b0f96f）は次のとおりである。

| 項目 | 供給の実測 | 根拠 |
| --- | --- | --- |
| **維持率** | **無い。** `IMaintenanceMarginSnapshotSource` の既定実装は `UnavailableMaintenanceMarginSnapshotSource`（常に `null`） | [IADR-0133](../adr/IADR-0133_maintenance-margin-auto-reduce.md) 決定5。PoC 項目 3 は「**実弾口座でのみ**照会でき SIMULATE では照会 API 自体が失敗」（[blocked-tasks](../blocked-tasks.md) A-2） |
| **借株料の累計** | **無い。** 累計を保持する型・ストア・イベントがコード全体に 1 つも無い（`ShortSellOrderContext.BorrowRateAnnual` は 1 注文の事前照会入力であり累計ではない） | 実測。PoC は 5 銘柄すべて `ShortFeeRate=1.5` で銘柄別に動かない（ADR-0016 決定3 の 20% 閾値が発火しない・B-4 裁定待ち） |
| **維持率割れ自動縮小の発動履歴** | **無い。** 発火元（維持率）が無く、Risk 側に履歴ストアも照会 API も無い（報告書側は `NoMarginReductionRecordSource`） | [IADR-0133](../adr/IADR-0133_maintenance-margin-auto-reduce.md)。blocked-tasks「実装済みだが発動しない機能」 |
| **空売り比率** | **条件付き。** 建玉の射影（`PortfolioProjection.ProjectOpenPositions`）はあるが、**建玉評価額には現在値が要る**。現在値（`ICurrentPriceSource`）は `MarketData:EnableMarkToMarket` 既定 false で供給されない | ADR-0016 決定9 の分母は「建玉総額」＝時価。取得原価で代用すると別物の比率になる |
| **建玉の方向（ロング / ショート）** | **有る。** 台帳射影の `OpenPosition.Side`（符号付き在庫の向き） | `PortfolioProjection.ProjectOpenPositions` |
| **閾値・回復目標・空売り比率上限** | **有る（設定値）。** `ShortSellingLimits`（維持率閾値 0.40・回復目標オフセット 0.05・空売り比率上限 0.50） | `GET /risk-controls/settings` の `shortSell.limits` に既に載っている |

**画面はこれらを「取得できていない」と明示する。** 0 や「—」だけで誤魔化して正常値に見せてはならない。
とくに維持率は計画が「**本画面の最上位に置く。マージンコールは口座を失う唯一の経路である**」と書いた指標であり、
**未供給を正常のように見せることが最悪の失敗**である（[#403](https://github.com/endazon/ai-stock-trading/issues/403)
の `ControlViolationCount` 既定 0 が「違反なし」に見えた fail-open と同型の事故を作らない）。

## 対象範囲

### 対象（本 PR に入れる）

#### A. SC-03 空売り関連の現況（バックエンド＋フロント）

- **バックエンド**: 読み取り専用エンドポイント `GET /risk-controls/short-selling`（OwnerOnly）を新設する。
  応答は各指標について**供給可否（`MetricAvailability`）を明示的に宣言**する（IADR-0154）。
  - `Available`（0）… 値がある
  - `NotSupplied`（1）… **供給元が無い／取得できない**（画面は「取得できていません」と警告表示する）
  - `NotApplicable`（2）… 概念が成立しない（建玉が 1 件も無い等）
  - 供給可否は**サーバが判定する**。フロントに「維持率は未供給」と書き込むと、供給が入った日に
    画面が嘘をつき続ける（#403 と同型の逆向きの事故）。
- **フロント**: SC-03 に「維持率・空売りの現況」節を**画面最上位**（3 統制より上）に置く。
  保有ポジション一覧に**方向（ロング / ショート）**と**借株料累計**の列を加える。
  「維持率割れによる自動縮小」は 3 統制とは**別の節**に置き、`role="note"` と見出し文で
  **「動かす」統制であること**（利用者の承認を待たず AI を介さずに建玉を決済する）を明示する。
  縮小対象の順序（**必要証拠金の降順**。含み損の大きい順ではない）を画面に明記する。

#### B. SC-03 の 3 統制の優先順位

- 3 統制を**優先順位つきの表**（1: kill switch ＞ 2: 日次損失ロックアウト ＞ 3: 一時停止）で描き、
  **優先統制**（`activeControl`）を明示する。各統制の**発動主体・解除条件**を併記する（ADR-0009）。

#### C. SC-01 §2 収集パラメータ（バックエンド＋フロント）

- **変動閾値**: MarketMonitorService に `PUT /monitor/settings/movement-threshold`（OwnerOnly・
  `{ movementThresholdRatio, reason }`）を新設する。**理由必須・値域検証・変更履歴（監査）**を伴い、
  クールダウンと監視銘柄を巻き込まない（既存の `PUT /monitor/settings` は**全置換**であり、
  画面から使うと送り漏らしで監視銘柄が消える）。履歴照会に `GET /monitor/settings/history` を加える。
- **収集間隔**: **供給が無い**。実測では起動時構成（`Collection:PollIntervalSeconds` / `Monitor:PollIntervalSeconds`）
  であり、読み書きするエンドポイントも設定ストアも存在しない。**入力欄を作らず**、画面に
  「収集間隔は起動時構成であり画面から閲覧・変更できない」ことを明示する（IADR-0155）。
  計画（FR-13・SC-01 §2）との差異は環流記録に残す。

#### D. 文書

- 画面仕様書 `docs/screens/20260718_SC-0{1,2,3}_*.md` を SC 単位で更新する。
- 実装 ADR **IADR-0154** / **IADR-0155**。索引（`docs/adr/README.md`）へ追加する。
- 環流記録 `feedback/20260806_sc01-sc03-unsupplied-screen-items.md`。
- `docs/blocked-tasks.md` の「実装済みだが発動しない機能」へ、画面の表示と食い違わないよう追随させる。

### 対象外（理由を明記して残す）

| 項目 | 理由 |
| --- | --- |
| 収集間隔の**変更**機能そのもの | 設定ストア・エンドポイント・ポーリングの動的再読込を新設する**機能追加**であり、画面の再実装の範囲を超える。供給が無いことを画面と文書に明示し、環流記録で計画へ返す |
| 維持率・借株料・自動縮小履歴の**供給元の実装** | 実口座への接続（PoC 項目 3・A-1 / A-2）が要る。本 PR は「供給が無いことを画面が正しく表現する」ところまでを担う |
| 段階ゲートの**承認操作**・pause/kill switch の操作 UI | 計画が Discord Bot へ一元化（#165・IADR-0081）。Web に置かない |
| SC-02 のガード・段階の**変更 UI** の拡張 | ガードは #188/IADR-0086 で実装済み。段階の変更は段階ゲート承認（Discord）であり画面に置かない |
| platform SPA 新スタック（React 19 + TanStack）への追随 | 基盤（microservices-platform）側の合成点の移行に従属し、本ユニット単独では検証できない。#187 の残件として据え置く |

## 受け入れ基準（テストへの写像）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | 維持率の供給が無いとき、SC-03 は**「取得できていません」と明示**し、数値も「—」だけの表示もしない | `ControlStatusPage.shortSelling.test.tsx`「維持率が未供給のとき警告として明示する」 |
| 2 | 維持率の供給があるとき、値・適用閾値・回復目標（閾値+5pt）を表示する | 同「維持率が供給されているとき現況と閾値・回復目標を表示する」 |
| 3 | 借株料の累計は**常に未供給**として明示され、`0` と表示されない | 同「借株料累計は未供給として明示され 0 を表示しない」 |
| 4 | 空売り比率は建玉が無ければ「建玉なし」、評価額が揃わなければ「取得できていません」を出し分ける | 同 2 ケース |
| 5 | 保有ポジションに**方向（ロング / ショート）**が出る | 同「保有ポジションに建玉方向を表示する」 |
| 6 | 自動縮小の節が 3 統制と**別枠**で描かれ、「動かす」統制である旨と**必要証拠金の降順**が明記される | 同「維持率割れ自動縮小は 3 統制と別枠で『動かす』統制として描かれる」 |
| 7 | 自動縮小の発動履歴が未供給のとき「発動なし」と表示しない | 同「発動履歴が未供給のとき『発動なし』と表示しない」 |
| 8 | 3 統制が優先順位つきで並び、優先統制が明示される | `ControlStatusPage.test.tsx`「3 統制を優先順位順に表示し優先統制を明示する」 |
| 9 | SC-03 に**変更操作が 1 つも無い**（`textbox`/`checkbox`/`radio`/`combobox` と保存系ボタンが存在しない） | `ControlStatusPage.readonly.test.tsx` ／ E2E `sc03-controls.spec.ts` |
| 10 | SC-01 §2 で変動閾値を閲覧・変更でき、**理由必須**・**値域外は保存不可** | `SettingsPage.collection.test.tsx` ／ E2E `sc01-settings.spec.ts` |
| 11 | SC-01 §2 が**収集間隔の入力欄を持たず**、供給が無いことを明示する | `SettingsPage.collection.test.tsx`「収集間隔は入力欄を持たず未供給として明示する」 |
| 12 | 変動閾値の保存で**競合（409）**は自動再試行せず再読込を促す | E2E `sc01-settings.spec.ts` |
| 13 | 実弾切替はチェックボックスと「REAL」入力の**両方**が無ければ通らない（既存の退行防止） | `RiskSettingsPage.brokerProvider.test.tsx`（既存＋否定形の追加） |
| 14 | `paper` の約定が Stage 1 進捗に算入されない旨が画面に出る（既存の退行防止） | `ControlStatusPage.brokerProvider.test.tsx`（既存） |
| 15 | バックエンドの新応答がフロントの契約フィクスチャと一致する | `FrontendContractFixtureTests`「空売り現況応答がフロントの契約フィクスチャと一致する」 |

## 設計方針

### 供給可否はサーバが宣言する（IADR-0154）

`decimal?` の `null` だけでは「**供給が無い**」と「**概念が成立しない**」を区別できない。
リポジトリは既に同じ区別を `MaintenanceMarginEvaluationStatus`（`NoActionRequired` / `SnapshotUntrusted`）で
行っており（IADR-0133 決定8）、画面にも同じ規律を持ち込む。

enum は**数値で往来**する（IADR-0086 決定4）。序数は末尾追加のみとし既存を動かさない（IADR-0134 決定2）。

### 閾値を画面に直書きしない

維持率閾値（0.40）・回復目標オフセット（0.05）・空売り比率上限（0.50）はすべてサーバ応答から取る。
`Stage1GateCriteria` と同じ方針である（計画の改訂に画面が追随しないことを防ぐ）。

### 契約フィクスチャ（IADR-0146）

新設エンドポイントの応答は `frontend/src/features/risk/contract-fixtures/risk-controls.short-selling.json` として
**実エンドポイントから採る**。フロントは `contractFixtures.ts` で契約型へ代入し、画面テスト・E2E のモックは
必ずそこから作る。**フィクスチャだけ書き換えて typecheck を通す**ことは規律違反である。

### 変動閾値の値域（IADR-0155）

計画は「正値・下限/上限の範囲内」とだけ書き、具体値を定めていない。実装が値域を決める（`RiskLimitBounds` と同じ扱い）。
**下限 0 超・上限 0.50（50%）**とする。0 以下ではすべての変動が閾値超過となり監視が統制にならず、
50% 超では 1 日で半値になる変動でしか発火せず監視が事実上無効になるためである。既定は 0.03（±3%・
計画 FR-03 の確定値）。値域の具体値は環流記録で計画へ返す。

## 影響範囲

- バックエンド
  - `RiskManagementService.Application/State/ShortSellingStatusView.cs`（新規）
  - `RiskManagementService.Application/Services/ShortSellingStatusService.cs`（新規）
  - `RiskManagementService.Api/Foundation/Endpoints/RiskControlEndpoints.cs`（1 経路追加）・`Program.cs`（DI 1 行）
  - `MarketMonitorService.Application/State/MonitorSettingsChangeEntry.cs`（enum 末尾追加）
  - `MarketMonitorService.Application/Services/MonitorSettingsService.cs`（新規）
  - `MarketMonitorService.Domain/MonitorSettingsBounds.cs`（新規）
  - `MarketMonitorService.Api/.../MonitorSettingsEndpoints.cs`（2 経路追加）・`Program.cs`（DI 1 行）
- フロント
  - `features/risk/contracts.ts`（型・写像の追加）・`contractFixtures.ts`・`contract-fixtures/*.json`
  - `features/monitor/contracts.ts`（監視設定の型・値域）
  - `features/sc01-settings/SettingsPage.tsx`・`CollectionSettingsForm.tsx`（新規）
  - `features/sc03-controls/ControlStatusPage.tsx`・`ShortSellingStatusView.tsx`（新規）
  - テスト（vitest）・E2E（Playwright）
- 文書: `docs/screens/*`・`docs/adr/IADR-0154`・`IADR-0155`・`docs/adr/README.md`・
  `docs/blocked-tasks.md`・`feedback/20260806_*`

## 完了条件

`docs/DEFINITION_OF_DONE.md` に加えて次を満たす。

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet test backend/backend.slnx --filter Category!=Integration` が緑
- `dotnet format --verify-no-changes` が緑
- `npm --prefix frontend run typecheck` / `lint` / `test -- --run` が緑
- `node scripts/check-doc-links.js` / `check-commit-messages.js` / `check-banned-libraries.js` /
  `check-test-traceability.js` / `check-consumer-endpoint-names.js` が緑
- **変異検査**で (a) 実弾切替の二重確認、(b) 維持率などの未供給表示、(c) SC-03 の参照専用性を反転させると
  テストが赤くなることを実測する
