---
title: 壁時計どうしの競争で合否が決まる試験（形 (a)）を止める検査器を足す（#921）
type: spec
status: accepted
related_ids: [NFR, IADR-0168, IADR-0366, IADR-0367, IADR-0379]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs: []
---

# 仕様書: 壁時計どうしの競争で合否が決まる試験を止める検査器（#921）

## 起点

- **#921**（IADR-0379 決定 4 の追随）。判断・検出規則・allowlist の形は IADR-0379 決定 4 と #885 の走査コメントが既に決めている。
  本作業は**それを機械化して CI へ配線する**だけで、新しい判断は持ち込まない（細目の解釈は IADR-0379 へ日付つき追記で残す）。
- 着手条件「#907 と #920 の両方が develop にマージ済み」は満たしている（`e4f699d`＝#907、`5f5351a`＝#920。`git log origin/develop` で確認）。
- メタ作業（検査器の追加）であり、計画の非機能要件表に当たる番号は無い → 無採番 `NFR`（`traceability.md` の 2 の場合）。

## 作るもの

| 項目 | 内容 |
| --- | --- |
| 検査器 | `scripts/check-wall-clock-timeout-tests.js`（外部依存ゼロ・Node 標準のみ・検出で exit 1） |
| 模擬ツリー | 環境変数 `WALL_CLOCK_RACE_CHECK_ROOT` で走査の根を差し替える |
| allowlist | `ALLOWED`（`Map`: 相対パス → 理由＋起票 ID）。**空で入れる** |
| CI | `ci.yml` の `static-checks` ジョブへ step を 1 つ足す（`check-tracked-session-timeout` の直後） |
| 一覧 | `scripts/README.md` の `static-checks` の表に 1 行 |
| 自己試験 | `scripts/scripts.repo.test.js`（姉妹検査器 `check-tracked-session-timeout` と同じ場所・同じ形） |

## 検出規則（IADR-0379 決定 4 のとおり）

同一テストファイル内で、**有限の実時間の打ち切り**が**実時間の遅延**より小さければ落とす。

| 側 | 入口（コメント・文字列を潰した後のコードに対して照合） | 期間として読むもの |
| --- | --- | --- |
| 打ち切り | `Timeout =`（語境界。`ReplyTimeout =` は当たらない）/ `CancelAfter(` / `new CancellationTokenSource(` / `Deadline = ….Add…(`（同じ文の中の最初の `.Add…(`） | `TimeSpan.FromXxx(<数値>)`。`CancelAfter` と CTS は裸の数値もミリ秒として読む |
| 遅延 | `Task.Delay(` / `Thread.Sleep(` / `DelayingHandler(` | `TimeSpan.FromXxx(<数値>)`。`Task.Delay` と `Thread.Sleep` は裸の数値もミリ秒として読む |

- **定数だけを読む。** 変数・式（`Task.Delay(delay, ct)` / `TimeSpan.FromSeconds(1) * 2`）は読まない —— 再現率より的中率を採る（#921 本文）。
- `Timeout.InfiniteTimeSpan` / `Timeout.Infinite` / 負のミリ秒は**有限でない**ので読まない（是正後の「応答しない上流」は素通りする）。
- 「どれか 1 組でも 打ち切り ＜ 遅延」は「ファイル内の最小の打ち切り ＜ 最大の遅延」と同値なので、後者で判定し、報告にはその 2 行を出す。
- コメント・文字列リテラルは `check-tracked-session-timeout.js` の `stripComments` を **require して共用する**（規則を 2 か所に持たない。補間の穴・入れ子の文字列・逐語文字列の作法は同スクリプトの自己試験が既に固定している）。
- 母集合: **ディレクトリ名が `Tests` で終わる**ディレクトリ配下の `*.cs`（`bin` / `obj` / `.claude` 等は除く）。

## #922 への拡張点

検出は `SHAPES`（`{ id, title, remedy, detect(strippedText) }` の表）で持つ。形 (c)（`ExecuteAndWaitAsync` の既定 5 秒）の検出器は
この表へ 1 行足せば、母集合・コメント潰し・allowlist・報告の書式を共用できる。#922 が `check-tracked-session-timeout.js` の拡張として
実装するか、本検査器の `SHAPES` へ足すかは #922 の判断に残す（本 PR は #922 の射程に触れない）。

## 母集合（規則 9〜11）

### 規則 9: 走査してから挙げる

