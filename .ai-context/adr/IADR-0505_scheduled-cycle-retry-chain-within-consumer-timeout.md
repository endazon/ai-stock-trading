---
title: IADR-0505 定時サイクルの試行の上限を、再試行の連鎖全体（起点の待ち ＋ 試行 × ハンドラの上限 ＋ 再試行の待ち）がブローカの consumer_timeout に収まる最大の回数として起動時に導き、定時サイクルのチェーンにだけ適用する。1 回でも収まらない構成では起動を止める
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-02, NFR-02, NFR-13, ADR-0013, IADR-0490, IADR-0129, IADR-0023, IADR-0483, IADR-0163]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-02, FR-04, NFR-02, NFR-13)
---

# IADR-0505: 定時サイクルの再試行の連鎖全体を consumer_timeout に収める（#1194）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-08
- 決定者: claude（#1194 の依頼「起動時に検査する／収まらない構成では起動を止めるか、定時サイクルのハンドラだけ再試行しない」に沿って起案）

## 起点・関連

- 起票: [#1194](https://github.com/endazon/ai-stock-trading/issues/1194)（PR #1193 の独立監査 🟡1）。Refs #1169
- 関連する計画書 ID: FR-04・FR-02（定時の取引サイクル）・NFR-02（定時サイクル 1 回の所要）・NFR-13（LLM 費用の統制）。計画 ADR-0013（Wolverine）
- 関連 IADR: [IADR-0490](IADR-0490_scheduled-cycle-timeout-and-deterministic-decision-id.md)（上限 T と前提の上限の式。本件はその 2026-10-07 追記の既知の穴を閉じる）・
  [IADR-0129](IADR-0129_wolverine-messaging-topology.md) 決定 5（共通の失敗方針）・IADR-0023（1 銘柄の失敗でサイクルを再配送させない）・IADR-0483（最終の失敗の報告）・
  IADR-0163（必須依存）。参考: MSP の IADR-0478 決定 7（`ConsumerHandlerTimeouts.EnsureRetryChainFits`）
- 作業仕様書: [`.ai-context/specs/20261008_1194_scheduled-cycle-retry-chain-budget.md`](../specs/20261008_1194_scheduled-cycle-retry-chain-budget.md)（母集合・窓の形の表）
- 基点コミット: `origin/develop` `c20e6b9c`

## コンテキストと課題

- 共通の失敗方針（`OnAnyException().RetryWithCooldown(2s, 10s, 30s).Then.MoveToErrorQueue()`）は**同じ配信の中で**再試行する。
  RabbitMQ の受信は Inline で、ack は最後の試行の後である。したがって 1 通が配信を握る時間は「試行 × ハンドラの上限 ＋ 待ちの和」まで伸びる。
- 定時サイクルのハンドラの上限 T は長い（既定 960 秒・経路B 1,140 秒）。IADR-0490 の追記は 1 回の試行だけを `consumer_timeout`（既定 1,800 秒）と比べた。
  打ち切り（前提超過のとき）が続くと、経路B で最大 600 ＋ 4 × 1,140 ＋ 42 ＝ 5,202 秒握り、ブローカがチャネルを閉じて先読みした配送ごと再配送する
  （LLM の費用が二重。発注は IADR-0490 決定 2 の DecisionId で重複しない）。

## 決定

### 決定 1 — 握り(n) を導く

- **握り(n) ＝ 起点の待ち W ＋ n × T ＋ 共通の再試行の待ちの先頭 n − 1 個の和**。T は Wolverine に設定する整数秒（`HandlerTimeoutSeconds`）。
- W は**鮮度の上限の既定 600 秒**（`ScheduledCycleBudget.DefaultStaleness`）。判断される起点は鮮度の上限以内で始まるので、先読みされたまま待つ時間はこれ以下である。
  配備の巡回は本番既定・経路B とも 300 秒（鮮度の宣言 600 秒）で一致する。**巡回を延ばすときは W の前提を引き直す**（受け手は起動時に巡回間隔を知らない）。
- 共通の間隔は shim の `WolverineExtensions.RetryCooldowns`（読み取り専用。新設）から読む（間隔を 2 箇所に持たない）。

### 決定 2 — 定時サイクルの試行の上限は、収まる最大の回数

- 試行の上限 n は、共通の上限（初回＋再試行 3 回＝4）以下で **握り(n) ＜ `consumer_timeout`** となる最大の n（`ScheduledCycleRetryChain.Derive`）。
- `consumer_timeout` は `Messaging:BrokerConsumerTimeoutSeconds`（MSP と同じキー。既定 1,800 秒＝RabbitMQ の既定。未設定・不正・非正値は既定）。
- 既定（握り(1) ＝ 1,560）・経路B（1,740）はいずれも **n ＝ 1**: **定時サイクルは再試行しない**（失敗した配信は `_error` へ）。T が短い構成（例 340 秒）は n ＝ 3。

### 決定 3 — 1 回でも収まらなければ起動を止める

- n ＝ 1 でも収まらない（握り(1) ≧ `consumer_timeout`。等しいときも）なら `InvalidOperationException` で起動を止める。文言は構成キー（`consumer_timeout` と監視銘柄数の前提）を名指す。
- 連鎖は DI の単一の値（必須依存）で、`ScheduledCycleRetryPolicy` が Wolverine の起動中（`HandlerGraph.Compile`）に解決する。起動時に試行の上限を 1 行出す。
- 経路B で前提を 13 へ上げると 600 ＋ 1,230 ＝ 1,830 で起動しない（IADR-0490 追記の「前提 12 が最大」がコメントだけでなくコードで強制される）。

### 決定 4 — 規則は定時サイクルのチェーンにだけ載せる

- `ScheduledCycleRetryPolicy`（`IHandlerPolicy`）が `InformationCollected` のチェーンに失敗の規則を載せる: n ＝ 1 は `OnAnyException().MoveToErrorQueue()`、
  n ≧ 2 は共通の間隔の先頭 n − 1 個で `RetryWithCooldown(...).Then.MoveToErrorQueue()`。他のハンドラ（価格変動の判断ほか）は共通の規則のまま。
- Wolverine はチェーンの規則を共通の規則より先に当てる（実測: T-10-2405）。
- 打ち切られた試行の判断の発行は捨てられる（Wolverine の失敗した試行の発行の破棄。IADR-0490）。そのサイクルの判断は無くなり、次の巡回（300 秒後）が判断し直す。
  銘柄ごとの失敗は従来どおりハンドラの中で分離される（IADR-0023 / IADR-0483。本決定は銘柄の catch の外へ漏れた失敗だけに効く）。

### 決定 5 — 監視銘柄数の前提の上限の式

- 従来「鮮度の上限 ＋ T ＜ `consumer_timeout`」→ **「W ＋ n × T ＋ Σ待ち(n − 1) ＜ `consumer_timeout`」**。n ＝ 1 なら従来と同値で、経路B の最大は前提 12 のまま。
  前提を上げると n が減り（再試行が無くなり）、n ＝ 1 でも収まらなければ起動しない。

## 検討した選択肢

| 選択肢 | 連鎖が収まる | 小さい T で再試行を保つ | 変更の範囲 | 採否 |
| --- | --- | --- | --- | --- |
| (a) 収まる最大の回数を導き、定時サイクルだけに適用（1 回でも収まらなければ起動を止める） | ✓ | ✓ | trade-decision ＋ shim の公開 1 つ | **採用** |
| (b) 定時サイクルは常に再試行しない（固定） | ✓ | ✗ | 小 | 不採用（短い T の構成で一過性の失敗の再試行を失う。(a) は既定・経路B で (b) と同じ） |
| (c) 共通の 4 回のまま検査だけする（MSP と同形） | 既定でも起動しない | — | 小 | 不採用（既定 T 960 でも 4 × 960 ＞ 1,800。全構成が起動しなくなる） |
| (d) ack してから別キューへ再投入（遅延の再試行） | ✓ | ✓ | 大（durable な予約送信・試行回数の持ち越し） | 不採用（本サービスは DB を持たない。遅れた起点は鮮度の判定で捨てられ、再試行の意味が薄い） |
| (e) 待ちを起点ごとの宣言（`NewsStatusValidFor`）から実行時に読む | ✓ | ✓ | 中 | 不採用（待ちが起動時に決まらず「起動時に止める」に合わない） |

## 統制と現在の実現手段

| 統制 | 現在の実現手段 |
| --- | --- |
| 再試行の連鎖全体が consumer_timeout に収まる | `ScheduledCycleRetryChain`（T-10-2403）＋ `ScheduledCycleRetryPolicy`（本番の組み立てを T-10-2406 が固定） |
| 収まらない構成は起動しない | 同 `Derive` の例外（T-10-2404。純関数と本番の組み立て） |
| 既定・経路B の定時サイクルは打ち切られても再試行しない | 同ポリシー（受信の実行器で T-10-2405） |
| 他のハンドラの失敗の規則は変えない | T-10-2406（価格変動の判断のチェーンに規則が無い） |

## 結果

- 良い点: 定時サイクルが打ち切られても、1 通がブローカの `consumer_timeout` を超えて握られることが無くなる（既定・経路B では LLM の二重呼び出しが起きない）。
  前提の上げすぎは起動時に止まる。
- 悪い点・残余リスク:
  - **打ち切られたサイクルは判断ごと失われる**（既定・経路B）。打ち切りの前に判断した銘柄の発行も捨てられる。次の巡回が判断し直す。
    前提以内の監視銘柄では 1 銘柄の締め切りが打ち切りを防ぐので、通常は起きない。
  - 失敗した `InformationCollected` は `trade-decision-service.InformationCollected_error` に残る（**アラートは無い**。検知はログ `Failed to process message` と
    キューの滞留）。再投入しても古い起点は鮮度の判定で捨てられるので、再投入は不要（消してよい）。
  - W は配備の巡回 300 秒を前提にした定数である。巡回が T より長い構成（待ちが無い）では誤って弾き得る（作業仕様書の窓の表 P3）。配備に該当構成は無い。
  - 巡回が 300 秒より長く、かつ T がその巡回より長い構成では、鮮度の宣言（巡回の 2 倍）が 600 秒を超え、待ちを過小に見積もる。巡回を変えるときは本決定の W を引き直す。
  - `consumer_timeout` を配備で変えたら `Messaging__BrokerConsumerTimeoutSeconds` も合わせる（chart も MSP の platform-infra も現状変えていない）。
  - 🔴 **LLM の timeout を延ばす構成も起動を止め得る。** 既定の前提 10 銘柄・二段では 600 ＋ 10 × (2 × timeout ＋ 30) ＋ 60 ＜ 1,800 から
    **timeout は 41 秒まで**（42 秒で 1,800 ちょうど）。既存の試験 1 件（`LlmCompletionClientSelectionTests` の明示設定の例 90 秒）を 40 秒へ改めた。
    配備（chart・docker-compose）は timeout を空（既定 30 秒）にしており該当しない。
  - 停止（SIGTERM）で打ち切られたサイクルも `_error` へ送られ得る（従来は再試行）。次の巡回が判断し直す。
