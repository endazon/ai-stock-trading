---
title: フロント SC-02 ガード変更 UI（FR-13 残）
type: work
status: In Progress
related_ids: [FR-13, FR-19, UC-06, SC-02, ADR-0007, IADR-0084, IADR-0086]
issue: 188
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md  # FR-13（利用者が設定を変更できる）
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md          # UC-06（設定変更）
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
---

# 作業仕様書: フロント SC-02 ガード変更 UI（FR-13 残・#188）

## 目的 / 背景

Issue #106 / PR #186（[IADR-0084](../adr/IADR-0084_frontend-risk-settings-and-control-status.md)）は FR-13 の中核である
**リスク上限（`limits` 8 項目）の変更**のみを実装し、ガード（`guard`）・段階（`stage`）は参照表示に留めた（IADR-0084 決定 2）。
本作業は残スコープのうち **ガード変更 UI** を SC-02（`settings/risk`）へ追加する。設計判断は [IADR-0086](../adr/IADR-0086_frontend-guard-edit-ui.md)。

## スコープ

- SC-02（`RiskSettingsPage`）に**ガード変更 UI** を追加。`PUT /risk-controls/settings/guard` を消費する（**バックエンド既存・無改修**）。
  - 有効な商品種別（現物/信用）・有効な市場（日本/米国）のチェックボックス編集。
  - 禁止銘柄（`bannedSymbols`）の追加/削除（`symbol`・`market`・`reason`・`registeredOn`）。
  - 同日再エントリー禁止（`preventSameDayReentry`）・相場操縦パターン禁止（`prohibitManipulativeOrderPatterns`）のトグル。
  - #186 の作法踏襲: **理由必須**・検証(400)・競合(409) を安全側処理（メッセージ表示・**破壊的自動再試行なし**）。
  - **危険な緩和は明示確認**（fail-safe）: ガード無効化（いずれかのトグル OFF 化）・禁止銘柄の削除・信用（Margin）の新規有効化を
    含む送信は、専用の確認チェックを ON にするまで保存不可とする。
  - 数値 enum ↔ 選択肢の写像はフロントに閉じる（未知値は安全側フォールバック・IADR-0084 決定 4 踏襲）。
- feature/route/nav は既存（`sc02-risk-settings` / `settings/risk`）を再利用（新規 feature を増やさない）。
- 依存規則は IADR-0080/0084 踏襲: `@foundation/*` と `TradingRole` のみ・`test/foundation-stub` ＋ローカル vitest で自己完結。

### 非スコープ（明示分離・フォローアップ）

- **監視銘柄（watchlist）設定 UI**: 設定ストア側に変更 API が未整備。**バックエンド起票が先行**（本 PR ではフォローアップ issue を
  P3 で起票し着手しない。issue #188 受け入れ基準 2 の通り）。
- **段階（stage）の直接変更**: 段階遷移は段階ゲート承認フロー（#20/#165 Discord Bot）へ一元化する方針（IADR-0084）。
  UI から `PUT /settings/stage` は開かない。
- 実ブラウザ E2E（Playwright）・実 BFF/Keycloak 疎通は実基盤依存として後続（#187 系）へ分離。

## 設計（要点）

- **1 画面 2 フォーム**: 既存のリスク上限フォームに加え、`aria-label="取引ガードの変更"` の第 2 フォームを追加する。
  参照専用だった `GuardView` を編集可能な `GuardForm` へ置換する（段階 `StageView` は参照のまま）。
- **フォーム状態**: 商品種別/市場は選択集合（`Set<number>`）、禁止銘柄は配列、トグルは boolean、理由は文字列で保持する。
  送信時に `GuardUpdateRequest`（`{ enabledProductTypes, enabledMarkets, bannedSymbols, preventSameDayReentry,
  prohibitManipulativeOrderPatterns, reason }`）へ整形する。enum は**数値**で往復（Risk Worker は `JsonStringEnumConverter` 非設定）。
- **PUT は全置換**: エンドポイントは差分 PATCH ではなく全置換。現在値をフォーム初期値に読み込み、編集後の全体を送る。
- **危険変更の検出**: 現在値と送信予定を比較し、(a) いずれかのトグルを OFF 化、(b) 禁止銘柄の削除、(c) 信用の新規有効化 を
  「危険な緩和」として列挙する。1 件以上あれば確認チェック未 ON で保存を無効化し、確認文に該当項目を明示する。
- **理由必須・空集合の扱い**: 理由が空、または危険確認未 ON（危険変更がある場合）は保存無効。商品種別・市場が空集合でも
  黙って送らず、実効な範囲検証はサーバ 400 が担う（#186 と同方針：クライアントは明白な無効のみ抑止）。
- **失敗縮退**: 409 は競合メッセージ、400 は詳細つきメッセージ、403 は権限メッセージへ写像（既存 `saveMessageOf` を再利用）。
  成功後は現在値・履歴を再取得し、危険確認を解除する。

## 受け入れ基準（issue #188）→ テスト写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | ガードの各項目を利用者が変更でき、理由必須・409/400 の安全側処理が入力検証テストで固定される | `RiskSettingsPage.guard.test.tsx`：現在値の反映／商品種別・市場・トグル・禁止銘柄の編集が payload に反映／理由必須で保存無効／409 で競合表示・再試行なし／400 で詳細表示 |
| 2 | 監視銘柄設定は対応するバックエンド API の整備後に着手（別 issue で先行） | 本 PR 非対象（フォローアップ issue を P3 起票）。段階の直接変更 UI が無いことを既存 `RiskSettingsPage.test.tsx` で担保 |

補助: 危険な緩和（トグル OFF・禁止銘柄削除・信用有効化）で確認チェック未 ON なら保存無効（fail-safe）をテストで固定する。

## 検証

- `cd frontend && npm ci && npm run typecheck && npm run lint && npm run test`（Vitest）が緑。
- 実ブラウザ E2E（Playwright）・実 BFF/Keycloak 疎通は後続へ分離。CI は既存 `frontend` ジョブで実行（`ci.yml` は追加のみ・既存保持）。