| 走査 | 手段 | 結果 |
| --- | --- | --- |
| 検査対象（develop `69d098c`） | 本検査器の母集合（ディレクトリ名が `Tests` で終わる配下の `*.cs`） | **661 ファイル**（#885 時点の 633 から増えた。うち `backend/Tests/AiStockTrading.IntegrationTests/` 13 ファイルを含む） |
| develop での検出 | `node scripts/check-wall-clock-timeout-tests.js` | **0 件**（exit 0） |
| 入口ごとの生の一致（テスト樹形） | `git grep -nE "CancelAfter\(\|new CancellationTokenSource\([^)]\|Deadline *=\|Thread\.Sleep\(\|Task\.Delay\([0-9T]"` | 30 行。有限の定数遅延は `CollectionPollingServiceTests`（300 ms・否定形）・`ReportDependencyHandlerTests`（50 ms・否定形）・IntegrationTests のポーリング（250 / 500 ms）だけで、同じファイルに有限の打ち切り定数が無い。残りは `Timeout.Infinite(TimeSpan)`・変数（`ConnectTimeout` / `timeout`） |
| 再現率（過去の木） | `git archive e4f699d^`（#907 前）/ `5f5351a^`（#920 前）を模擬ツリーにして実走 | **12 件 / 11 件**。IADR-0379 の一覧（12 ファイル、#907 が 1 件を先に直す）と**ファイル名まで一致**。偽陽性 0 |

### 除外とその理由

- `backend/Tests/AiStockTrading.IntegrationTests/` は**除外しない**（`Tests` で終わるディレクトリの配下であり、#921 本文の母集合の定義に入る）。
  既定 CI では走らないが、Docker 経路でも同じ競争は偽の赤を出す。現に 0 件（`readyDeadline = …AddSeconds(30)` は語境界で当たらず、30 秒は遅延より大きい）。
- 形 (b)（該当なし）・形 (c)（#922）は検出しない（#921 本文）。

### 規則 10: この変更で新たに誤りになる自分の記述

- IADR-0379 の残余リスク「検査器が入るまで新しい同型は止まらない」と索引行の同文 → **日付つき追記で解消を記録する**（本文は書き換えない）。
- 作業仕様書 `20260923_885_wall-clock-timeout-test-inventory` の「未決事項: 検査器の投入は #921」→ **確定済みの作業仕様書であり書き換えない**（point-in-time の記録）。
- `docs/ai-workflow.md` の必須 check 名の表 → **変わらない**（ジョブを足さず `static-checks` の step を足すだけ。`check-workflow-job-refs.js` で確認）。
- #921 本文の数え（633 ファイル）は検証せず転記しない → 本作業の実測（661）を書いた。

### 規則 11: 窓を扱う是正か

**当たらない。** 本作業は試験コードの窓を動かさない（検査器を足すだけ）。検出規則の「増える側（打ち切り）」「減る側（遅延）」の
両方の入口を赤ケースと緑ケースで自己試験に置く（下記）。

## 受け入れ基準

- [ ] `scripts/check-wall-clock-timeout-tests.js` を新設し、外部依存ゼロ
- [ ] `ALLOWED` が空のまま、リポジトリ全体で緑（develop で 0 件）
- [ ] 模擬ツリー（`WALL_CLOCK_RACE_CHECK_ROOT`）で**検出できること**と**是正後の形を素通りさせること**の両方を自己試験する
- [ ] CI（`static-checks`）へ配線する。必須 check 名の表は変わらない
- [ ] `scripts/README.md` に 1 行足す
- [ ] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑

## テスト方針（`scripts/scripts.repo.test.js`）

- 赤: `Timeout = 50 ms` ＋ `DelayingHandler(2 s)`（#920 前の実形）／`CancelAfter(100)` ＋ `Task.Delay(600)`／
  `new CancellationTokenSource(TimeSpan.FromMilliseconds(100))` ＋ `Thread.Sleep(1000)`／`Deadline = …AddMilliseconds(50)` ＋ `Task.Delay(TimeSpan.FromSeconds(1))`。
- 緑: 是正後の形（`Task.Delay(Timeout.InfiniteTimeSpan, ct)`）／打ち切りが遅延より大きい／変数の遅延／コメント・文字列中の言及／
  `ReplyTimeout =` のような別名／打ち切りだけ・遅延だけ。
- 母集合: `backend/TestSupport/X.Tests/` を拾い、`Tests` で終わらないディレクトリは拾わない。
- 模擬ツリー: 環境変数で根を差し替えて子プロセスで実走し、赤は exit 1、是正後は exit 0。
- allowlist が空であること、実ツリーが 0 件であること。

## 計画書との差異

なし（試験の検査器であり、製品コードは変えない）。

## 未決事項

- 形 (c) は #922（本 PR は触らない）。
