---
title: IADR-0463 新規建てが必ず拒否される銘柄は LLM を呼ぶ前に見送る。可否はリスク管理が審査と同じ述語で返し、判断側は読むだけ（審査は残す）
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-04, FR-11, NFR, UC-01, UC-02, ADR-0003, ADR-0009, IADR-0394, IADR-0358, IADR-0462, IADR-0374, IADR-0390, IADR-0346, IADR-0420, IADR-0427, IADR-0008, IADR-0119, IADR-0452]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-09-30
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10, FR-04)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003 (リスク管理の権威と直列の配置)
---

# IADR-0463: 新規建てが必ず拒否される銘柄は LLM を呼ぶ前に見送る（#1113）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-09-30
- 決定者: オーナー裁定（[#1113](https://github.com/endazon/ai-stock-trading/issues/1113) の 2026-09-30 のコメント）を claude が実装

## 起点・関連

- 関連する計画書 ID: FR-10（統制）・FR-04（判断）・FR-11（監査）。計画 ADR-0003（リスク管理の権威と直列の配置）は変えない。
- 起票: [#1113](https://github.com/endazon/ai-stock-trading/issues/1113)
- 関連する実装仕様書: [`.ai-context/specs/20260930_1113_entry-blockers-before-llm.md`](../specs/20260930_1113_entry-blockers-before-llm.md)（母集合・窓の表・試験）
- 改める: [IADR-0394](IADR-0394_stop-out-same-day-reentry-block.md) 決定 8（判断側は変えない）と、同 §検討した選択肢の案 B の不採用。
- 前提:
  - [IADR-0358](IADR-0358_skip-open-when-holdings-unknown.md) 決定 2 / [IADR-0119](IADR-0119_decision-derived-close.md): 保有が不明なら新規建てを見送り、決済は止めない。保有 0 の売りは裸の新規ショートとして見送る。
  - [IADR-0390](IADR-0390_working-entries-in-decision-input.md): 未約定の新規建ては第 3 の状態。不明を空に倒さない。
  - [IADR-0462](IADR-0462_ledger-position-query-status-and-pre-llm-skips.md) 決定 4: LLM を呼ぶ前の見送りは `TradeDecisionForgoneBeforeLlm` を 1 回ごとに出す。
  - [IADR-0420](IADR-0420_cross-service-read-contract-convention-and-guard.md) / [IADR-0427](IADR-0427_risk-read-grpc-stage2.md): 跨サービスの読み取りの契約と gRPC の原則 A。

## 背景

PoC（2026-09-29 夜）で、`TradeDecisionMade`（Buy/Open）37 件のうち承認は 5 件だった。拒否は `StoppedOutSameDay` 34 件・
`MaxPositionsExceeded` 15 件（重複あり）。**審査で必ず落ちる新規建てのために、判断は毎サイクル LLM を呼んでいた**。
月次の LLM 費用の上限に早く達し、健全な銘柄の判断の頻度まで落ちる。

IADR-0394 は案 B（判断側で損切りを知って見送る）を「同じ規則を 2 か所に置く」「LLM に任せない」として退け、
決定 8 で「判断側は変えない。見送りとしても数えるならオーナー確認と別 issue」とした。**#1113 はその別 issue である。**

## 決定

### 決定 1: 両端で止める（審査は残す）

判断が LLM を呼ぶ前（T0）に「新規建てが必ず落ちる」銘柄を見送り、**審査（T1）は変えない**。前で省くのは費用の最適化であって
統制ではない。照会の失敗・未結線は **LLM を呼ぶ側**へ倒す（見送らない。審査が止める）。

| 形 | 増える側（T0〜T1 に塞がる） | 減る側（T0〜T1 に空く） |
| --- | --- | --- |
| 後の端だけ（審査だけ＝是正前） | 拒否 | 承認。塞がっている間は毎サイクル LLM を呼ぶ |
| 前の端だけ（審査を外す） | **通る（統制の穴）** | 見送り |
| **両端（採用）** | 拒否 | 1 サイクル見送り（安全側） |

### 決定 2: 規則はリスク管理の 1 か所（審査と同じ述語）

- 状態だけで確定する述語を `Domain/EntryStateBlockers` に置き、`RiskEvaluator.Evaluate` は**同じ位置で同じ関数を呼ぶ**。
- `EntryStateBlockers.Determine(方向, 設定, スナップショット, 当日の損切り, ロックアウト)` が、確定する理由だけを審査の並びで返す。
- 日次損失のロックアウトは `OrderScreeningService.IsLockoutActive`（静的）を審査と共有する（口は掃除しない）。
- 対象は「新規建てだけを拒否し、注文の数量・価格・商品種別に依存せず、状態が既知」の **7 理由**:
  `KillSwitchActive`・`TradingPaused`・`StoppedOutSameDay`（方向別）・`GoodFaithViolationLimitReached`（件数が既知のとき）・
  `MaxPositionsExceeded`・`DailyLossLimitReached`（判定コアの到達、またはロックアウト）・`MaxDrawdownReached`。
- 🔴 **不明は返さない**（裁定 3）: `StopOutStatusUnknown`・`CapitalBaselineUnavailable`・`BrokerAccountTypeUnverified`・
  `InformationSourceDegraded`（縮退と不明を 1 ビットで持つ）・GFV の件数の未供給。
- 金額に依存するもの（1 注文・日次枠・段階資金・現金口座の決済済み資金）、注文の属性に依存するもの（商品種別・段階の商品制約・
  差金決済防止・相場操縦・発注先）、決済にも掛かる規則（禁止銘柄・無効な市場）、空売り専用の 9 種は対象外（作業仕様書の母集合の表）。

### 決定 3: 読み取り口（銘柄単位・方向別）

- REST `GET /risk-controls/entry-blockers?symbol&market`（`OwnerOrService`。欠落・未定義の市場は 400）。
  応答 `EntryBlockersView(Symbol, Market, LongSide, ShortSide)`。
- gRPC `RiskControlsRead/GetEntryBlockers`（同じサービス。欠落は `INVALID_ARGUMENT`）。理由は新しい列挙 `EntryBlocker`（対象 7 理由と
  `UNSPECIFIED`）で名前で写す。方向ごとの入れ物の欠落は不明、空の入れ物は「確定する拒否は無い」。対象外の理由は送り手で例外。
- `EntryBlockersService` は審査と同じ入力（設定・`PortfolioSnapshotBuilder`・`StopOutProjection.Project`・ロックアウト・`TradingDay.Of`）を読む。

### 決定 4: 判断側は結果を読むだけ（保有 0・未約定なしの新規建てだけを省く）

- 新しいポート `IEntryBlockersProvider`（Http / Grpc / NoOp。選び方は保有照会と同じ）。NoOp は常に不明。
- `TradeDecisionAppService` の保有・未約定の照会と換算レートの鮮度切れの見送りの後、RAG の前に置く。次のすべてを満たすときだけ
  `TradeDecisionForgoneBeforeLlm(EntryBlockedByRiskControls)` で見送る:
  - 保有が**既知で 0**、未約定の新規建てが**既知で空**（保有中・未約定あり・不明の銘柄では照会もしない＝決済の判断は必ず残す。IADR-0358 決定 2 と同じ線引き）。
  - 可否の照会が成功し、**LongSide** が空でない。保有 0 の売りは LLM の後に `NakedShortOpen` で必ず見送られるため ShortSide は見ない
    （方向別の答えは将来、保有 0 の空売りを許すときのために返す）。
- 新しい理由 `EntryBlockedByRiskControls` は `DecisionForgoneBeforeLlmReason` と `DecisionSkipReason` の**末尾**へ足す（名前は一致）。
  `TradeDecisionHeld` は出さない（[IADR-0452](IADR-0452_baseline-advances-on-judged-skip.md) 決定 1）。

### 決定 5: 計器の移動を受け入れる（裁定 4）

`ast.risk.rejections{reason}` の一部（塞がっている銘柄の新規建て）が、判断側の `ast.trade_cycle.decision_skips{reason="EntryBlockedByRiskControls"}`
と監査台帳の `TradeDecisionForgoneBeforeLlm` へ移る。月報・ダッシュボード・夜間の要約（§11）の読み方の注記を同じ PR で直す。
**審査の拒否が減っても統制が緩んだのではない**（審査は変わらない）。

## 採らなかった案

- **案 A**（`SizingContext` に建玉数と当日の損切りの一覧を足し、判断側で比べる）: 比較と方向の選択が判断側に複製される。
- **案 C**（判断側が `OrderRejected` を購読して、その日のうち抑止する）: 規則と取引日の状態を判断側に持つことになり、二重化そのもの。
- **口の対象を StoppedOutSameDay と MaxPositionsExceeded に限る**: 裁定 2 が「必ず落ちる状態すべて」に広げた。
- **判断側で ShortSide も見る**: 保有 0 の売りは既に必ず見送られる（LLM の後）。ShortSide だけ塞がる銘柄で LLM を省くと、
  買いの判断まで失う。
- **不明（StopOutStatusUnknown 等）でも省く**: 裁定 3 が退けた。不明は一過性であり、審査が止める。

## 結果

- 良い影響: 塞がっている銘柄で LLM（一次＋本判断）を呼ばない。見送りは理由つきで台帳と計器に出る。
- 悪い影響・トレードオフ:
  - 保有 0・未約定なしの銘柄では判断ごとに可否の照会が 1 回増える（5 秒で打ち切り。失敗は LLM を呼ぶ側）。
  - LLM の遅延の間に枠が空いた場合、1 サイクル分の機会損失（決定 1 の表）。
  - `MarketDisabled`・`BannedSymbol` は対象外（決済にも掛かる規則。監視銘柄の設定と重なるため費用の実害は小さい）。
- 試験: T-10-1782〜T-10-1796（`docs/tests/FR-10_risk-controls-tests.md`）。
