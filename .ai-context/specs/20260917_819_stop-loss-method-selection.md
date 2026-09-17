---
title: SIMULATE の損切り実行機構を選択式にする（設定点・実弾での拒否・承認イベントへの搭載・空売りの除外・S2 の実装・S1/S3 の fail-closed 置き場）
type: spec
status: accepted
related_ids: [FR-10, FR-12, FR-11, UC-02, UC-06, ADR-0040, ADR-0016, ADR-0003, IADR-0016, IADR-0111, IADR-0141, IADR-0210, IADR-0211, IADR-0342]
author: endazon (with Claude Code)
created: 2026-09-17
updated: 2026-09-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1・決定2・決定3・決定6)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 の 3 文〔口座種別の軸〕)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md (§逆指値が成立しない場合の扱い)
---

# 仕様書: SIMULATE の損切り実行機構を選択式にする（#819）

## 起点

- #819（enhancement）。計画 ADR-0040（planning#638 の裁定・2026-09-17）決定 1・2・3・6、フォローアップ 2。
- 症状の一次情報: #809（moomoo のペーパー口座は `OrderType_Stop` を受け付けず、SIMULATE では新規建てが全件取消）。
- 計画: FR-10 の 3 文（2026-09-17 に「口座種別」の軸を追加）・業務フロー 02 §逆指値が成立しない場合の扱い。
- 新規 IADR: **IADR-0342**（本作業の設計）。追記: IADR-0210（引用した拘束元の是正のみ・挙動不変）。

## 射程

| 含む | 含まない（別 issue） |
| --- | --- |
| 選択の設定点（`PUT /risk-controls/settings/stop-loss-method`・OwnerOnly・履歴） | SC-02 の入力 UI・SC-03 の表示・日報（#823） |
| 実弾での拒否（設定側 2 方向・発注執行側の per-order 拒否） | S1 ソフトウェア逆指値（#820） |
| `OrderApproved` への手法の搭載（任意項目・既定 S0） | S3 他のブローカー側注文種別（#821） |
| 空売り建玉の除外（常に S0） | 決定 5（根拠文の数量とサイジングの突合） |
| S2（逆指値なしの建玉を許容）と「ペーパーで免除」の事実の監査・通知 | 計画 ADR-0040 決定 4 の実弾解禁前の確認 |
| S1/S3 選択時の fail-closed（S0 と同じ扱い＋警告ログ） | |

## 設計（詳細は IADR-0342）

1. **型**: `Shared.Contracts.Trading.StopLossExecutionMethod`（`BrokerStopOrder=0`〔S0〕/ `SoftwareStop=1`〔S1〕/
   `NoProtectiveStop=2`〔S2〕/ `AlternativeBrokerOrderType=3`〔S3〕）。序数は動かさない（ワイヤは整数）。
2. **設定**: `RiskManagementSettings.StopLossMethod`（既定 S0・本体プロパティ）。単一行 JSON に nullable キーを足し、
   読み取りは allow-list（未知・旧行は S0）。変更は `RiskSettingsService.UpdateStopLossMethod`（理由必須・前後値つき履歴
   `SettingsChangeType.StopLossMethodChanged`＝末尾追加の序数 9）。版（`Version` 並行トークン）は既存の `Save` がそのまま効く（競合は 409）。
3. **実弾での拒否（設定側）**: 現在の発注先が moomoo REAL のとき S0 以外への変更を拒否（400・設定も履歴も不変）。
   逆方向として、S0 以外が有効なまま moomoo REAL へ切り替える要求を `BrokerProviderChangeRejection.StopLossMethodNotBrokerStop`
   （末尾追加の序数 4）で拒否する。
4. **承認イベント**: `OrderApproved` の末尾に `StopLossExecutionMethod StopLossMethod = BrokerStopOrder` を足す。
   スクリーニングの承認だけが現在の設定値を載せる（owner 手仕舞い・自動縮小は Close のため既定のまま）。
5. **発注執行の解決**（純関数 `StopLossMethodPolicy.Resolve`）: Open にのみ適用し、順に
   (a) 手法が S0 → S0、(b) 発注先が moomoo SIMULATE でない → **拒否**（見送り `StopLossMethodNotPermitted`・Error ログ）、
   (c) 空売り（`ProductType.ShortSell`）→ S0、(d) S2 → 保護レグ免除、(e) S1/S3/未知 → S0（未実装の警告ログ）。
6. **S2 の事実**: 新イベント `ProtectiveStopWaived`（`ProtectiveStopCoverageLost` とは別）。エントリーが生きている
   （Accepted / PartiallyFilled / Filled）ときだけ発行する。監査台帳へ記録し、Discord へ Warning で通知する。
   逆指値レグの記録（`protective_stop_orders`）を作らないため `ProtectiveStopGuard` の巡回対象に入らない。
