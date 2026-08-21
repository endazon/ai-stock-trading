---
title: 費用統制 poller の s2s 配線を完成させる（GET /costs/state の間隔延長/停止を実運用で有効化）— Issue #79
type: spec
status: draft
related_ids:
  - NFR
  - FR-02
  - IADR-0027
  - IADR-0031
  - IADR-0051
author: claude
created: 2026-07-16
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR: 費用統制)
related_specs:
  - "../adr/IADR-0051_service-to-service-auth.md（s2s 認証・読み取りは OwnerOrService）"
  - "20260715_79_llm-cost-metering-impl.md（#79 の費用計測スライス・PR #134 でマージ済）"
---

# 仕様書: 費用統制 poller の s2s 配線を完成させる（Issue #79）

## 起点となる計画書（トレーサビリティ）

- 非機能: **NFR**（費用統制）／機能: FR-02（定時サイクル）
- 関連 IADR: **IADR-0031**（poller が `GET /costs/state` を同期照会し間隔延長/停止を適用）／
  **IADR-0051**（s2s 認証・**読み取りは OwnerOrService**・書き込みは OwnerOnly＝最小権限）／IADR-0027（費用統制）
- Issue: [#79](https://github.com/endazon/ai-stock-trading/issues/79) の残スコープ「poller 配線」

## 目的・背景

#79 の「実 LLM 費用計測」は PR #134 でマージ済みで、**費用は実際に月次台帳へ計上される**ようになった。
一方 poller 側（情報収集の定時サイクル）は、ゲート適用ロジック（`CollectionPollingService.EffectiveInterval`・
Halted スキップ）と `HttpCostControlGate` が**実装済みでありながら実運用では機能していない**。理由は
`HttpCostControlGate` のコメントが明示している:

> 注意（#76 依存）: `/costs/state` は OwnerOnly のため、service-to-service 認証（#76）が入るまで本呼び出しは
> 認証ヘッダなし＝常に 401 → Normal に倒れる。よって `CostControl:BaseUrl` 設定後も #76 完了までは
> 間隔延長/停止は実運用で無効。

その **#76 / IADR-0051（s2s 認証）は既にマージ済み**（`AddAiStockTradingServiceToken`・`OwnerOrService`）。
`RiskControlEndpoints`・`ReportEndpoints` は読み取り系を `OwnerOrService` へ分離済みだが、
**`CostControlEndpoints` だけ全体が `OwnerOnly` のまま**取り残されている。この積み残しを解消し、
費用統制の間隔延長/停止を実運用で有効化する（＝#79 のクローズ条件）。

## 対象範囲

**対象（本 PR）**
- `CostControlEndpoints`: **読み取り系（`/state`・`/review`）を `OwnerOrService` へ分離**（IADR-0051 の既存パターン
  と同形）。**書き込み系（`/record`）は `OwnerOnly` 据え置き**（サービスへ書き込み権限を与えない＝最小権限）。
- 情報収集 Worker: `HttpCostControlGate` 用 HttpClient に `AddAiStockTradingServiceToken` を付与（他 s2s 呼び出しと同形）。
  併せて陳腐化した「#76 依存で常に 401」コメントを実態へ更新する。
- chart: 情報収集へ `CostControl__BaseUrl`（＝`http://cost-control-service:8080`）と `ServiceAuth__ClientId/Secret` を注入。

**対象外**
- **上限のバージョン取得**（`DefaultCostLimitsProvider` → #19 のバージョン付き前提条件）。#19／#22 の後続。
- 費用計測そのもの（PR #134 でマージ済）。

## 設計

- **最小権限**（IADR-0051）: 読み取り（統制状態の照会）はサービスに許可、**書き込み（費用計上）は許可しない**。
  費用計上はイベント（`LlmCostIncurred`・IADR-0055）で行うため、サービスに `/costs/record` は不要。
- **fail-safe は不変**（IADR-0031）: 照会不達・401・例外は `Normal`（停止せず・1×）へ倒す。費用統制の一時障害で
  取引サイクル全体を止める（Halt）のは過大であるため。`ServiceAuth` 未設定なら従来どおり no-op（認証なし→401→Normal）。
- **chart の既定**: `CostControl__BaseUrl` を実サービスへ向ける。単価既定 0（#134）のため費用は 0 円で
  積み上がらず、**有効化しても Normal のまま**＝挙動は変わらない（実単価投入時に初めて効く）。

## 受け入れ基準

- [ ] `GET /costs/state`・`/costs/review` がサービストークン（`trading-service`）で **200**（従来 403/401）
- [ ] `POST /costs/record` はサービストークンで **403**（書き込みは与えない＝最小権限）を維持
- [ ] 無トークンは **401**（据え置き）
- [ ] `HttpCostControlGate` がサービストークンを付与して照会する（`ServiceAuth` 未設定時は従来どおり no-op→Normal）
- [ ] chart で情報収集に `CostControl__BaseUrl`＋`ServiceAuth__*` が注入される
- [ ] 照会失敗時に `Normal` へ倒す fail-safe が維持される（回帰）

## テスト方針

- 単体（xUnit）: エンドポイントの認可（OwnerOrService で 200／サービストークンの書き込みは 403／無トークン 401）は
  既存の `CostControlEndpointsTests` 系に準拠して追加。`HttpCostControlGate` は fake handler で
  「トークン付与時も従来の写像・fail-safe が不変」を確認。
- 実 Keycloak トークンでの s2s 疎通は、既存の `ServiceTokenSyncQueryE2ETests`（IADR-0050/0051）に費用統制を
  追加して実基盤 E2E で担保する（#82 の E2E 基盤・Docker 無し環境でも #136 の外部注入経路で実走可能）。

## 計画書との差異

- なし（IADR-0031 が定めた poller 適用を、IADR-0051 の s2s 認証で実際に成立させるだけ）。

## 未決事項

- 上限のバージョン取得（#19）。本 PR 後の #79 の扱い（クローズ or #19 待ちで継続）は実装後に判断する。
