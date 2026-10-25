---
title: 書き込みの 400 の文言から .NET の引数名接尾辞・enum 名・英語の既定文言を除き、日本語に揃える
type: spec
status: done
related_ids: [SC-01, SC-02, FR-03, FR-10, FR-13, FR-17, FR-19, NFR-06, IADR-0586, IADR-0585, IADR-0509, IADR-0503]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
---

# 仕様書: 利用者へ見せる 400 の文言を日本語に揃える（#1472）

> 本仕様書は実装着手前に作成した（AST `origin/develop`。#1471 のマージ後）。

## 起点（トレーサビリティ）

- 起点: #1472（#1471 / #1470 で 400 を契約フィクスチャとして採ったときに見つかった）。#1469（IADR-0585）以後、画面は 400 の `error` / `details` をそのまま出す。
- 判断を伴う決定（接尾辞を落とす位置・表示名の対応表の置き場）は IADR-0586 に残す。

## 文言を作るコードの所在（着手前の確認）

- 応答へ載せる文言は `ClientFacingErrors.MessageFor`（`backend/TestSupport/AiStockTrading.TestSupport.PlatformShim`）が決める。
  名は「足場」だが、本リポから組む全サービスの配備で動く実行時の配線である（同 csproj の注記・IADR-0013 の 2026-10-07 追記）。
- 基盤（MSP）に `ClientFacingErrors` は無い（`grep -rln ClientFacingErrors /home/user/microservices-platform/src` は 0 件）。
  **本番の文言は AST 内で作られる。MSP の変更は要らない。**

## 母集合（規則 9）

引き方: `grep -rn "ClientVisible()" backend` と `grep -rn "ClientVisibleArgument.ThrowIfNullOrWhiteSpace" backend`（印つきの送出＝応答へ文言が載るもの）、
`grep -rn "ClientFacingErrors.MessageFor" backend`（文言を載せる経路）、`grep -rn "Results.BadRequest\|StatusCode.InvalidArgument" backend/Services`（端点が直接組む 400）。

### 文言を載せる経路（6 か所。いずれも `MessageFor` を通る）

| 経路 | 端点 | 画面 / Bot |
| --- | --- | --- |
| `MonitorSettingsEndpoints.MapException` | 監視銘柄の追加・削除・入れ替え案の適用、変動閾値、クールダウン、監視設定 | SC-02 画面、Bot（`WatchlistOwnerWriteGrpcService` が同じ写しを呼び、`error` を gRPC の detail にする） |
| `RiskControlEndpoints` の写し | SC-02 のリスク統制の書き込み | SC-02 画面、Bot（`RiskControlsOwnerWriteGrpcService`） |
| `RiskControlsReadGrpcService` | gRPC の読み取り | Bot |
| `ReportEndpoints` / `ReportOwnerReadGrpcService` | 報告書 | Bot |
| `AssumptionsEndpoints` | SC-01 前提条件 | SC-01 画面 |
| `CostControlEndpoints` | 費用統制 | SC-01 画面 |

**Bot の文言は REST と同じ `MessageFor` を通る**（gRPC の写しは REST の処理関数と `MapException` を呼び、本文の `error` を detail にする）。
したがって `MessageFor` で直せば画面と Bot の両方に効く。

### 印つきの送出（文言が利用者へ届くもの）と、3 つの欠陥への該当

| 送出 | 接尾辞 | enum 名 | 英語の既定文言 |
| --- | --- | --- | --- |
| `ClientVisibleArgument.ThrowIfNullOrWhiteSpace`（理由 6 か所〔監視 2・リスク統制 3・前提条件 1〕・銘柄コード 1 か所） | 有 | — | **有**（`The value cannot be an empty string …` / `Value cannot be null.`） |
| `MonitorWatchlistService`（既に監視対象・巡回に収まらない・監視対象にない） | 有 | **有**（`UnitedStates`） | — |
| `MonitorWatchlistService`（未定義の市場・入れ替え案の形） | 有 | — | — |
| `MonitorSettingsService`（変動閾値・クールダウン・銘柄コード欠落・未定義の市場・重複） | 有 | — | — |
| `MonitorSettingsService`（巡回に収まらない置換の「収まらない追加」） | 有 | **有**（`AAPL@UnitedStates`） | — |
| `MonitorSettingsEndpoints.MarketOf`（`market は必須です。`） | 有 | — | — （項目名が英語のみ。後述） |
| `RiskSettingsService`（口座種別と商品種別の組み合わせ） | 有 | **有**（`Cash`・`MarginLong` / `ShortSell`） | — |
| `RiskSettingsService`（段階の既定発注先・閉場前・テーマ） | 有 | — | — |
| `RiskLimitBounds` / `HighVolatilitySettings` | 有 | — | — |
| `Stage1TradeCountBounds`（`ArgumentOutOfRangeException`） | 有 | — | **有**（`Actual value was N.` の行） |
| `ReportDraftService`（PeriodKey の不一致） | 無（引数名なし） | **有**（`Daily`） | — |

