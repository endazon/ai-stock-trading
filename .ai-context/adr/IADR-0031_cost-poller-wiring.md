---
title: IADR-0031 費用統制の間隔延長/停止は定時サイクル poller が同期照会して適用する
type: impl-adr
status: Accepted
related_ids: [NFR, FR-01, FR-02, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0031: 費用統制の間隔延長/停止は定時サイクル poller が同期照会して適用する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: NFR（費用・LLM 月次上限）、FR-01（情報収集 poller）、FR-02（定時サイクル）、ADR-0001
- 対象 Issue: [#79](https://github.com/endazon/ai-stock-trading/issues/79)（`Refs #23`・`Refs #22`）
- 関連する実装仕様書: [20260711_cost-poller-wiring](../specs/20260711_cost-poller-wiring.md)
- 関連 IADR: [IADR-0027](IADR-0027_cost-control.md)（費用統制・CostGovernor）、[IADR-0028](IADR-0028_daily-policy-sync-api.md)/[IADR-0029](IADR-0029_sizing-context-sync-api.md)/[IADR-0030](IADR-0030_position-store-sync-api.md)（同期 API 方式・踏襲）

## コンテキストと課題

費用統制（#23・IADR-0027）は `GET /costs/state` で `CostControlDecision`（State: Normal/Throttled/Halted・IntervalMultiplier）を
提供するが、定時サイクルの起点である情報収集 poller（`CollectionPollingService`）は固定間隔（`PeriodicTimer`）で回り、この判定を
参照していない。よって「80% で間隔延長・100% で停止」が判定されても実サイクルに反映されず、#23 の受け入れ条件が未達である。

## 決定

- **定時サイクル poller が費用統制を同期照会して適用する**（IADR-0028/0029/0030 と同方式）。情報収集サービスにポート
  `ICostControlGate`（`Task<CostControlGate> GetAsync`・`CostControlGate = (Halted, IntervalMultiplier)`）を置き、`HttpCostControlGate`
  が `GET {CostControl:BaseUrl}/costs/state` を写像する（JSON は `isHalted`/`intervalMultiplier` で疎結合に読む。CostControl.Domain 型は参照しない）。
- **poller の適用**: 各巡回冒頭で照会し、`Halted` なら収集/発行をスキップ（`InformationCollected` を出さない＝サイクル停止）。次回間隔を
  `base × 実効倍率`（Throttled→2×、Halted→2× で再照会、Normal→1×）へ動的化する。`PeriodicTimer` から動的 `Task.Delay` ループへ変更し、
  実効間隔は純関数 `EffectiveInterval` で算出する（テスト可能）。
- **フェイルセーフ**: 費用統制不達・非 2xx・タイムアウト・不正応答は **Normal（停止せず・1×）** に倒す。
- **安全既定でゲート**: `CostControl:BaseUrl` 未設定/不正 URI は `PlaceholderCostControlGate`（Normal）。構成で有効化時のみ実照会（解決時に構成を読む・5s タイムアウト）。
- **適用範囲**: 本スライスは定時サイクルの起点＝情報収集 poller に限定する。市場監視 poller（価格駆動・別系統）への適用は対象外。

## 理由

- 費用統制の所有（判定）と適用（poller）を分離し、poller が照会のみで従う（IADR-0028/0029/0030 と一貫・Database per Service 維持・可逆）。
- 実効間隔を純関数化して境界（Normal/Throttled/Halted）を決定的にテストできる。

## フェイルセーフの方向（明示）

daily-policy/sizing-context の「取引しない」（保守側）と異なり、費用統制不達時は **Normal（停止も間隔延長もしない）** に倒す。
これは「間隔延長/停止を止められない側」の縮退である。理由: 月次予算は緩変で短時間の不達では超過しにくく、費用統制の一時障害で
取引サイクル全体を止める（Halt）のは過大。緩和策: 実費用計測（#79 後続）と月次台帳による独立の上限管理、`LogWarning` の監視連携。

## 結果

- 良い影響: 費用統制の判定が実際にサイクル間隔へ反映され、#23 の受け入れ条件「自動的に間隔延長/停止する」が定時サイクルで満たされる。
- 悪い影響・トレードオフ: 実 LLM 費用計測（platform ゲートウェイ）・月次上限のバージョン取得（#19）は未配線のため、実費用に基づく統制は後続。
  費用統制不達時は Normal（統制されない側）に倒れる。
- フォローアップ: 実 LLM 費用計測・上限のバージョン取得（#79 後続）、service-to-service 認証（#76）、市場監視 poller への適用、キャッシュ/リトライ。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0027](IADR-0027_cost-control.md)（CostGovernor）、[IADR-0030](IADR-0030_position-store-sync-api.md)（同期 API 方式）
