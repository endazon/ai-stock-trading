---
title: フロント SC-02 監視銘柄（watchlist）変更 UI
type: work
status: In Progress
related_ids: [FR-13, FR-03, FR-11, UC-06, SC-02, IADR-0084, IADR-0086, IADR-0088, IADR-0090]
issue: 196
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md  # FR-13（利用者が設定を変更できる）/ FR-03（監視）
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md          # UC-06（設定変更）
  - planning:projects/ai-stock-trading/05_screens/01_screens.md            # SC-01/SC-02（運用パラメータ：監視銘柄）
---

# 作業仕様書: フロント SC-02 監視銘柄（watchlist）変更 UI（#196）

## 目的 / 背景

Issue #191（PR #195・[IADR-0088](../adr/IADR-0088_watchlist-settings-api.md)）で監視銘柄（watchlist）の設定ストア側
バックエンド API を整備した（MarketMonitorService の `GET/POST/DELETE /monitor/watchlist` ＋ `GET /monitor/watchlist/history`・
owner 認可・理由必須・楽観排他・変更履歴）。#188（FR-13 残 UI）で「バックエンド API 未整備」としてブロックされていた
**監視銘柄 UI** の後続にあたる。本作業はこの API を消費する**監視銘柄の変更 UI** を SC-02（`settings/risk`）へ追加する。
設計判断は [IADR-0090](../adr/IADR-0090_frontend-watchlist-ui.md)。

## スコープ（フロントエンド・`frontend/` 閉域）

- SC-02（`RiskSettingsPage`）に**監視銘柄セクション**（`WatchlistForm`）を追加。MarketMonitorService の
  `GET/POST/DELETE /monitor/watchlist`・`GET /monitor/watchlist/history` を消費する（**バックエンド既存・無改修**）。
  - **一覧表示**: 監視銘柄（`symbol`・`market`）を表で表示。市場は数値 enum をラベルへ写像。
  - **追加**: 銘柄コード＋市場＋**理由必須**で `POST /monitor/watchlist`。
  - **削除**: 破壊的操作のため**明示確認**（削除理由必須＋確認ボタンの 2 段）で `DELETE /monitor/watchlist`（body に理由）。
  - #186/#188 の作法踏襲: **理由必須**・検証(400)・競合(409) を安全側処理（メッセージ表示・**破壊的自動再試行なし**）。
  - 数値 enum ↔ ラベルの写像はフロントに閉じる（未知値は安全側フォールバック・IADR-0084 決定 4 踏襲）。
    `market` は `risk/contracts` の `marketLabel`/`MARKET_OPTIONS` を再利用。履歴の `changeType`
    （`MonitorSettingsChangeType`）は新規に写像する（0=追加・1=削除）。
- 監視銘柄セクションは**リスク設定とは別サービス（MarketMonitorService）**のため、**独立してロード/縮退**する
  （リスク設定の取得可否に連動しない・IADR-0090 決定 1）。
- feature/route/nav は既存（`sc02-risk-settings` / `settings/risk`）を再利用（新規 feature を増やさない）。
- 依存規則は IADR-0080/0084 踏襲: `@foundation/*` と `TradingRole` のみ・`test/foundation-stub` ＋ローカル vitest で自己完結。

### 非スコープ（明示分離・フォローアップ）

- **実 BFF の `/monitor/*` 合成点（プロキシ）**: BFF は microservices-platform（MSP）側にあり、`risk-controls` の合成点
  （MSP #287）と同様に**別リポの後続作業**。フロントは論理パス `/monitor/*`（apiFetch が `/bff` を前置）を叩き、
  実プロキシ結線は MSP フォローアップ issue で扱う。E2E は `page.route('**/bff/**')` のモックで契約（パス/メソッド）を検証する。
- **段階（stage）の直接変更**: 段階遷移は段階ゲート承認フロー（#20/#165 Discord Bot）へ一元化（IADR-0084）。本 UI では開かない。
- **監視の変動閾値・収集間隔**の変更 UI（`PUT /monitor/settings`）は本 issue 対象外（監視銘柄に限定）。必要なら別 issue。

## 設計（要点・詳細は IADR-0090）

- **自己完結セクション**: `WatchlistForm` は自前で `GET /monitor/watchlist`（一覧）と `GET /monitor/watchlist/history`（履歴）を
  読み、`status`（loading/ok/notFound/error）と `historyStatus` を独立管理する。`RiskSettingsPage` はページ末尾に無条件で
  レンダリングする（リスク設定の status に連動しない）。
- **追加/削除は個別 API**（全置換ではない）: バックエンド（#191/IADR-0088）は POST/DELETE の**個別操作**であり、
  クライアント側でマージしない。それぞれ**理由必須**。enum は**数値**で往復（Worker は `JsonStringEnumConverter` 非設定）。
- **削除の明示確認（fail-safe）**: 監視からの削除は破壊的（対象が監視・検知・取引の対象外になる）。行の「削除」を押すと
  インライン確認（削除理由入力＋「監視から削除」確認ボタン＋キャンセル）を開き、理由が入るまで確定不可。**自動再試行なし**。
  追加は非破壊（監視対象を増やす＝厳格化方向）のため 1 段（理由必須のみ）。
- **失敗縮退**: 一覧 GET が 404（BFF 未結線含む）→「監視銘柄設定は利用できません。」へ縮退（IADR-0009 と整合）。
  追加/削除の 409 は競合、400 は詳細つきメッセージ、403 は権限メッセージへ写像。履歴の取得不能は履歴領域のみ縮退。
- **重複/不在**: 重複追加・不在削除はサーバ 400（#191）。クライアントは空欄（symbol/reason）のみボタン無効化で抑止し、
  実効な重複/不在検証はサーバに委ねる（#186 と同方針）。

## 受け入れ基準（issue #196）→ テスト写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 監視銘柄の一覧表示・追加・削除 UI を SC-02 に追加し #191 API（owner）を消費する | `WatchlistForm.test.tsx`：一覧描画（市場ラベル写像）／POST が `{symbol,market,reason}` で送出／DELETE が確認後に `{symbol,market,reason}` で送出／履歴の changeType 写像 |
| 2 | 理由必須・検証(400)・楽観排他(409)・権限(403)・破壊的自動再試行なし・危険操作は明示確認 | `WatchlistForm.test.tsx`：追加は理由未入力でボタン無効／削除は確認 2 段（理由必須）／409 で競合表示・再試行 1 回のみ／400 で詳細表示／403 で権限メッセージ |
| 3 | 数値 enum↔ラベル写像はフロントに閉じ未知値フォールバック | `monitor/contracts.test.ts`：`marketLabel`/`monitorChangeTypeLabel` の既知値・未知値フォールバック |
| 4 | BFF/合成点の結線を確認・整備（E2E は契約検証） | `e2e/sc02-risk-settings.spec.ts`：`GET/POST/DELETE /monitor/watchlist` のパス/メソッドを page.route で追認。実プロキシは MSP 後続へ分離 |

## 検証

- `cd frontend && npm ci && npm run typecheck && npm run lint && npm run test`（Vitest）が緑。
- 実ブラウザ E2E（Playwright）は CI の `frontend-e2e` ジョブで実行（モック BFF・実クラスタ非依存）。
- 実 BFF/`/monitor/*` プロキシ疎通は実基盤依存として MSP フォローアップへ分離。CI は既存ジョブで実行（`ci.yml` は追加のみ・既存保持）。
