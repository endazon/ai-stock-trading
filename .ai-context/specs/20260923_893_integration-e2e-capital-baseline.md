---
title: Integration E2E の取引パイプライン試験へ基準資金（前取引日の口座照会）を正規経路で供給する（#893）
type: spec
status: accepted
related_ids: [FR-10, UC-01, ADR-0041, IADR-0050, IADR-0267, IADR-0354]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 リスク管理)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 注記: 前営業日終値時点の equity)
---

# 仕様書: Integration E2E の取引パイプライン試験へ基準資金を正規経路で供給する（#893）

## 起点

- **#893**（CI 自動起票）。`Integration E2E`（`.github/workflows/integration.yml`）が develop で連続して赤。
- 起点 ID: **FR-10**（リスク管理の統制上限）。退行を持ち込んだのは #874（IADR-0354・ADR-0041 決定 2）。

## 診断（実測）

### 失敗は 1 件・1 原因で持続している

| 区分 | run | コミット | 結果 |
| --- | --- | --- | --- |
| 最後の緑 | 35434438011 | `8107ff33` | success |
| 最初の赤 | 35435173901 | `890f6b24`（#874） | failure |
| 以後 | 35439472460 〜 35658640404（10 本。push 8・schedule 3、うち 1 本 cancelled） | `d8264233` 〜 `84026a41` | すべて failure |

`8107ff33..890f6b24` のコミットは **#874 の 1 本だけ**である。
確認した 3 本（35435173901 / 35468040543 / 35658640404）とも、落ちたのは同じ 1 件だけ（29 件中 28 件合格）:

```
Failed AiStockTrading.IntegrationTests.TradeExecutionPipelineE2ETests.取引判断が承認され発注執行まで複数サービスを跨いで流れる
Expected object not to be <null> because TradeDecisionMade→リスク管理承認→ペーパー執行→実 Postgres 永続が複数サービス跨ぎで成立すること.
  at ...TradeExecutionPipelineE2ETests.cs:line 142
```

同じ fixture の fan-out 試験（`OrderApproved` を直接発行し、審査を経ない）は緑である。

### 機序

#874 は統制上限の分母（equity）を**前取引日の口座照会の観測**（`ICapitalBaselineStore`・EF 永続・
`AccountEquityDays`）に由来させ、**無ければ新規建てを `CapitalBaselineUnavailable` で拒否する**
（`RiskEvaluator`・fail-closed）。単体側の `RiskWorkerWebApplicationFactory` には前取引日の行を
仕込む処理が同 PR で足されたが、**結合試験 `TradeExecutionPipelineE2ETests` は独自に
`WebApplicationFactory<RiskManagementWorker::Program>` を組むため追随していなかった**。
新しい実 Postgres には行が無い → 審査が拒否 → `OrderApproved` が出ない → 30 秒待って `null`。

区分: **テストコード（fixture の前提条件不足）**。製品コード・環境・ワークフロー構成の問題ではない
（統制は意図どおり働いている）。

## 対象範囲

- 対象: `backend/Tests/AiStockTrading.IntegrationTests/TradeExecutionPipelineE2ETests.cs` の 1 ファイル。
- 対象外: 製品コード（統制を緩める・構成で基準資金を注入する口を作ることは IADR-0354 決定 4 が禁じる）。

## 母集合（走査と除外理由）

| 走査 | コマンド | 結果 |
| --- | --- | --- |
| リスク管理 Worker を結合試験で起動する箇所 | `grep -rln "RiskManagementWorker::Program" backend/Tests` | 3 件（`TradeExecutionPipelineE2ETests` / `KeycloakOwnerOnlyEndpointE2ETests` / `ServiceTokenSyncQueryE2ETests`） |
| うち新規建ての審査を通すもの | 上の 3 件で `TradeDecisionMade` を発行するもの | `TradeExecutionPipelineE2ETests` の 1 件だけ。他 2 件は同期照会の認可のみで審査を経ず、CI でも緑 |

## 設計

- **統制を迂回しない。** 同試験が情報収集の現況観測を扱うのと同じ作法（IADR-0267）で、
  **本番の供給経路（`BrokerAccountObserved` → `BrokerAccountObservedHandler` → `ICapitalBaselineStore.Record`）へ
  実ブローカ経由で観測を届ける。** DB へ直接行を書かない。
- 観測時刻は **現在の 1 日前**とする。ストアは「当日（米国東部の暦日）より前の取引日で最新の行」だけを
  判定に使い（当日の観測は日中の評価損益を含むため見ない）、鮮度上限は既定 4 日である。1 日前は常に
  前暦日に属し、鮮度内に入る。
- 評価額は従前の既定資金と同額（`TradingDefaults.InitialCapital` ＝ $3,000）とし、試験のコメントが
  前提にしている上限計算（1 注文 25% ＝ $750 ほか）を保つ。
- 同じ観測は口座種別のストアにも記録されるが、1 日前の観測は 30 分の有効期間を過ぎているため
  「観測無し」と同じに扱われ、#874 以前の状態（口座種別観測なしで緑）から変わらない。
- 発行後、`ICapitalBaselineStore.GetCurrent()` が非 null になるまで待つ（発行の完了と適用の完了は別。
  `WaitInformationObservedAsync` と同形）。

## 受け入れ基準

- [ ] `dotnet build backend/backend.slnx` が通る（ローカル実測）
- [ ] `dotnet format backend/backend.slnx --verify-no-changes` が通る（ローカル実測）
- [ ] Integration E2E で当該試験が緑になる（**ローカルは Docker API が無く Testcontainers を実行できない**。
      検証は develop マージ後の push 起動、または `workflow_dispatch` で行う）

## テスト方針

テストケースは増やさない。既存ケースの前提条件（基準資金）を本番と同じ経路で整えるだけである。

## 計画書との差異

なし。

## 実装 ADR を書くか

**書かない。** 設計判断は IADR-0354（基準資金の供給元と fail-closed）と IADR-0267（結合試験は統制を
迂回せず正規経路で前提を整える）の既存決定の適用であり、新しい判断を含まない。

## 未決事項

- ローカルで結合試験を実行できないため、緑の実測は CI に委ねる。
