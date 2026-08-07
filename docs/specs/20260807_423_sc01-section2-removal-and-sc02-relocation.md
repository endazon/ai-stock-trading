---
title: 作業仕様書 — SC-01 §2 を廃止し、変動閾値・クールダウンを SC-02 へ移し、Stage 1 の最小取引件数を設定化し、条件 3 の計上単位を固定する
type: work
status: review
related_ids: [SC-01, SC-02, SC-03, FR-13, FR-03, FR-20, FR-11, UC-06, ADR-0008, IADR-0155, IADR-0149, IADR-0142, IADR-0151, IADR-0164]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - ../../planning/projects/ai-stock-trading/05_screens/01_screens.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
related_specs:
  - ../adr/IADR-0164_stage1-trade-count-setting-and-monitor-parameter-relocation.md
  - ../adr/IADR-0155_sc01-collection-parameters-supply.md
  - ../adr/IADR-0149_stage1-trade-count-supply.md
  - ../adr/IADR-0142_stage1-simulate-only-aggregation.md
  - ../adr/IADR-0151_risk-limit-percent-input-and-bounds.md
  - ../screens/20260718_SC-01_settings.md
  - ../screens/20260718_SC-02_risk-settings.md
  - ../functional/FR-20_staged-gates.md
  - ../tests/FR-20_staged-gates-tests.md
  - ../blocked-tasks.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: SC-01 §2 の廃止と SC-02 への移管（#423）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-01**（§2 廃止・§1 のみの画面へ）／**SC-02**（変動閾値・クールダウン・最小取引件数の追加）／**SC-03**（最小取引件数が設定値になったことの表示）
