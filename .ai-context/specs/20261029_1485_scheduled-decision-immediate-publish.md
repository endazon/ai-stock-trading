---
title: 定時判断を判断した時点で送り、巡回の末尾までまとめて遅らせない（DecisionId の冪等は保ち、再試行は送り済みの判断をし直さない）
type: spec
status: done
related_ids: [FR-02, NFR-02, FR-10, ADR-0003, ADR-0013, IADR-0023, IADR-0057, IADR-0129, IADR-0407, IADR-0483, IADR-0490, IADR-0505, IADR-0529, IADR-0593]
author: claude (Claude Code)
created: 2026-10-29
updated: 2026-10-29
---

# 仕様書: 定時判断の即時の発行（#1485）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。AST `origin/develop` `a003a190`）。

## 起点（トレーサビリティ）

- 起点: #1485（稼働 PoC 2026-10-26〜28。判断→審査の承認が 35〜125 秒〔中央値 ≈ 70 秒〕遅れ、承認時刻は巡回の最後の銘柄の判断の時刻と一致した）。
- 計画: FR-02（定時の取引サイクル）、NFR-02（定時サイクルの所要）、FR-10（重複発注をしない統制の前提）、ADR-0003、ADR-0013（Wolverine）。
- 前提の IADR: IADR-0490 決定2（定時判断の DecisionId を起点・市場・銘柄から決定的に導く）、IADR-0505（定時サイクルの試行の上限）、IADR-0023（銘柄ごとの失敗の分離）、
  IADR-0407・IADR-0529・IADR-0057（下流の DecisionId の冪等）、IADR-0483（最終の失敗の報告口はランタイムの MessageBus で送る）。
- 本件の判断: IADR-0593（新規）。

## 原因（コードで確認・develop `a003a190`）

- `InformationCollectedHandler.Handle` は監視銘柄を `foreach` で順に判断し、判断ごとに引数の `IMessageBus`（そのメッセージの文脈）で `PublishAsync` する。
- Wolverine はハンドラの文脈で発行したメッセージを**ハンドラが成功で終わってから**まとめて送る（IADR-0490 の注記・T-10-2246 が実測で固定していた「失敗した試行の発行は捨てる」の裏側）。
- よって 1 銘柄目の判断も巡回の末尾まで下流へ届かない。**再現**: T-10-3076（下流の代役が 2 銘柄目の判断の前に 1 銘柄目の判断を受けない）が是正前に赤。

## 母集合（規則 9・10）

```bash
git grep -n "発行は捨て\|試行の発行\|まとめて送\|捨てられる" -- docs backend
git grep -n "bus.PublishAsync" -- backend/Services/TradeDecisionService
```

| 箇所 | 扱い |
| --- | --- |
| `InformationCollectedHandler`（判断の発行） | 即時の発行口へ替える |
| `ScheduledCycleRetryPolicy.cs` の注記「打ち切られたサイクルの判断の発行は捨てられ」 | 改める |
| `IEntryReservationStore.cs` の注記「判断 N 件はリスク管理へ同時に届く」 | 日付つきで追記（直列化と予約は変わらず要る） |
| `docs/operations/operations.md` の打ち切りの行「打ち切られた巡回の判断は失われ」 | 改める |
| `docs/tests/FR-10_risk-controls-tests.md` の T-10-2245・T-10-2246・T-10-2405 の期待 | 改める（試験も同じ） |
| 価格変動の判断（`PriceMovementDetectedHandler`）の発行 | **対象外**。1 通で 1 銘柄なので末尾までの遅れが無い。失敗した配送の発行を捨てる性質を変えない |
| 判断サービスの中の監査系の発行（見送り・保有の照会の健全性など。scoped の IMessageBus） | **対象外**。審査・発注に届かず、遅れても価格が古くならない |
| `.ai-context/` の過去の記録 | point-in-time の記録（書き換えない） |

## 設計

1. **即時の発行口** `ScheduledDecisionPublisher`（singleton・必須依存）: ランタイムから作る `MessageBus` で送る（ハンドラの文脈に乗らない。取引判断の失敗の報告口と同じ作法）。
2. **冪等**: DecisionId は従来どおり決定的。打ち切られた試行の判断はもう捨てられないので、発行口は**送れた決定的な DecisionId を覚え**（上限 1,024 件・古いものから忘れる）、
   ハンドラは判断の前にそれを読み、送り済みなら**判断し直さない**（LLM を二重に呼ばない・同じ判断を二重に送らない）。決定的に導けない ID・送信の失敗は覚えない。
3. **別のプロセスへの再配送**（ack 前に Pod が落ちた）は覚えが無いので判断し直して同じ DecisionId で送り、下流の冪等（IADR-0407 / 0529 / 0057）が止める（従来どおり）。
4. 審査・発注の順序の前提: 判断は 1 件ずつ審査へ届く（従来は巡回の末尾で同時）。審査は新規建てを直列化し予約で数えるので（IADR-0529）、続く判断の審査は先の承認を算入する。前提の変更は無い。

## 受け入れ基準（→ 試験）

- [x] 巡回の 1 銘柄目の判断が、巡回の末尾を待たずに審査（下流）へ届く（T-10-3076。是正前に赤）。
- [x] 打ち切り・再試行でも判断は銘柄ごとに 1 件・決定的な DecisionId。再試行は送り済みの銘柄の LLM を呼ばない（T-10-2246・T-10-2405 の改め）。
- [x] 別のプロセスへの再配送は同じ DecisionId で出る。同じプロセスへの再配送は送り直さない（T-10-2245 の改め）。
- [x] 発行口は送れた決定的な ID だけを覚え、上限で古いものから忘れる（T-10-3077）。

## 検証

- `dotnet build backend/backend.slnx`・TradeDecisionService.Tests・Architecture.Tests・`dotnet format --verify-no-changes`・文書検査・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。

## 配備

- 取引判断だけを上げる。設定の追加は無い。
