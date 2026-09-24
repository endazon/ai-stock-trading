---
title: RiskWorkerWebApplicationFactory の前取引日シードを 2 日前にし、夏時間終了日に当日へ落ちる窓を閉じる（#905）
type: spec
status: accepted
related_ids: [FR-10, ADR-0041, IADR-0354]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 リスク管理)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 注記: 前営業日終値時点の equity)
---

# 仕様書: RiskWorkerWebApplicationFactory の前取引日シードを 2 日前にする（#905）

## 起点

- **#905**。`backend/Services/RiskManagementService/Tests/RiskWorkerWebApplicationFactory.cs:75` が
  基準資金の行を `DateTimeOffset.UtcNow.AddDays(-1)` でシードしている。
- 起点 ID: **FR-10**（リスク管理の統制上限）。持ち込んだのは #869（IADR-0354・ADR-0041 決定 2）。
- 先例: **PR #903**（#893）が Integration E2E 側の同型の欠陥を `AddDays(-2)` で是正済み。

## 診断

`EfCapitalBaselineStore.GetCurrent()` は **`TradingDay < today`**（`today` は**米国東部時間の暦日**）の行だけを
判定に使う。当日の行は日中の評価損益を含むため見ない（計画 05_trading-assumptions §5 注記）。

`UtcNow.AddDays(-1)` は暦日ではなく **24 時間前**である。米国東部で夏時間が終わる日は
**25 時間**あるため、その日の最後の 1 時間（ET 23:00〜23:59。UTC では 04:00〜04:59）に限り、
24 時間前は**同じ ET 暦日**に落ちる。すると仕込んだ行は `TradingDay == today` となり判定から外れ、
`GetCurrent()` が `null` を返す。基準資金が未供給になり、**新規建てを前提にするテストは
`CapitalBaselineUnavailable` で落ちる**（fail-closed。`RiskEvaluator`）。

該当窓は年 60 分（**2026-11-02T04:00Z〜04:59Z / 2027-11-08T04:00Z〜04:59Z**。#905 本文・PR #903 の監査が実測）。
本作業でも同じ計算を 2026〜2027 年の全分（1,051,200 分）へ当て直して再現した（下「実測」）。

区分: **テストコード（fixture の前提条件）**。製品コードは意図どおり働いている。

## 母集合（走査と除外理由）

**記憶で挙げず、誤りの側の文字列で走査した**（`traceability.repo.md` 規則 9）。

| 走査 | コマンド | 結果 |
| --- | --- | --- |
| 壁時計からの日数減算（`.cs` 全件） | `grep -rnE "(DateTimeOffset\|DateTime)\.UtcNow\.Add(Days\|Hours)\(-" --include=*.cs backend` | **4 件** |
| 固定時刻定数からの減算 | `grep -rn "AddDays(-" --include=*.cs backend`（約 130 件）から、`Now` / `T0` / `At` / `Day` が**凍結定数**であるものを除外 | 残り 0 件（例: `CapitalBaselineTests` の `Now` は `2026-07-09T18:00Z` の定数） |

走査で残った 4 件の判定:

| 箇所 | 用途 | 同型か |
| --- | --- | --- |
| 🔴 `backend/Services/RiskManagementService/Tests/RiskWorkerWebApplicationFactory.cs:75` | 基準資金の**前取引日**シード（`TradingDay < today(ET)` と突き合わせる） | **同型。本 PR で是正する** |
| `backend/Tests/AiStockTrading.IntegrationTests/TradeExecutionPipelineE2ETests.cs:147` | 同上（結合試験） | 同型だが **PR #903 で是正済み**（`AddDays(-2)`） |
| `.../AdoptPositionDrift/PositionDriftAdoptionEndpointTests.cs:41` | 台帳へ建玉（承認＋約定）を積むだけの時刻 | **非該当**（下記） |
| `.../ClosePosition/PositionCloseEndpointTests.cs:37` | 同上 | **非該当**（下記） |

**非該当の理由**: この 2 件の `at` は「前取引日であること」を前提にしていない。`PortfolioProjection` が
取引日境界を見るのは**当日発注枠・同日再エントリー・当日実現損益**であり、これらは
**新規建て（`PositionEffect.Open`）の審査にしか効かない**。両ファイルの表明は手仕舞い（`Close`）・
乖離取り込み・期間照会（`today±7 日`）であり、約定が前日から当日へ 1 時間だけ移っても結果は変わらない。
`realizedToday` も新規建ての実現損益 0 で不変である。**よって是正しない**（射程を広げない）。

## 設計

- `AddDays(-1)` → **`AddDays(-2)`**。PR #903 と同じ是正であり、新しい判断を含まない。
  - **48 時間前はどの瞬間でも ET の前暦日に属する**（暦日は最長 25 時間）。
  - 鮮度上限は既定 **4 日**（`CapitalBaselineOptions.DefaultMaxAgeDays`）であり、2 日前は内側。
- 日数をリテラルで散らさず、**`CapitalBaselineSeedDaysAgo` 定数**として factory に置き、テストが同じ定数を検査する
  （数値と根拠を 1 箇所に置く）。
- 評価額・冪等性・`TradingDay.Of` の使い方は変えない。

## 受け入れ基準

- [ ] `RiskWorkerWebApplicationFactory` のシードが、**どの瞬間でも** `TradingDay.Of(seed, US) < TradingDay.Of(now, US)` を満たす
- [ ] シードの経過時間が鮮度上限（既定 4 日）の内側に収まる
- [ ] 24 時間前では上の不変条件が破れる瞬間が実在することを、退行防止として固定する
- [ ] `dotnet build backend/Services/RiskManagementService/RiskManagementService.slnx` 相当が通る
- [ ] `dotnet test backend/Services/RiskManagementService/Tests/RiskManagementService.Tests.csproj` が緑
- [ ] `dotnet format --verify-no-changes` が通る

## テスト方針

`backend/Services/RiskManagementService/Tests/CapitalBaselineSeedTests.cs` を新設する（3 点セットのうち
**境界値**と**プロパティベース**を採る。否定形は既存の `CapitalBaselineTests` が持つ）。

- **T-10-682（プロパティベース）**: 2026〜2027 年を 1 分刻みで走査し、`AddDays(-2)` が常に前 ET 暦日かつ
  鮮度上限の内側であることを表明する。
- **T-10-683（境界値・退行の記録）**: 同じ走査で `AddDays(-1)` が破れる瞬間を数え、
  **年 60 分・夏時間終了日の ET 23 時台**であることを表明する（是正の根拠を失わないため）。

## 計画書との差異

なし。

## 実装 ADR を書くか

**書かない**（予約番号 IADR-0378 は使わない）。IADR-0354 決定 3/4 が定めた取引日境界と鮮度の適用であり、
是正の形は PR #903 の先例と同一である。新しい判断を含まない。

## 実測

`dotnet test` の出力は PR 本文に貼る。
