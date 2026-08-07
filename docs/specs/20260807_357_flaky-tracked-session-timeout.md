---
title: 並列実行時のみ再現する flaky test を、Wolverine TrackedSession の壁時計依存として解消する
type: spec
status: review
related_ids: [NFR, IADR-0129, IADR-0168]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
---

# 仕様書: flaky test（TrackedSession の壁時計タイムアウト）の解消

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **なし**（NFR。テストの信頼性）
- 実装 ADR: **[IADR-0168](../adr/IADR-0168_tracked-session-timeout-budget.md)（本作業で新設）** ／
  [IADR-0129](../adr/IADR-0129_wolverine-messaging-topology.md)（Wolverine への移行。`TrackActivity` はここで導入された）
- 起点 issue: [#357](https://github.com/endazon/ai-stock-trading/issues/357)。関連: #343（退行防止テスト基盤）・#344（全面再実装）

## 目的・背景

`dotnet test backend/backend.slnx` を**ソリューション全体で並列実行**したとき、テストが 1 件だけ失敗することがある。単体で再実行すると緑になる。#357 は AI レビューセッションで**独立に 2 回**観測されたが、**失敗したテスト名が記録されていなかった**ため、まず特定が要るとされていた。

**flaky test は CI ゲートを構造的に無効化する。** CI が確率的に赤くなると、人も AI も「また flake だろう」と再実行する習慣を身につけ、**本物の退行も同じ反応で流される**。資金を扱うシステムの統制系テストがこの扱いを受けるのは受け入れられない。

## 特定（本作業で実測した）

**ソリューション全体を 15 回反復実行し、15 回目で再現した。**

```
Failed AiStockTrading.CostControl.Infrastructure.Tests.LlmCostIncurredConsumerTests.別_MessageId_はそれぞれ計上される [6 s]
System.TimeoutException : This TrackedSession timed out before all activity completed.
Activity detected:
| bcd99ef6-… | LlmCostIncurred |  81 ms | Sent     |
| bcd99ef6-… | LlmCostIncurred | 154 ms | Received |
   at Wolverine.Tracking.TrackedSession.AssertNotTimedOut()
```

**#357 が報告した `RiskManagementService.Worker.Tests` / `ReportService.Worker.Tests` とは別のプロジェクトである。** これは重要な手がかりで、**原因がプロジェクト固有ではない**ことを意味する。

### 原因

`Wolverine.Tracking.TrackedSession` は**壁時計で打ち切る**。既定は **5 秒**であり、リポジトリ内に `.Timeout(...)` の指定は **1 件も無い**（`grep` で確認）＝**全 131 か所が既定の 5 秒**である。

上の失敗では `Sent`（81 ms）と `Received`（154 ms）は記録されているが、**`Executed` が窓内に現れなかった**。メッセージが失われたのではなく、**ハンドラの完了が 5 秒以内にスケジュールされなかった**。ソリューション全体の並列実行では **9 プロジェクト × 複数ホスト**が同時に動き、CPU が飽和する。

**つまりロジックの不具合ではなく、テストハーネスの壁時計への暗黙の結合である。** #357 が疑った「共有状態 / 実時刻依存 / ポート衝突」のうち **実時刻依存**が当たりである。

> **この 5 秒は「性能の表明」ではない。** どのテストも「5 秒以内に完了すること」を要求していない。5 秒は **Wolverine の既定値がたまたま入っていただけ**であり、意味づけは「**ハングの検知**」である。

## 対象範囲

### 対象

| 追加物 | 役割 |
| --- | --- |
| `backend/TestSupport/AiStockTrading.TestSupport.Messaging` | 新規のテスト専用プロジェクト |
| └ `WolverineTrackingExtensions.TrackActivityForTest(this IHost)` | **予算を適用した** `TrackedSessionConfiguration` を返す |
| └ `TrackedSessionBudget` | 予算の単一情報源（既定 **30 秒**・環境変数で上書き可） |
| `scripts/check-tracked-session-timeout.js` ＋ CI ジョブ | **素の `TrackActivity()` をテストコードで禁止**する |
| 131 か所の呼び出し | `host.TrackActivity()` → `host.TrackActivityForTest()` |

### 対象外（意図的にやらない）

- **`Thread.Sleep` / リトライでの糊塗**（#357 の受け入れ基準が明示的に禁じている）
- **並列度を下げること**（`dotnet test -m:1` 等）。#357 は「**並列度に依存しない形へ**」と要求しており、並列度を下げるのは**依存したまま条件を避ける**ことである。CI の実行時間も伸びる
- **`TrackedSession` を使わない形への書き換え**（表明の意味が変わる。IADR-0129 の移行方針を覆さない）
- **テストの表明・対象の変更**

## 設計

### なぜ「予算を伸ばす」のが糊塗ではないのか

**タイムアウトの意味づけが違うためである。**

| | 意味 | 適切な値 |
| --- | --- | --- |
| **性能の表明** | 「N 秒以内に終わること」がテストの主張 | 厳しく |
| **ハングの検知**（本件） | 「終わらないなら永久に待たず落とす」 | **十分に緩く** |

本件はすべて後者である。**後者に厳しい値を入れると、検知したいもの（ハング）ではなく、検知したくないもの（スケジューリング遅延）を拾う。**

### なぜ各所に `.Timeout(...)` を書き足すのではなく、専用の拡張にするのか

131 か所への機械的な追記は**新しく書かれるテストに効かない**。`TrackActivity()` は Wolverine の標準 API であり、次に書く人は素直にそれを呼ぶ。**同じ flake が静かに戻る。**

したがって **(1) 予算つきの入口を 1 つ用意し、(2) 素の入口を機械的に禁止する**。`check-banned-libraries.js` / `check-banned-settled-cash-sources.js` と同じ形である。

### 予算の値

**既定 30 秒**（既定 5 秒の 6 倍）。実測した失敗は 6 秒であり、飽和時の遅延に十分な余裕を取る。環境変数 `AST_TEST_TRACKING_TIMEOUT_SECONDS` で上書きできる（**より遅い CI 実行環境で値を変えるために、コード改変を要しない**）。

**代償**: 本当にハングしたテストは 5 秒ではなく 30 秒かけて落ちる。**ハングは稀であり、flake は常時である**——取るべきトレードオフはこの向きである。

## 受け入れ基準（#357 の 4 項目）

- [ ] **失敗するテストが特定されている**（反復実行で再現させる）
- [ ] **原因が特定されている**
- [ ] **時刻・並列度に依存しない形へ修正されている**（`Thread.Sleep` での回避やリトライでの糊塗ではない）
- [ ] **反復実行で 1 度も失敗しない**

## テスト方針

| 担保 | 内容 |
| --- | --- |
| `scripts/check-tracked-session-timeout.js` | **素の `TrackActivity()` の再混入を CI で止める**（本作業の再発防止の本体） |
| `scripts/scripts.test.js` への追加 | 上の検査自体が**効いていること**（陽性・陰性の両方向） |
| `TrackedSessionBudgetTests` | 既定値・環境変数の上書き・**不正値では既定へ倒す**ことを固定 |
| **予算を動かす決定的な実験** | 予算そのものを操作し、**同じ失敗を意のままに出し入れする**（下記） |

**「15 回走らせて出なかった」では担保にならない**（元の再現率が 1/15 である）。

**当初は「CPU を飽和させて発生率を上げ、修正前後を比較する」計画だったが、これは採らなかった。** 実際に負荷（コア数と同数のビジーループ）をかけて 3 回走らせても**一度も再現しなかった**ためである——**発生率を上げられない負荷条件での「出なかった」は、何も示さない。**

**代わりに、予算そのものを操作変数として動かした。**

| 予算 | 結果（`CostControlService.Infrastructure.Tests`・31 件） |
| --- | --- |
| **0.05 秒**（極小） | **Failed: 4** —— すべて `System.TimeoutException : This TrackedSession timed out`（flake と同一の型・同一の文言） |
| **30 秒**（既定） | **Passed: 31** |

**同じテスト・同じコード・同じ機械で、予算だけが結果を決めている。** 確率に頼らず「壁時計の予算が操作変数である」ことを示せる。これは**環境変数の上書きが実際に効いていることの実証でもある**（2 つを 1 つの実験で兼ねている）。

あわせて**検査自身をミューテーションで確かめた** —— 1 か所を素の `TrackActivity()` へ戻すと `check-tracked-session-timeout.js` が exit 1 で当該行を指摘し、戻すと緑に復した。

## 残余リスク

1. **予算を伸ばしただけであり、飽和が極端になれば再び超え得る。** 30 秒を超えるスケジューリング遅延は、テスト環境そのものの問題として別に扱う。
2. **ハングしたテストの失敗が 6 倍遅くなる。**
3. **検査は `TrackActivity()` という綴りに依存する。** 別名の入口（将来 Wolverine が追加する API）は検出できない。
4. **本作業は `TrackedSession` の壁時計依存だけを塞ぐ。** #357 が挙げた他の仮説（共有状態・固定ポート）が別に存在する可能性は否定できない——**ただし観測された失敗はすべて `TimeoutException` であった**。