### 除外とその理由

- **端点が直接組む 400**（`Results.BadRequest(new { error = "…" })`・gRPC の `InvalidArgument` の直書き）: すでに日本語で、接尾辞も enum 名も無い。
  `reason（理由）は必須です。` のような「項目名（日本語）」の書き方は本ユニットの既存の作法であり、本 issue の 3 つの欠陥に当たらない。
  ただし `market は有効な市場（Japan/UnitedStates）を指定してください。`（`ClosePosition` / `AdoptPositionDrift`）は、受け付ける**入力値**を示す文で、
  画面は enum を数値で送るため通常の操作では出ない。変えると契約・既存試験の期待値が動くので、本 issue では変えない（画面とテストの変更を最小にする）。
- **印の無い例外**: 固定文言（`要求の内容が正しくありません。`）に置き換わり、元の文言は出ない（IADR-0509）。
- **入れ替え案の内訳（`WatchlistProposalPlan` の項目ごとの理由）**: 例外を経ない文字列で、接尾辞も enum 名も無い。
- **`RiskSettingsService` の文言に含まれる `ADR-0021 決定4-4`**: 本 issue の 3 つの欠陥ではない。残す（報告のみ）。
- **`MonitorSettingsService` の未定義の市場（`X@99`）・`市場 99 は未定義です。`**: 未定義の値には表示名が無いので数値のまま出す。

## 文言の方針

1. **接尾辞を落とす**: `MessageFor` が、印つきの例外の文言から ` (Parameter 'x')` と、`ArgumentOutOfRangeException` の
   `Actual value was N.` の行を落とす。落とす末尾は、同じ `ParamName`（と `ActualValue`）で空の文言の例外を作って**実行時の文化のまま**得る
   （英語の文字列を直書きしない）。例外の型・`ParamName`・`Message` は変えない（ログ・試験は従来どおり）。
2. **英語の既定文言をやめる**: `ClientVisibleArgument.ThrowIfNullOrWhiteSpace` が日本語の文言（`理由は必須です。` / `銘柄コードは必須です。`）で投げる。
   呼び出し側が項目の日本語名を渡す。例外の型（null は `ArgumentNullException`）と `ParamName` は変えない。
3. **enum は表示名**: 市場は `日本` / `米国`、口座種別は `信用口座` / `現金口座`、商品種別は `現物` / `信用買い` / `空売り`、報告書の種別は `日報` / `週報` / `月報`。
   取引の語の対応表は `Shared.Contracts`（`TradingTermDisplay`。`OrderStatusDisplay` と同じ置き場）、報告書の種別は ReportService 内の既存の呼び名（`ReportSummary.KindLabel`）を使う。
4. `market は必須です。` は `market（市場）は必須です。` にする（項目名だけの英語を、本ユニットの「項目名（日本語）」の作法へ揃える）。

## 受け入れ基準

- A1: 印つきの例外の応答の文言に ` (Parameter '` と `Actual value was` が出ない（`ClientFacingErrorsTests`。`ArgumentException` / `ArgumentNullException` /
  `ArgumentOutOfRangeException`、引数名なし）。例外の `Message` 自体は変わらない。
- A2: 理由・銘柄コードの空欄は日本語の文言で 400（`ClientVisibleArgument` の試験・前提条件の契約フィクスチャ）。null と空白で例外の型が従来どおり。
- A3: 監視銘柄の追加・削除・置換、口座種別の組み合わせ、報告書の PeriodKey の文言が enum 名でなく表示名（各サービスの試験）。
- A4: 契約フィクスチャ（`*.400.json`）を `UPDATE_CONTRACT_FIXTURES=1 dotnet test` で採り直し、接尾辞・enum 名・英語が無い。
- A5: Bot の経路（`WatchlistOwnerWriteGrpcService` の detail）にも接尾辞が出ない（試験）。
- A6: 変異: `MessageFor` の除去を外すと A1・A4・A5 が赤になる（実測）。
- A7: frontend の typecheck・lint・vitest・Playwright が緑（フィクスチャ経由のモックのため期待値の書き換えは無い見込み）。
