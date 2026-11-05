---
title: 外部から受け取る値の検証を締める（市場データの時刻・外部の HTTP の応答の上限・OpenD の再認証の入力待ちの判定）
type: spec
status: accepted
related_ids: [NFR, NFR-05, FR-01, FR-05, FR-10, ADR-0002, ADR-0004, ADR-0024, IADR-0614, IADR-0068, IADR-0322, IADR-0469]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-01 情報収集・FR-10 判定入力)
---

# 仕様書: 外部から受け取る値の検証（#1532）

## 起点

- #1532。外部から受け取る値の扱いを点検し、異常な入力のときの振る舞いを締める。平常の入力では振る舞いを変えない。

## 決定（IADR-0614）

1. **Unix 秒の写しは例外を投げない**（`UnixTime.TryFromSeconds`。Shared.Infrastructure）。負・表せる範囲の外は不正とする。
   - 市場データの現在値（`FinnhubQuoteClient`）: 時刻が不正、または未来へ 5 分（`FutureTimestampTolerance`）を超えたら取得不可（null）。0（欠け）は従来どおり受信の時刻。
   - レート制限の応答のヘッダの時刻: 不正なら「無い」と同じ扱い（従来の分類のまま null を返す）。
   - 企業ニュース（情報収集）: 発行時刻が 0 以下・不正な記事は採らない（同じ銘柄の他の記事は残す）。
   - 企業ニュース（Stage 0 の as-of）: 不正な時刻は「不明」（null。0 以下と同じ扱い）。
2. **外部を取る名前付き HttpClient に応答の本文の上限**（`ExternalHttpResponseLimits.MaxResponseBytes` = 16 MiB）を置く。超えたら `HttpRequestException` になり、各アダプタの既存の「取得できない」の経路へ倒れる。
3. **OpenD の再認証**: 最後の検証コードの案内より後に `Login successful` があれば入力待ちとしない（`ConsoleTail.DetectPrompt`）。入力待ちでなければ従来どおり 409・何も書かない。

## 母集合（規則 9）

- **Unix 秒の写し**: `git grep -n 'FromUnixTimeSeconds(' -- backend ':!*Tests*' ':!*Migrations*'` → `FinnhubQuoteClient`（応答の `t`・ヘッダのリセット 2 箇所）・`FinnhubCompanyNewsSource`・`AsOfReferenceFeeds`（Finnhub の as-of）。いずれも直した。リセットの 2 行（`previousUnix`）は自分が保存した値で、検証済みの値しか入らない。
- **外部を取る名前付き HttpClient**: `git grep -n 'CreateClient(' -- backend/Services ':!*Tests*'` から外部の相手のものを選んだ:
  - `marketdata`（MarketMonitor・RiskManagement・Report・TradeDecision）・`fx`（RiskManagement・Report・TradeDecision）・`collection`（InformationCollection）・`backtest-bar-data`（Backtest）・`stage0-asof-external`（TradeDecision）。計 10 登録。
  - **除外**: サービス間（`risk`・`monitor`・`reports`・`order-execution`・`cost`・`risk-ledger`・`audit-ledger`・ボットの各クライアント）。相手は自分のサービスで、応答は自分が組む。LLM のゲートウェイ（`llm`・`report-llm`）は基盤のサービスで、応答の長さは出力のトークン数の上限で決まる。InformationCollection の無名の `AddHttpClient()` は名前付きのクライアントから使われていない（情報源はすべて `collection`）。
  - **流し読み**: 外部のアダプタは `ReadFromJsonAsync` / `ReadAsStringAsync`（バッファする読み方）だけで、`ResponseHeadersRead` は使っていない（`git grep -n HttpCompletionOption -- backend` が 0 件）。
- **OpenD の入力待ちの判定**: `ConsoleTail.DetectPrompt` の呼び手は `GET /state` と `POST /verify` の 2 つ（同じ関数）。

## 受け入れ基準

- [x] 範囲外・未来の時刻の応答は取得不可になり、例外が抜けない（`FinnhubMarketDataSourceTests` に 6 件。企業ニュースは `NewsInformationSourceTests` に 3 件）。
- [x] 外部の HTTP クライアントに応答の上限があり、超えた応答は取得の失敗になる（各サービスの配線の試験 6 ファイル・`上限を超える応答は取得不可として扱う`）。
- [x] ログインの成功の後は入力待ちと判定せず、コードを受け付けない（`ConsoleTailTests` 3 件・`OpendAuthEndpointsTests` 1 件。旧い実装で 3 件が赤であることを確かめた）。
- [x] 既存の試験が緑のまま。

## 範囲外

- サービス間の応答の上限（相手は自分のサービス）。
