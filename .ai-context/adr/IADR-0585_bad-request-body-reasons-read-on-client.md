---
title: IADR-0585 本ユニットの 400 の本文 `{ error, details }` は変えず、画面が `ApiError.body` から理由を読む。本文の形は契約フィクスチャで縛る
type: impl-adr
status: Accepted
related_ids: [SC-01, SC-02, FR-10, FR-13, FR-19, FR-20, NFR, IADR-0503, IADR-0146, IADR-0496]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md（§SC-02「設定変更の一般則」。拒否の実効はサーバが持ち、画面は理由を示す）
related_specs:
  - ../specs/20261024_1465_bad-request-reasons-on-screen.md
---

# IADR-0585: 400 の理由は画面が本文から読む（サーバの本文の形は変えない）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-24
- 決定者: Claude Code（#1465 が「サーバとクライアントのどちらへ寄せるか」を実装に委ねた）

## コンテキスト

1. 本ユニットのサービスは、画面が叩く書き込みの 400 を**すべて** `{ error, details? }` で返す（SC-01 の前提条件、SC-02 の 8 端点、
   監視銘柄・監視パラメータ。走査は作業仕様書 §母集合）。RFC 7807（ProblemDetails / ValidationProblem）で返す端点は 0 件である。
2. 基盤の apiClient は、400 / 409 の本文のうち RFC 7807 の `errors` / `detail` / `title` だけを `ApiError.details` へ詰め、解析済みの本文を
   `ApiError.body` に残す（基盤 `lib/api/apiClient.ts`）。本ユニットの本文からは `details` が空になる。
3. その結果、画面の 9 つの書き込みフォームは、どれも一般文言「入力内容に誤りがあります。」だけを出し、サーバの理由を出していなかった（#1465）。
   単体試験のモックは理由を `ApiError.details` へ直接詰めており、本番と違う形を検証していたので、両側とも緑のまま見逃していた。
4. `{ "error": "…" }` は機械が読む契約でもある。Discord Bot の gRPC の書き込みの写し（`RiskControlsOwnerWriteGrpcService`・
   `WatchlistOwnerWriteGrpcService`）と通知サービス（`HttpStageGateController`）が `error` を読む。IADR-0503 決定 3 は「`error` の欄は変えない」と定めた。

## 決定

1. **サーバの 400 の本文は変えない**（`{ error, details? }` のまま）。
2. **画面は `ApiError.body` から理由を読む**（`frontend/src/lib/validationReasons.ts` の `validationReasonsOf`）。読む順は
   `ApiError.details`（RFC 7807 由来）→ `body.details`（文字列の配列）→ `body.error`（文字列）。文字列でない要素は捨てる。
   9 つのフォームの写し（`saveMessageOf` / `messageOf`）がこれを通す。
3. **400 の本文も契約フィクスチャで縛る**（IADR-0146 の機構）。`{ error, details }` の代表に損切りの実行機構の拒否、`{ error }` の代表に
   Stage 1 の最小取引件数の省略を、RiskManagementService の実端点から採る。単体試験は `serverBadRequest(body)`（本番の apiClient と同じく
   `details` は空・理由は `body`）でモックを作り、E2E もフィクスチャの本文で組む。

## 却下した案

- **サーバを ProblemDetails / ValidationProblem へ揃える**（#1465 の第一案）: 画面が叩く 400 の送出点だけで 7 サービス・約 20 か所あり、
  Bot の gRPC の写しと通知サービスが読む `error` を壊す（IADR-0503 決定 3 に反する）。`error` を拡張欄として残す形にしても、
  全サービスの 400 の試験（本文の形を断言するもの）の付け替えになり、本件（画面に理由が出ない）の直しに比べて大きすぎる。
  本ユニットの他の 400（Bot 専用の端点）とも形が割れる。将来、基盤に揃えて全面的に移るなら、`validationReasonsOf` は RFC 7807 も読むので画面の変更は要らない。
- **基盤の apiClient に `{ error, details }` を読ませる**: 基盤は本ユニットの形を知るべきでない（基盤の境界の外の都合）。本ユニットの側だけで閉じられる。

## 結果・影響

- 9 つのフォームで、サーバの理由が一般文言の後ろに括弧書きで出る。409 の扱い（一般文言・再試行なし）は変わらない。
- サーバの本文の形を変えると契約テストが赤になり、フィクスチャを再生成すると `riskContractFixtures.ts` の型（`BadRequestBody`）で `tsc` が赤になる。
- 残余リスク: MarketMonitorService と ConfigurationService の 400 は契約フィクスチャで縛っていない（形は同じ `{ error }` で、単体試験と E2E は
  本番の形のモックに改めた）。`ApiError.body` を渡さない apiClient に差し替わると理由が再び消える（基盤の apiClient は渡す。E2E のハーネスも渡す）。
