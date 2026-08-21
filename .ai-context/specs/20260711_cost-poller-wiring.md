---
title: サービス間連携 継続（費用統制 → 定時サイクル poller への間隔延長/停止の配線）
type: spec
status: review
related_ids: [NFR, FR-01, FR-02, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: 費用統制の判定を定時サイクル poller へ配線する

> Issue [#79](https://github.com/endazon/ai-stock-trading/issues/79)（`Refs #23`・`Refs #22`）。費用統制（#23・PR #72）の
> `CostGovernor`（LLM 月次上限 80% で間隔延長・100% で停止）判定を、定時サイクルの起点である情報収集 poller
> （`CollectionPollingService`）へ同期 API で配線し、**実際に間隔延長/停止が起こる**ようにする。IADR-0028/0029/0030 の
> 同期 API 方式・フェイルセーフ既定を踏襲する。

## 起点となる計画書・課題（トレーサビリティ）

- NFR（費用）: LLM 月次上限超過時、定時サイクル間隔を自動延長（80%）・停止（100%）。05_trading-assumptions §6。
- FR-01/FR-02: 情報収集 poller（`CollectionPollingService`）が定時サイクル（`InformationCollected` → 取引判断）の起点。
- ADR: ADR-0001（Database per Service）。関連 IADR: IADR-0027（費用統制・CostGovernor）、方式は IADR-0028/0029/0030 を踏襲。
- 対象 Issue: #79（`Refs #23`・`Refs #22`）。

## コンテキストと課題

費用統制（#23）は `GET /costs/state` で `CostControlDecision`（State: Normal/Throttled/Halted・IntervalMultiplier）を提供するが、
poller 側は固定間隔（`PeriodicTimer`）で回っており、この判定を**参照していない**。よって「80% で間隔延長・100% で停止」が
判定されても実際のサイクルには反映されない（#23 の受け入れ条件「自動的に間隔延長/停止する」が未達）。

## 対象範囲

### 情報収集サービス `InformationCollectionService`（費用統制の照会・適用）

- Application: ポート `ICostControlGate`（`Task<CostControlGate> GetAsync(CancellationToken)`）と `CostControlGate`
  （`Halted` bool・`IntervalMultiplier` decimal）。
- Worker:
  - `HttpCostControlGate`（`GET {CostControl:BaseUrl}/costs/state` → `CostControlGate` に写像。JSON は `isHalted`/`intervalMultiplier`
    で疎結合に読む）。不達/非2xx/例外/タイムアウト/不正応答は**Normal（停止せず・1×）の安全既定**に倒す。
  - `PlaceholderCostControlGate`（Normal・初回 1 回警告）。
  - `CollectionPollingService` を費用統制対応に変更:
    - 各巡回の冒頭で `ICostControlGate.GetAsync` を照会。`Halted` なら収集/発行をスキップ（`InformationCollected` を出さない＝サイクル停止）。
    - 次回巡回までの間隔を `base × 実効倍率` に動的化（`Throttled`→2×、`Halted`→2×で再照会、`Normal`→1×）。`PeriodicTimer` から
      動的 `Task.Delay` ループへ変更（実効間隔は純関数 `EffectiveInterval` で算出・テスト可能）。
- `Program.cs`: `CostControl:BaseUrl` 未設定/不正 URI は `PlaceholderCostControlGate`＝安全既定でゲート、設定時のみ Http（解決時に構成を読む・5s タイムアウト）。

## フェイルセーフの方向（明示）

費用統制不達時の安全既定は **Normal（停止せず・base 間隔）**。理由: 月次予算は緩変で短時間の不達では超過しにくく、費用統制サービスの
一時障害で取引サイクル全体を止める（Halt）のは過大。緩和策: 実費用計測（#79 後続）と月次台帳による独立の上限管理、`LogWarning` の監視連携。
※これは「間隔延長/停止」を止められない側の縮退である点を IADR に明示する。

## 受け入れ基準

CI で緑にする範囲（ユニット＋fake HttpMessageHandler＋WebApplicationFactory）:
- [ ] `EffectiveInterval`: Normal=base、Throttled=base×2、Halted=base×2（再照会）を返す。
- [ ] `CollectionPollingService.RunOnceAsync`: `Halted` のとき収集があっても `InformationCollected` を発行しない。Normal では従来どおり発行する。
- [ ] `HttpCostControlGate`: 200（Throttled/Halted）応答を `CostControlGate` に写像する（fake handler）。
- [ ] 404/非 2xx/例外/タイムアウト/不正応答は Normal（停止せず・1×）に倒す。
- [ ] `CostControl:BaseUrl` 未設定は `PlaceholderCostControlGate`、設定時は `HttpCostControlGate`（選択テスト）。
- [ ] 既存の poller テストを緑に保つ（`ICostControlGate` 依存の追加）。

実 API/実コンテナ前提（CI 既定では実行しない）:
- [ ] 実 CostControlService への同期照会・service-to-service 認証（#76）付き E2E。

## 対象外（後続）

- 実 LLM 費用計測（platform ゲートウェイ）・月次上限のバージョン取得（#19）は #79 の後続スライス。
- 市場監視 poller への適用（本スライスは定時サイクルの起点＝情報収集 poller に限定）。service-to-service 認証（#76）。キャッシュ/リトライ。

## テスト方針

- `EffectiveInterval` は純関数で境界を検証。
- `CollectionPollingService` は fake ゲート（Normal/Halted）で発行有無を検証。
- `HttpCostControlGate` は fake `HttpMessageHandler`（200/404/500/タイムアウト）で写像とフェイルセーフを検証。選択は WebApplicationFactory で検証。

## 関連仕様

- 連携元: [20260710_cost-control](20260710_cost-control.md)（#23・CostGovernor）、[20260710_information-collection](20260710_information-collection.md)（poller）
- 先行: [20260711_position-store-wiring](20260711_position-store-wiring.md)（#22・同期 API 方式）
- 実装ADR: [IADR-0031](../adr/IADR-0031_cost-poller-wiring.md)

## 未決事項

- 実 LLM 費用計測・上限のバージョン取得・service-to-service 認証・市場監視 poller への適用は #79/#76 の後続で確定する。