7. **起動時の停止**: 発注執行は手法を起動時に知らない（設定は risk-management の DB にあり、承認ごとに届く）。
   さらに発注執行の実弾はアダプタ生成前に `LiveTradingGate` が起動時に止める（未解禁）。よって「実弾で S0 以外が有効」は
   設定側 2 方向の拒否＋承認ごとの拒否で塞ぐ（IADR-0342 決定 5 に記録）。

## 母集合（着手前の走査・2026-09-17・`git grep -l -F`・CHANGELOG.md を除く）

| 検索語 | ファイル数 | 扱い |
| --- | --- | --- |
| `ProtectiveStop` | 53 | 本番コード: `OrderExecutionAppService`（S2 分岐を追加）・`OrderDispatchResult`（免除の結果を追加）・`OrderApprovedHandler`（発行）・`AuditEntryFactory` / `AuditEventHandlers`（免除の監査を追加）・`NotificationFormatter` / `NotificationHandlers`（免除の通知を追加）。`ProtectiveStopGuard` / `ProtectiveStopGuardService` / EF ストア / 行型 / migration / `ProtectiveStopLedgerHandlers` / `ProtectiveStopIds` / `ProtectiveStopOrder` は**変更しない**（S2 は記録を作らないため巡回対象外・S0 の挙動不変。`OrderExecutionServiceStopLossMethodTests` が「保護記録なし・Active 0 件」で固定）。契約 `ProtectiveStopPlaced` / `ProtectiveStopCoverageLost` は**意味を変えない**。既存テストの表明は変えず（例外 1 件は下表 AC1）、免除のテストを同じファイルへ足す（`OrderApprovedConsumerTests`・監査 / 通知のテスト。`AuditCycleCompletenessTests` は契約イベント全数の標本に 1 件足す）。`.ai-context/specs/` 7 件は凍結記録のため変更しない。`.ai-context/adr/` 4 件のうち IADR-0210 と索引だけを追記（IADR-0276 / IADR-0286 は語の言及のみで挙動の記述なし）。docs 3 件は下表 |
| `StopOrderRequired` | 23 | **変更しない**。空売りに重ねた拒否理由（ADR-0016 決定 2(b)）であり、ADR-0040 決定 2 が対象外と明記。本作業は空売りを常に S0 に倒すため同理由の意味は不変 |
| `IProtectiveOrderBroker` | 17 | **変更しない**。能力ポートの契約は不変（S2 は呼ばないだけ） |
| `new OrderApproved(`（本番コード） | 3 | `OrderScreeningService` は手法を載せる。`PositionCloseService`（owner 手仕舞い）・`MaintenanceMarginReductionService`（自動縮小）は Close であり既定 S0 のまま（手法は Open にしか効かない） |
| `EntryCancelled` | 9 | **変更しない**。S0 の未受理時の対処であり、S2 はこの分岐へ入らない |
| `逆指値なしの建玉` | 18 | 本番コード 7 件の注釈は S0 の規律として正しく、書き換えない（`OrderExecutionAppService` 冒頭注釈にだけ S2 の例外を追記）。docs 2 件（`FR-10_risk-controls.md`・`FR-10_risk-controls-tests.md`）は口座種別の軸と S2 を追記 |
| `MapPut("/settings`（risk-management） | 5 | 同型の設定点。新エンドポイントは 6 本目として `UpdateStopLossMethod/Endpoint.cs` に置き、`RiskControlEndpoints` の owner グループへ登録 |
| `OrderDispatchForgoneReason`（docs / deploy / frontend / infra） | 0 | 新しい理由の追加で追随する外部の列挙は無い（`NotificationFormatter.ReasonLabel` だけが写像を持つ→追記） |

**除外とその理由**

- `.ai-context/specs/` の既存仕様書: 凍結記録（`traceability.repo.md`）。
- フロントエンドの画面（SC-02 の入力・SC-03 の表示・履歴種別 9 の表示ラベル）: #823 の射程。**契約型と契約フィクスチャだけは
  本 PR で追随する**（後掲「実装中に追加で判明したこと」。フィクスチャの再生成がフロントの契約型の追随を強制する仕組みのため）。
- `docs/api/openapi.yaml`: 8 行の雛形で risk-controls の経路を持たず、`scripts/generate-openapi.sh` も無い（生成物が無い）。orval の生成対象でもない。
- `deploy/helm/.../files/pipeline.json`: 主経路のイベント（`OrderApproved` 等）だけを列挙しており、保護逆指値系のイベントは載っていない（`ProtectiveStopCoverageLost` も無い）。新イベントも載せない。

## 受け入れ基準 → テスト

