---
title: IADR-0506 Stage 0 の記録器は最小の名目額を本番と同じ関数で判定して記録に残し、再生器は再生の時点で新規建てになる注文にだけ最小の名目額と判断由来の決済の後の同日・同方向を本番と同じ述語で当てる
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-15, FR-04, ADR-0033, ADR-0054, ADR-0011, IADR-0495, IADR-0318, IADR-0498, IADR-0351, IADR-0387, IADR-0043, IADR-0260, IADR-0281, IADR-0163]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0033 (決定 2: Stage 0 は記録・再生方式。再生は純関数)
  - planning:projects/ai-stock-trading/07_adr/ADR-0054 (決定 3・4: Stage 0 は本番と同じ系で走らせる)
---

# IADR-0506: Stage 0 の記録・再生に、最小の名目額と判断由来の決済の後の同日・同方向を本番と同じ判定で再現させる（#1209）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-08
- 決定者: claude（#1191 の独立監査が後続として起票を求めた #1209 の実装。統制の値・方向は IADR-0495 のまま変えない）

## 起点・関連

- 関連する計画書 ID: **FR-10**（統制）。FR-15（バックテスト）は再生の地点。計画 ADR-0033 決定 2・ADR-0054 決定 3。
- 対象 Issue: [#1209](https://github.com/endazon/ai-stock-trading/issues/1209)（#1176 の後続）
- 関連する実装仕様書: [20261008_1209_stage0-replay-min-notional-and-decision-exit](../specs/20261008_1209_stage0-replay-min-notional-and-decision-exit.md)
- 関連 IADR: [IADR-0495](IADR-0495_min-entry-notional-and-decision-exit-same-day-reentry.md)（本番の 2 統制。§結果で Stage 0 の不再現を残した）、
  [IADR-0318](IADR-0318_stage0-ai-decision-record-and-replay.md)（記録・再生の分担）、[IADR-0498](IADR-0498_stage0-two-tier-recording-and-live-gate-prerequisite.md)（記録器の二段化）、
  [IADR-0351](IADR-0351_held-position-in-decision-prompt.md) 決定 7（記録器は保有なしの枝だけ）、[IADR-0043](IADR-0043_backtest-foundation.md)（戦略は純関数）、
  [IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md)（共有カーネル）。

## コンテキストと課題

IADR-0495 は本番に 2 統制を入れた —— (1) 最小の名目額（サイジングの直後に `SizedBelowMinimumNotional` で見送る）、(2) 判断由来の決済の後の
同日・同方向（審査が `DecisionExitSameDay` で拒否する）。同 §結果は「Stage 0 の記録・バックテストは本件の 2 統制を再現しない」と残した。
このままでは Stage 0 の合格判定（計画 ADR-0054 決定 3・4）が、本番では見送られる 1% 未満の建玉と再エントリーを含む系を測る。

制約（実測）:

- 記録器は銘柄 × 判断日ごとに独立で、保有なしの枝だけを記録する（IADR-0351 決定 7）。**保有を知らない。**
- 再生器は記録の数量を**差分**の注文として写し、建玉はシミュレータが積む。判断日 D の注文は D+1 の始値で約定する。
- 同じ再生器を基準・コスト 2 倍・ウォークフォワードの各窓で使い回す（各走行は建玉ゼロから）。**走行をまたぐ状態を持てない。**
- 再生側（バックテスト）はリスク管理を参照できない。共有カーネルは両方から読める。

## 検討した選択肢

### 最小の名目額の置き場

| 案 | 判定 |
| --- | --- |
| A1. 記録器で判定し、満たなければ数量を 0 にする | **不採用**。再生では記録の Sell / Buy が建玉の決済として働くことがある。本番は決済に名目額を掛けない（FR-10「手仕舞いは止めない」）。数量を消すと決済が消え、建玉が開いたまま残る |
| A2. 再生器で equity とサイジングを持ち直して判定する | **不採用**。再生でサイジングを再計算しない（IADR-0318。評価対象が「記録した判断＋いまのサイジング規則」になる）。記録時点の資金は記録に無い |
| A3. **記録器が本番と同じ関数・同じしきい値の構成で判定して記録に残し（数量は変えない）、再生器が新規建てにだけ適用する** | **採用**。判定は記録時点の資金・価格（サイジングと同じ値）で、適用は建玉を知る側 |

### 判断由来の決済の後の同日・同方向の置き場

| 案 | 判定 |
| --- | --- |
| B1. 記録器で判定する | **不採用**。記録器は保有も決済も知らない |
| B2. 再生器に規則を複製する | **不採用**。規則が 2 か所になり、片方だけが変わる（Stage 0 で合格した系と本番の系がずれる） |
| B3. **規則を共有カーネルの純関数（`DecisionExitReentry`）へ移し、本番の射影と再生器の両方がそれを通る** | **採用**。本番の射影（`DecisionExitProjection`）は由来・市場で絞り、時刻を市場の現地取引日へ写して委ねるだけになる（挙動不変） |

### 再生の時間軸での「当日」

| 案 | 判定 |
| --- | --- |
| C1. 判断日だけを見る（決済の承認日＝判断日） | **不採用**。再生は 1 銘柄 1 日 1 件に畳むので決して当たらず、D の決済が D+1 の始値で約定した後の D+1 の同方向の判断を通す。本番の述語（約定が当日なら数える）と食い違う |
| C2. **本番の述語をそのまま: 承認の取引日＝判断日、約定の取引日＝約定したバーの日。当日＝新規建ての判断日** | **採用**。仕様書の規則 11 の表（3 通りの形 × 3 つの場面）で、本番の述語と一致するのはこの形だけ |

### 走行をまたぐ状態

| 案 | 判定 |
| --- | --- |
| D1. 戦略に建玉と決済の履歴を持たせる | **不採用**。同じ戦略を複数の走行で使い回すため、前の走行の状態が次の走行へ漏れる |
| D2. **呼ばれるたびに、その走行で当日までに渡されたバー（`BacktestContext.History`）から建玉と決済を組み直す** | **採用**。シミュレータと同じ規則（建玉ゼロから・次の取引日の始値で・バーがあれば約定）で組み直すので、当日の建玉はシミュレータと一致する。費用は 1 呼び出しあたりバーの本数に比例（Stage 0 の期間・銘柄数では問題にならない） |

## 決定

### 決定 1: 記録器は最小の名目額を判定して記録に残す（数量は変えない）

- 記録（`Stage0DecisionRecord`）の末尾に **`bool? EntryBelowMinimumNotional = null`**。記録器は、サイジングの結果が数量 > 0 のとき、**本番と同じ
  `MinimumEntryNotional.IsBelow`**（名目額＝数量 × 参照価格〔基準通貨〕、equity＝サイジングの資金）で判定して載せる。Hold・数量 0 は null。
- しきい値は**本番の判断と同じ構成**（`Sizing:MinEntryNotionalRatio` の DI の単一の値）。記録器の構築で省略すれば既定（1%）で効く（IADR-0163 決定 2）。
- 戦略 ID は判定を持つ記録のときだけ判定を含める（`|n:1` / `|n:0`）。判定を持たない記録（本項目より前の形）の戦略 ID は変わらない。

### 決定 2: 再生器は新規建てになる注文にだけ 2 統制を当てる

- 再生の時点の建玉で注文を分ける: 建玉 0、または建玉と同じ符号 → **新規建て**。符号が逆（建玉を跨ぐものを含む）→ **判断由来の決済**（再生の注文は
  すべて記録した判断から出る）。決済は 2 統制を当てずに写す（FR-10「手仕舞いは止めない」）。
- 新規建ては本番と同じ順で評価する: まず判断由来の決済の後の同日・同方向（共有カーネルの述語。理由 `RejectionReason.DecisionExitSameDay`）、
  次に最小の名目額（記録の判定が true。理由 `DecisionSkipReason.SizedBelowMinimumNotional`）。当たれば写さない。
- 判断由来の決済は、承認の取引日＝判断日、約定の取引日＝約定したバーの日として述語へ渡す。当日＝新規建ての判断日。
- 再生器は `Replay(history, asOf)` で当日の注文と、その走行で当日までに見送った新規建て（判断日・銘柄・数量・理由）を返す。理由は本番の列挙そのもの。

### 決定 3: 規則は共有カーネルに 1 つだけ置く

- `AiStockTrading.Shared.Kernel.Trading.DecisionExitReentry`（純関数）: 取引日で表した決済の列と当日から方向ごとの有無を返す `Project`、
  新規建ての方向が当たるかの `BlocksEntry`。
- リスク管理の `DecisionExitProjection` は由来（`TradeDecision`）・市場で絞り、`TradingDay.Of(・, market)` で取引日へ写して委ねる。
  `DecisionExitReentrySupply.ForEntry` も `BlocksEntry` へ委ねる。本番の挙動は変えない（既存の試験がそのまま通る）。

## 結果

- 良い影響: Stage 0 の記録・再生が、本番では見送られる 1% 未満の新規建てと、判断由来の決済の後の同日・同方向の新規建てを、本番と同じ理由で見送る。
  規則が 1 か所なので、本番の統制を変えれば Stage 0 も同じく変わる。
- 悪い影響・トレードオフ:
  - **判定を持たない記録（本項目より前の記録）には最小の名目額を当てない**（判定できない）。2026-10-08 時点で記録は 1 件も作られていない
    （as-of 入力の実供給が未実装。`docs/blocked-tasks.md` B-7）ので、該当する記録は無い。
  - **再生の挙動が変わっても、判定を持たない記録の戦略 ID は変わらない**（IADR-0281 決定 3 の「戦略の変更」の鍵は記録の内容だけ）。本変更より前に
    組んだ verdict は無い（記録が無い）が、仮にあれば作り直すこと。
  - 再生は記録の数量を決済にも使う近似のまま（本番の判断の決済は保有の全量）。建玉を跨ぐ注文を決済として扱うのもこの近似の側である。
  - 再生器は呼ばれるたびに当日までのバーを走査する（走行あたりバー数 × 判断日数）。
- フォローアップ: なし（IADR-0495 §結果の不再現は本 IADR で解消。同 IADR へ日付つき追記で記録する）。

## 関連

- [#1209](https://github.com/endazon/ai-stock-trading/issues/1209) / [#1176](https://github.com/endazon/ai-stock-trading/issues/1176) / PR #1191
- 実装（`backend/` 配下）: `Shared/AiStockTrading.Shared.Kernel/Trading/DecisionExitReentry.cs`・
  `Shared/AiStockTrading.Shared.Contracts/Backtest/{Stage0DecisionRecord,Stage0StrategyIdentity}.cs`・
  `Services/TradeDecisionService/Features/TradeDecision/RecordStage0Decisions/Stage0DecisionRecorder.cs`・`Services/TradeDecisionService/Program.cs`・
  `Services/BacktestService/Domain/RecordedDecisionReplayStrategy.cs`・
  `Services/RiskManagementService/{Features/RiskManagement/DecisionExitProjection,Domain/DecisionExitReentrySupply}.cs`
- テスト: T-10-2410〜T-10-2417（`docs/tests/FR-10_risk-controls-tests.md`）