- 機能要求（FR）: **FR-13**（設定変更）／**FR-03**（価格変動検知）／**FR-20**（段階ゲート）／**FR-11**（監査ログ）
- ユースケース（UC）: **UC-06**
- 技術検討: **06_daytrading-review §4.1 条件 3・§4.3**
- 関連 ADR: **ADR-0008**（段階ゲートとバックテスト）
- 実装 ADR: **[IADR-0164](../adr/IADR-0164_stage1-trade-count-setting-and-monitor-parameter-relocation.md)（本作業）**
- 起点 issue: [#423](https://github.com/endazon/ai-stock-trading/issues/423)
- 計画 submodule: **`06fa163`**（`a4616a8` で pin 済みの内容を含む。本作業では submodule を進めない）

## 目的・背景

2026-08-07 の利用者裁定（質問票 第 13 回 Q11・Q12・Q6）が計画へ反映された。要点は 4 つである。

1. **SC-01 §2「収集パラメータ」を廃止する。** SC-01 は §1（全体前提条件・FR-17）のみの画面になる。
   **収集間隔は起動時構成とし、画面・API から変更できないことを構造で担保する**（Q11・案 A）。
2. **変動閾値・クールダウンを SC-02 へ移す**（Q12・案 B）。**権威は MarketMonitorService**（`GET/PUT /monitor/settings`）
   であり、旧記述の「ConfigurationService 由来」は誤りであった。**実装を計画へ合わせて ConfigurationService へ移す案は
   裁定で却下されている**（監視サービスの自律性が下がるため）。
3. **Stage 1 の最小取引件数を SC-02 の設定項目にする**（Q6 の追加指示）。既定 100・値域 1〜1000・
   変更理由必須・監査ログ・楽観排他。**100 未満は警告を常時表示するが、設定は妨げない。**
4. **条件 3 の計上単位を実装で固定する。**「約定が成立した新規建て注文 1 件」。1 注文が分割約定しても 1 件、
   手仕舞いは計上しない、約定しなかった注文は計上しない。

### 現況調査（実装前に確認した既存の挙動）

| 裁定 | 現況（develop `0683d80` 実測） | 本作業でやること |
| --- | --- | --- |
| (1) SC-01 §2 の撤去 | `frontend/src/features/sc01-settings/CollectionSettingsForm.tsx` が §2 を描画している | **節ごと削除**し、SC-01 を §1 のみにする |
| (1) 収集間隔の変更経路 | **既に存在しない。** 起動時構成 `Monitor:PollIntervalSeconds` / `Collection:PollIntervalSeconds` のみ | **重複実装をしない。** 「変更経路が存在しないこと」を**構造テスト（否定形）**で固定する |
| (2) 変動閾値・クールダウンの API | **既に実装済み。** `PUT /monitor/settings/movement-threshold` / `/cooldown`（理由必須・履歴記録・`MonitorSettingsBounds` で値域検証。IADR-0155） | 値域の**境界**（0 不可・0.50 可・0 可・24h 可・24h 超不可）をテストで固定する |
| (2) 全置換 `PUT /monitor/settings` | **穴がある。** 理由も履歴も持たず、値域検証も `ratio > 0` / `cooldown >= 0` だけ（上限なし） | **値域を `MonitorSettingsBounds` へ寄せ、理由必須・履歴記録にする**（IADR-0155 残余リスク 4 の解消） |
| (2) BFF | **未結線。** `MonitorBffEndpoints` は watchlist 4 本のみで `/monitor/settings*` を登録していない（IADR-0072 決定2 の前提が #340 で崩れていた） | SC-02 が実消費する 4 本（`GET /settings`・`PUT /settings/movement-threshold`・`PUT /settings/cooldown`・`GET /settings/history`）を登録する |
| (2) クールダウンの UI | **未実装。** SC-01 §2 は変動閾値のみ | SC-02 に**クールダウンの入力**を新設する |
| (3) 最小取引件数の設定化 | **未実装。** `Stage1GateCriteria.Default`（100）が定数 | `RiskManagementSettings.Stage1MinimumTradeCount` を新設し、`StageGateService` が実効値として重ねる |
| (4) 計上単位 | **既に正しい。** `Stage1Aggregation.CountTrades` が `DecisionId` で畳み、`CountsAsTrade` が `Open` かつ `MoomooSimulate` だけを数える（IADR-0149 決定2）。`OrderExecutedStage1FillHandler` は `FilledQuantity <= 0` を記録しない | **重複実装をしない。** 「3 回分割約定 → 1 件」「手仕舞いを数えない」「未約定を数えない」を**設定値が変わっても崩れないこと**まで含めてテストで固定する |

## 対象範囲

### やること

- SC-01 §2 の撤去（`CollectionSettingsForm` の削除・`SettingsPage` の文言是正）
- SC-02 への「市場監視パラメータ（変動閾値・クールダウン）」節の新設（監視銘柄の近く）
- SC-02 への「Stage 1 の最小取引件数」節の新設（運用段階の参照表示の近く）
- Stage 1 最小取引件数の設定化（バックエンド: 設定・値域・履歴・エンドポイント・段階ゲートへの反映）
- 条件 3 の計上単位の固定（テストのみ。実装は既に正しい）
- 収集間隔の変更経路が存在しないことの構造テスト（MarketMonitor / InformationCollection の両サービス）
- 全置換 `PUT /monitor/settings` の値域・理由・履歴の是正
- BFF への `/monitor/settings*` 4 本の登録
- 文書更新（画面仕様書 SC-01 / SC-02・機能仕様書 FR-20・テスト仕様書 FR-20・IADR-0164・blocked-tasks・環流）

### やらないこと（裁定が明示的に却下したもの）

- **収集間隔を画面から変更できるようにすること**（Q11 で却下。実装すること自体が違反）
- **変動閾値の権威を ConfigurationService へ移すこと**（Q12 で却下）
- **条件 1（統制違反 0 件）・条件 2（60 営業日）を設定化すること**（Q6 が「及ばない」と明示）
- **§4.3 の打ち切り規則（累計 120 営業日で Stage 0 へ差し戻す）の変更**

## 実装方針

### 1. SC-01 §2 の撤去と収集間隔の構造的担保

`CollectionSettingsForm.tsx` と `SettingsPage.collection.test.tsx` を削除し、`SettingsPage` の説明文を
「全体前提条件（FR-17）の画面である」に是正する。**収集パラメータへの導線を SC-02 へ向ける。**

収集間隔については、**変更経路が無いことをテストで固定する**（`CollectionIntervalNotConfigurableTests`）。
`EndpointDataSource` を実 `Program.cs` の配線から取得し、

- ルートパターンに `interval` / `poll` を含むエンドポイントが 1 本も無いこと
- 監視設定のドメイン型・要求 DTO に間隔を表すプロパティが無いこと

を検査する。**否定形を構造（型・ルート表）に対して置く**ことで、将来「利便性の改善」として
変更エンドポイントが足された瞬間に赤くなる。

### 2. 変動閾値・クールダウンの SC-02 移管

- **API は現状のまま**（`PUT /monitor/settings/movement-threshold` / `/cooldown`）。権威は MarketMonitorService。
- 全置換 `PUT /monitor/settings` は **`MonitorSettingsBounds` を通し、理由必須・履歴記録**にする。
  これをしないと「画面は 0 を弾くが API 直叩きで 0 を保存できる」状態が残る（#423 の退行防止項目に反する）。
- BFF に 4 本を登録する（未結線のままでは SC-02 から到達できない）。
- 画面は 2 つの独立したフォーム（`変動閾値の変更` / `クールダウンの変更`）に分ける。
  **部分更新の API が 2 本ある以上、1 フォームで両方を送ると片方だけ失敗した状態を作れる。**
  アクセシブル名は完全に分ける（`変動閾値を保存` / `クールダウンを保存`）。

### 3. Stage 1 最小取引件数の設定化

| 層 | 変更 |
| --- | --- |
| Domain | `Stage1TradeCountBounds`（既定 100・値域 1〜1000・統計的根拠の下限 100）。`Stage1GateCriteria.BelowStatisticalBasis`（サーバが宣言する） |
| Domain | `RiskManagementSettings.Stage1MinimumTradeCount`（既定 100・init） |
| Infrastructure | 設定は単一行 JSON。DTO へ `int?` を足し、**null・値域外は既定 100 へ落とす**（低い側へ落とさない＝緩い側へ倒さない） |
| Application | `SettingsChangeType.Stage1MinimumTradeCountChanged`（**末尾追加**・序数 8）。`RiskSettingsService.UpdateStage1MinimumTradeCount` |
| Application | `StageGateService` が `IRiskSettingsStore` から実効値を読み、`policy.Stage1Criteria with { MinimumTradeCount = … }` を用いる |
| Api | `PUT /risk-controls/settings/stage1-minimum-trade-count`（`int?` + reason。省略・値域外は 400） |
| BFF | 同経路を 1 本登録 |
| Notification | Discord `/stage status` に「100 未満の設定である」旨の警告行を足す（＝**昇格承認側の警告**） |
| Frontend | SC-02 に入力＋警告、SC-03 に警告 |

**条件 1・条件 2・打ち切り 120 営業日には触れない。** `Stage1GateCriteria` の他 2 項目は
`StageGatePolicy` の既定（`Stage1GateCriteria.Default`）のままである。

### 4. 条件 3 の計上単位

実装は既に正しい（`Stage1Aggregation`）。**新規実装はしない。** 次を退行防止テストで固定する。

- 1 注文（同一 `DecisionId`）が 3 回に分割約定しても **1 件**
- 手仕舞い（`PositionEffect.Close`）は **0 件**
- 約定しなかった注文（`FilledQuantity <= 0`）は観測として**記録されない**
- **設定値を 1〜1000 のどこに置いても上記は変わらない**（設定化で単位が壊れないこと）

## 受け入れ基準 → テスト写像

| # | 受け入れ基準（issue「退行防止（テスト必須）」） | テスト |
| --- | --- | --- |
| A1 | 収集間隔を変更する API/UI が存在しない | `CollectionIntervalNotConfigurableTests`（MarketMonitor / InformationCollection）・`SettingsPage.test.tsx`（入力欄不在）・`RiskSettingsPage.monitorParameters.test.tsx`（SC-02 にも作らない） |
| A2 | 変動閾値: 0 不可 / 0 超可 / 0.50 可 / 0.50 超不可 | `MonitorSettingsBoundsTests`（Domain）・`MonitorCollectionSettingsEndpointsTests`（API）・`monitor/contracts.test.ts`（画面） |
| A3 | クールダウン: 0 可 / 24h 可 / 24h 超不可 | 同上 |
| A4 | 最小取引件数: 0 不可 / 1 可 / 1000 可 / 1001 不可 | `Stage1TradeCountBoundsTests`（Domain）・`RiskSettingsStage1TradeCountEndpointTests`（API）・`risk/contracts.test.ts`（画面） |
| A5 | 100 未満で警告が常時表示され、かつ設定を妨げない | `Stage1TradeCountBoundsTests`（`BelowStatisticalBasis`）・API テスト（400 にならず 200）・`RiskSettingsPage.stage1TradeCount.test.tsx`・`ControlStatusPage` テスト・`HttpStageGateControllerTests`（Discord 文言） |
| A6 | 計上単位: 分割約定 3 回で 1 件／手仕舞い不算入／未約定不算入 | `Stage1TradeCountUnitTests`（拡充）・`Stage1FillObservationConsumerTests` |
| A7 | 変動閾値・クールダウン・最小取引件数のいずれも理由なしでは保存できず、監査ログに残る | 各 API テスト（理由空欄 400・履歴に載る） |

## ミューテーションテスト（自己検証・本 PR で実施する）

| # | 意図的な改変 | 期待 |
| --- | --- | --- |
| (a) | 収集間隔の変更エンドポイントを復活させる | A1 の構造テストが赤 |
| (b) | 変動閾値の下限を `< 0` へ緩める（0 を許す） | A2 が赤 |
| (c) | 最小取引件数の値域を `0..1001` へ広げる／100 未満で警告を出さないようにする | A4・A5 が赤 |
| (d) | `CountTrades` の `Distinct()` を外す（約定イベント数を数える） | A6 が赤 |

## 影響範囲

- backend: MarketMonitorService（値域・理由・履歴）・RiskManagementService（設定・段階ゲート）・
  NotificationService（Discord 表示）・InformationCollectionService（構造テストのみ）・Bff
- frontend: `sc01-settings`（節削除）・`sc02-risk-settings`（節追加）・`sc03-controls`（警告）・
  `risk/contracts.ts`・`monitor/contracts.ts`・契約フィクスチャ・e2e
- docs: 画面仕様書 SC-01 / SC-02・機能仕様書 FR-20・テスト仕様書 FR-20・IADR-0164・blocked-tasks・feedback

## 未解決・残件

- 収集間隔は**依然として起動時構成のみ**である。これは裁定どおりであり不足ではない
  （blocked-tasks の該当行を「裁定により解消」へ更新する）。
- `Stage1GateCriteria` の他 2 項目（60 営業日・120 営業日）は設定化しない（裁定が及ばないと明示）。