| # | 受け入れ基準（#819） | テスト |
| --- | --- | --- |
| 1 | S0 既定で現行挙動が変わらない | 既存テスト全緑（`BrokerProviderSettingsTests.拒否理由の列挙子を増やしていない` のみ新しい拒否理由を足して更新——#434 が禁じた「未知の発注先」の二重化ではない）／`StopLossMethodPolicyTests`（S0 は発注先を問わず S0）／`OrderExecutionServiceStopLossMethodTests.S0は発注先を問わず保護逆指値を同時発注する`／`StopLossMethodSettingsTests`（既定 S0・旧行と未知の序数は S0・手法を渡さない発注先判定は従来と同一）／`StopLossMethodContractTests`（手法を持たない旧い承認本文は S0） |
| 2a | 実弾で S0 以外の設定変更が拒否される | `StopLossMethodSettingsTests`（REAL で S1/S2/S3 → 拒否・設定と履歴は不変／S0 以外のまま REAL へは確認操作が揃っても拒否）／`StopLossMethodEndpointTests`（同じ 2 方向を HTTP 400 で） |
| 2b | 実弾で S0 以外が有効な承認は発注されない | `OrderExecutionServiceStopLossMethodTests.SIMULATE以外でS0以外の承認は発注せず見送る`（REAL × S1/S2/S3・内蔵 paper × S2）・`実弾では空売りでもS0以外の承認は発注しない`／`OrderApprovedConsumerTests.SIMULATE以外へS2の承認が届いたら発注せず手法による見送りを発行する` |
| 3 | SIMULATE＋S2 で新規買いが保護レグなしで建玉として残り、監査・通知に免除が明示される | `OrderExecutionServiceStopLossMethodTests.SIMULATEでS2の新規買いは保護レグなしで建玉として残り免除の事実が出る`（逆指値 0 回・取消 0 回・手仕舞い 0 回・保護記録なし）・`S2でもエントリーが生きていなければ免除の事実は出ない`・`S2の再配送は発注も免除の事実も重ねない`／`OrderApprovedConsumerTests.SIMULATEでS2の新規買いは保護逆指値を発注せずProtectiveStopWaivedが発行される`／`AuditEntryFactoryTests`・`AuditEventConsumersTests`・`AuditCycleCompletenessTests`（免除の記録）／`NotificationFormatterTests`・`NotificationTemplateGoldenTests`・`NotificationConsumersTests`（Warning の通知） |
| 4 | 空売りは S2 でも S0 と同じ扱い | `OrderExecutionServiceStopLossMethodTests.空売りはS2でもS0と同じく逆指値を発注し未受理なら建玉を解消する`・`空売りはS2でも逆指値が受理されれば保護記録が作られる`／`StopLossMethodPolicyTests.空売りはSIMULATEのS2でも免除にならない` |
| 5 | S1/S3 は S0 と同じ扱い（未実装を記録） | `OrderExecutionServiceStopLossMethodTests.S1とS3と未知の手法はSIMULATEでS0と同じ扱いになる`／`StopLossMethodPolicyTests` |
| 6 | 変更は利用者のみ | `StopLossMethodEndpointTests`（未認証 401・サービスロール 403） |
| 7 | 承認に現在の手法が載る・現在値が読める | `StopLossMethodSettingsTests.承認は審査時点で有効な損切りの実行機構を運ぶ`／`StopLossMethodEndpointTests.SIMULATEではS2を選べ設定と統制状態と履歴に現れる`／`FrontendContractFixtureTests`（`stopLossMethod` を含む契約フィクスチャを再生成し、フロントの契約型へ `stopLossMethod: number` を追加） |

## 実装中に追加で判明したこと

- `GET /risk-controls/status`（SC-03 の参照）にも `stopLossMethod` を足した（計画は SC-03 に選択中の手法を出すと定める。表示は #823）。
- バックエンド↔フロントの契約フィクスチャ（`frontend/src/testing/contract-fixtures/risk-controls.{settings,status}.json`）は
  `UPDATE_CONTRACT_FIXTURES=1` で再生成し、フロントの契約型（`frontend/src/lib/risk/contracts.ts`）へ項目を足した（画面は変更しない）。
- `PUT /settings/broker-provider` の 400 文言に新しい拒否理由（S0 以外のまま実弾へ切り替えない）を足した。
- **BFF（`/bff/risk-controls/*`）へは経路を足さない。** BFF は経路を明示列挙しており（`RiskControlsBffEndpoints`・
  `BffPassThroughTests` の全経路表。MSP 側の同趣旨のテストもある）、画面が消費し始める #823 で基盤側と揃えて足す。
  それまでの選択は risk-management の `PUT /risk-controls/settings/stop-loss-method` を利用者トークンで直接呼ぶ。
- 計画 ADR のレンジ宣言（`.claude/rules/traceability.repo.md`）を `ADR-0001..0040` へ更新した（`check-trace-blocks.js` と
  `check-commit-messages.js` が ADR-0040 を実在として扱うため。計画リポ `origin/main` の `07_adr/` と公開 `kg-ranges.json` の実測）。
