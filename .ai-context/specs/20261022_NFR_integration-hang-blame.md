---
title: Integration E2E の試験のハングを 5 分で止め、試験名とダンプを残す（ジョブごと cancelled にしない）
type: spec
status: done
related_ids: [NFR, IADR-0208, IADR-0497]
author: claude (Claude Code)
created: 2026-10-22
updated: 2026-10-22
---

# 仕様書: Integration E2E の試験のハングを 5 分で止め、試験名とダンプを残す

> 本仕様書は実装着手前に作成した（着手 2026-10-22。AST `origin/develop` `9e034045`＝#1435 のマージ後）。

## 起点（トレーサビリティ）

- 起点: MSP#2060（MSP の Integration が赤）の調査で見つかった、AST の統合 E2E の**稀なハング**。試験の決定性＝ `NFR`。
- IADR: 作らない（CI の診断の追加だけ。製品コード・試験の判定・本番の構成は変えない）。計画との矛盾: なし。

## 事実（実測）

`backend/Tests/AiStockTrading.IntegrationTests` の実行が途中で無出力のまま止まり、ジョブが `timeout-minutes: 30` で cancelled になる。
どの試験で止まったかも、スタックも残らない。

| run | リポ | 止まる直前に「Passed」と出た試験 |
| --- | --- | --- |
| 38037388664 | AST | `KeycloakOwnerOnlyEndpointE2ETests` |
| 38068332987 | AST | （別プロジェクトの完了行の後） |
| 38916399777 | AST | `TradeExecutionPipelineE2ETests.取引判断が承認され…` |
| 39032776492 | AST | `TradeExecutionPipelineE2ETests.取引判断が承認され…` |
| 39630282507 | AST | `TradeExecutionPipelineE2ETests.取引判断が承認され…` |
| 39430975331 | MSP | `OrderExecutionPipelineE2ETests` |
| 39783718105 | MSP | `TradeExecutionPipelineE2ETests.同一イベントは…fan_out…` |

- 7 件中 6 件は、in-process のホスト（`WebApplicationFactory`）と実コンテナを使う E2E の直後で止まっている。
  xUnit は試験クラスの破棄を終えてから結果を報告するため、止まっているのは**次の試験**の
  コンストラクタ／`InitializeAsync`（コンテナ起動）／ホスト起動のどれかである可能性が高い（未確定）。
- 手元（4 コア・Docker）では再現しなかった: AST の slnx 全体を CI と同じ引数で 3 回、
  `TradeExecutionPipelineE2ETests`＋`OrderExecutionPipelineE2ETests` を seed を変えて 14 回、すべて合格。

## 方式

原因の特定に要る材料（どの試験か・スタック）を、次にハングしたときに必ず残す。

1. `dotnet test` に `--blame-hang-timeout 5m --blame-hang-dump-type mini` を足す。
   5 分は「1 試験の無進捗」の上限で、統合 E2E の最長（コンテナ起動込み 40 秒前後）に対し十分長い。
   止まれば testhost を落としてテストの step が赤になり、`The test running when the crash occurred:` に試験名が出る。
   後続の skip 検査は従来どおり走る（`!cancelled()`）。
2. テストの step が失敗したとき、`integration-results/**/*.dmp` と `Sequence_*.xml` を成果物 `integration-hang-dumps` に 7 日残す。
3. `scripts/scripts.repo.test.js` の integration.yml の配線の試験に、上の 2 つを固定する。

## 受け入れ基準

- [x] ハングした試験で testhost が止まり、試験名とダンプが残る（手元で `--blame-hang-timeout 15s` にして実測:
      `The test running when the crash occurred:` に試験名、TestResults に `*_hangdump.dmp`（mini で約 57MB）と `Sequence_*.xml`）。
- [x] 配線を外すと `scripts.repo.test.js` が赤になる（変異で確認）。
- [x] 平常時の判定は変わらない（ハングしない限りフラグは何もしない）。

## 残余

- 根本原因（何が止まるか）は未確定。次のハングで成果物のダンプ（`dotnet-dump analyze` の `clrstack -all`）から特定する。
