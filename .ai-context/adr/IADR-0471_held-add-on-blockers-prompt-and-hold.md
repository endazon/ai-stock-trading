---
title: IADR-0471 保有中の銘柄でも新規建ての可否を読み、買い増し・売り増しが審査で必ず拒否されるならプロンプトで選べないと伝え、LLM が返しても Hold に倒す（LLM は必ず呼ぶ・審査は残す）
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-04, FR-11, NFR, UC-01, UC-02, ADR-0003, ADR-0016, IADR-0463, IADR-0394, IADR-0358, IADR-0351, IADR-0390, IADR-0452, IADR-0374, IADR-0119, IADR-0346]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10, FR-04)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003 (リスク管理の権威と直列の配置)
---

# IADR-0471: 保有中の銘柄の買い増し・売り増しが必ず拒否されるとき、LLM に選ばせず、返しても Hold に倒す（#1130）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-01
- 決定者: オーナー起票（[#1130](https://github.com/endazon/ai-stock-trading/issues/1130)。「#1113 の裁定の射程を保有中の買い増しにも広げる」）を claude が実装

## 起点・関連

- 関連する計画書 ID: FR-10（統制）・FR-04（判断）・FR-11（監査）。計画 ADR-0003（リスク管理の権威と直列の配置）は変えない。
- 起票: [#1130](https://github.com/endazon/ai-stock-trading/issues/1130)（監査 2026-10-01 F2）
- 関連する実装仕様書: [`.ai-context/specs/20261001_1130_held-add-on-before-llm.md`](../specs/20261001_1130_held-add-on-before-llm.md)（母集合・窓の表・試験・自己変異）
- 改める: [IADR-0463](IADR-0463_entry-blockers-before-llm.md) 決定 4 の「保有中・未約定あり・不明の銘柄では照会もしない」のうち**保有中**の部分（保有中も照会する）。IADR-0463 の他の決定（#1113 の経路・口・審査）は変えない。
- 前提:
  - [IADR-0358](IADR-0358_skip-open-when-holdings-unknown.md) 決定 2 / [IADR-0119](IADR-0119_decision-derived-close.md): 決済は止めない。→ 保有中の銘柄では LLM を必ず呼ぶ。
  - [IADR-0351](IADR-0351_held-position-in-decision-prompt.md): 保有状況節。方針は書き換えない。
  - [IADR-0452](IADR-0452_baseline-advances-on-judged-skip.md) 決定 1: LLM の結論を統制が見送らせたら `TradeDecisionHeld`（判断後の見送り）を出す。
  - [IADR-0374](IADR-0374_decision-skip-reasons-and-first-alert-rule.md): 見送りは唯一の出口を通し、理由の語彙は末尾へ足す。

## 背景

2026-09-30 15:41 UTC、保有中の `MSFT` で LLM（一次と本判断）が買い増し（Buy 436 株）を返し、審査が `MaxPositionsExceeded` で拒否して Discord へ通知した。
IADR-0463 は保有 0・未約定なしの銘柄しか可否を照会せず、保有中の銘柄のプロンプトは「買い増し（Buy）・保有継続・手仕舞い」を常に選択肢に並べる。
費用・拒否の通知が無駄になり、根拠文（「買い増しを検討できる局面」）と結果が食い違う。

審査の確認: 買い増し（保有ロングへの Buy・保有ショートへの Sell）は判断側で `PositionEffect.Open` になり、`RiskEvaluator` の新規建ての述語がすべて同じ順序で掛かる。
建玉数の上限（`EntryStateBlockers.MaxPositions`）は「この注文が建玉を増やすか」を見ないので、上限ちょうどでは買い増しも拒否される（観測の理由）。
リスク管理の口（`EntryBlockersService`）は `Determine(Buy / Sell, …)` を銘柄の保有と無関係に求めるため、`LongSide` / `ShortSide` は買い増し・売り増しの審査の答えでもある。

## 決定

### 決定 1: 保有中の銘柄でも照会する（LLM は必ず呼ぶ）

- 保有が**既知で 0 でない**とき、#1113 の関門の後・RAG の前で可否を 1 回照会する（未約定の状態は問わない）。#1113 の経路（保有 0・未約定なし）は一字一句変えない。
  保有不明・保有 0 で未約定あり／不明は従来どおり照会しない。
- 保有の方向の答え（ロング → `LongSide`、ショート → `ShortSide`）を使う。照会の失敗・例外・未結線は不明で、決定 2・3 を行わない（従来どおり。審査が止める）。
- **リスク管理の口は変えない**（保有中を仮定した別の答えは要らない。試験 T-10-1900 が買い増しでの口と審査の一致を固定する）。

### 決定 2: プロンプトで選べないと伝える

- 保有の方向の答えが空でないとき、本判断の保有状況節の選択肢の行を「本日は買い増し（Buy）を選べません。リスク管理の審査で必ず拒否される状態です（理由: …）。
  保有継続（Hold）・手仕舞い（Sell）のいずれかを判断します。買い増し・売り増しを返しても、システムは発注せず Hold（見送り）として扱います。」へ差し替え、
  買い増しの条件の 2 行（損切りライン到達中の買い増し禁止・方針が支持する場合に限る）を出さない。出口の行は変えない。
- 一次（門）の短縮版も「本日は買い増し（Buy）を選べません。…手仕舞いの検討に値する場合だけ本判断へ進めます。」へ差し替える（門で買い増しの関心を本判断へ通さない）。
- 理由は 7 理由（IADR-0463 決定 2）の日本語名で書く。答えが空・不明・保有 0・保有不明では節は従来と一字一句同じ。

### 決定 3: LLM が返しても Hold に倒す（判断後の見送り）

- 建玉効果が `Open`（決済でない）で、LLM の前に読んだ答えの**その方向**が空でなければ、発注意図を作らず `DecisionSkipReason.AddOnBlockedByRiskControls`（末尾に追加）で見送る。
  既存の判断後の出口（`SkipJudgedAsync`）を通すので、`TradeDecisionHeld(Reason="AddOnBlockedByRiskControls")` が監査台帳に「判断後の見送り」として残り、
  `ast.trade_cycle.decision_skips{reason="AddOnBlockedByRiskControls"}` に 1 件計上される。
- **新しい監査イベントは足さない。** `TradeDecisionHeld` は「AI 判断が結論を得たが、統制が見送らせた」事実の既存のイベントであり、理由は文字列（`DecisionSkipReason` の名前）なので
  イベントのスキーマ（`event-schemas.baseline.json`）は変わらない。`TradeDecisionForgoneBeforeLlm` は使わない（LLM を呼んでいる）。
- 決済（ロング保有の Sell・ショート保有の Buy）は対象外。LLM の間に保有が 0 になった場合の Buy も同じ方向の新規建てで、T0 の答えは同じ述語なので倒す。
- 位置: 建玉効果の解決・未約定が不明の見送りの後、参照価格の検証の前。

### 決定 4: 審査は残す（両端で止める）

| 形 | 増える側（T0〜T1 に塞がる） | 減る側（T0〜T1 に空く） |
| --- | --- | --- |
| 後の端だけ（審査だけ＝是正前） | 拒否 | 承認。塞がっている間は LLM が買い増しを提案し、拒否と通知が出る |
| 前の端だけ（審査を外す） | **通る（統制の穴）** | Hold |
| **両端（採用）** | 拒否（プロンプトは買い増しを示し、変換もしない） | 1 サイクル Hold（安全側） |

倒すのは審査が必ず落とす注文だけで、発注に届く注文を増やす升目は無い。統制の水準は変わらない（IADR-0463 決定 1 と同じ論理）。

### 決定 5: 計器の移動を受け入れる（IADR-0463 決定 5 と同じ）

`ast.risk.rejections{reason}`（と拒否の通知）の一部（保有中の銘柄の買い増し）が、判断側の `decision_skips{reason="AddOnBlockedByRiskControls"}` と
監査台帳の `TradeDecisionHeld` へ移る。観測・ダッシュボードの読み方の注記を同じ変更で直す。審査の拒否が減っても統制が緩んだのではない。

## 採らなかった案

- **保有中の銘柄でも LLM を省く**: 決済の判断が消える（IADR-0358 決定 2 に反する）。
- **プロンプトだけ（変換しない）**: LLM の自制は統制ではない（IADR-0358 が「不明なら Hold」の依頼に頼らなかったのと同じ）。返されれば拒否と通知が残る。
- **`TradeDecisionForgoneBeforeLlm` に理由を足す**: LLM を呼んでいるので「LLM を呼ぶ前」の事実と誤読される。
- **新しい監査イベント（「判断の格下げ」）**: 既存の `TradeDecisionHeld` が同じ事実（結論を統制が見送らせた）を持つ。2 つにすると夜間の要約・月報の読み手が割れる。
- **LLM の後にもう一度照会して減る側を詰める**: 照会の失敗の扱いが 2 か所になる。#1113 と同じ 1 回に揃えた。
- **リスク管理の口に保有中用の答えを足す**: 審査は買い増しに同じ述語を掛けており、答えは同じ。足すと「同じ規則を 2 か所に置く」になる。

## 結果

- 良い影響: 保有中の銘柄で、必ず落ちる買い増しを LLM が提案しにくくなり、返しても審査の拒否・通知が出ない。決済の判断は残る。
- 悪い影響・トレードオフ:
  - 保有中の銘柄では判断ごとに可否の照会が 1 回増える（5 秒で打ち切り。失敗は従来どおり）。
  - LLM の間に枠が空いた場合、1 サイクル分の買い増しの機会損失（決定 4 の表）。
  - `TradeDecisionHeld` は急変の基準値を進める（判断をしたため。是正前の `TradeDecisionMade` も進めていた）。
- 残余（本件では変えない）:
  - 建玉数の上限で、建玉を増やさない買い増しを拒否するか（審査の意味論。変えるならオーナー確認と別 issue）。
  - 保有 0 で未約定の新規建てがある銘柄の追加の新規建ては対象外（LLM を呼び、審査が止める）。
- 試験: T-10-1900〜T-10-1909（`docs/tests/FR-10_risk-controls-tests.md`）。

［2026-10-07 追記 / #1176］口が返す理由が 8 つになった（`DecisionExitSameDay`。[IADR-0495](IADR-0495_min-entry-notional-and-decision-exit-same-day-reentry.md) 決定 3）。プロンプトの日本語名は「本日この方向で判断による手仕舞い（利確など）済み」。
保有中の銘柄の買い増しは、加えて LLM の後に最小の名目額（同 決定 1。`SizedBelowMinimumNotional`）でも見送られる。本文は書き換えない。
