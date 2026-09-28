---
title: IADR-0452 AI 判断後の見送り（Hold を含む）は新イベント TradeDecisionHeld で急変の基準値を判断時点の価格へ進め、判断をしなかった見送りと解析不能では進めない
type: impl-adr
status: Accepted
related_ids: [UC-02, UC-01, FR-03, FR-02, FR-04, FR-11, ADR-0003, IADR-0014, IADR-0023, IADR-0079, IADR-0099, IADR-0129, IADR-0248, IADR-0358, IADR-0374]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs:
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0452: AI 判断後の見送りで急変の基準値を進める（#1077）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-29
- 決定者: Claude Code（実装）。計画どおりに直す実装側の不具合であり、裁定は要らない

## 起点・関連

- 関連する計画書 ID: UC-02（基本フロー 2「前回判断時点からの変動率」）・FR-03（既定: 前回判断時点比 ±3%）・UC-01 事後条件（判断結果＝**発注あり/見送り**）・ADR-0003（AI 判断）。
  計画 `04_workflows/02_event-driven-trading.md` §補足・例外処理「閾値の基準点: 前回 AI 判断を行った時点の価格」。
- 関連する実装仕様書: [`.ai-context/specs/20260929_1077_baseline-advances-on-hold.md`](../specs/20260929_1077_baseline-advances-on-hold.md)
- 前提: [IADR-0014](IADR-0014_market-monitor-events-and-boundary.md)（基準値＝前回 AI 判断時点の価格）、[IADR-0374](IADR-0374_decision-skip-reasons-and-first-alert-rule.md)（見送りの唯一の出口と 13 値の語彙）、
  [IADR-0248](IADR-0248_parse-failure-vs-hold-distinction.md)（解析不能と見送りの区別）、[IADR-0129](IADR-0129_wolverine-messaging-topology.md)（キュー名）、[IADR-0079](IADR-0079_event-backward-compat-contract-test.md)（契約の追加のみ）
- 採番: #1079（#1035）と番号が衝突したため、後からマージする本 PR が最大＋1 の 0452 へ改番した（2026-09-29。欠番は作らない）。
- 関連 issue: #1077（起点）。#1035（定時の判断に値動きを渡す。本件と並ぶ「稼働 PoC で注文が出ない」構造上の原因。本件は #1035 の範囲に触れない）

## コンテキストと課題

急変の基準値は市場監視が `TradeDecisionMade` を受けたときだけ更新していた。`TradeDecisionMade` は発注意図がある判断にしか出ない。
Hold を含む見送りはすべて `null` を返し、何も出さない。基準値が無い銘柄は変動を判定しない。

稼働 PoC（2026-09-28 22:30〜23:55 JST、AST `1465f3e`）では定時判断 6 銘柄 × 17〜18 回がすべて Hold で、**UC-02 は一度も発火しなかった**。
計画は基準点を「前回 AI 判断を行った時点の価格」と定め、UC-01 は見送りを判断結果に数える。Hold も AI 判断であり、基準点になるべきである。

決めることは 3 つ: (1) どの見送りを基準点にするか、(2) 価格をどこから取るか、(3) 市場監視へどう伝えるか。

## 検討した選択肢

### 伝え方

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A（採用）: 新イベント `TradeDecisionHeld`** | 取引判断が判断後の見送りで発行し、市場監視が購読して基準値を更新する | 既存の契約・購読者を変えない（追加のみ・IADR-0079）。キューは `<ServiceName>.TradeDecisionHeld` で自動に分かれる（IADR-0129 決定1）。代償は全イベント監査（FR-11）の規約で監査ハンドラと写像が 1 つずつ増えること |
| B: `TradeDecisionMade` に結果（Hold）を載せる（省略可能項目） | 基準値のハンドラはそのまま | **採らない**。`TradeDecisionMade` は発注の経路であり、リスク管理・監査・報告書が「発注意図がある」と読む。Hold を載せるには全購読者に分岐を足す必要があり、1 つでも漏れると Hold が承認・発注へ流れる（安全側の逆） |
| C: `TradeDecisionSkipped` を流用する | 既存イベント | **採らない**。これは「割当モデルが使えず判断を実行しなかった」（ADR-0017 決定2）であり、通知と日報の回数に載る。**判断をした**見送りを載せると意味が逆になる（IADR-0358 決定4 が流用を誤帰属として棄却したのと同じ理由） |
| D: 市場監視が別の契機で基準値を作る（価格変動イベントの発行時・定時サイクルの開始時・基準値が無ければ最初の相場） | 取引判断を変えない | **採らない**。判断をしなかった回（日報未確定・現在値なし等）でも基準値が動き、計画の「AI 判断を行った時点」から外れる。基準値を固定点に置いたままだと、Hold のたびにクールダウン（15 分）ごとに同じ乖離で再発火し、LLM 費用が膨らむ |

### 基準点にする見送りの範囲

| 案 | 評価 |
| --- | --- |
| **A（採用）: LLM が結論を出した後の見送り 9 地点（`LlmHold` と、Buy/Sell の結論を統制が見送らせた 8 地点）** | 計画の文言「AI 判断を行った時点」に忠実。統制で止めた回も AI は判断している |
| B: `LlmHold` だけ | 統制で止めた回（例: 数量 0）は判断済みなのに基準点にならず、同じ乖離で再発火し続ける |
| C: すべての見送り（LLM を呼ぶ前の 4 地点を含む） | 判断をしていない回で基準値が動く（計画から外れる） |

### 価格の源

| 案 | 評価 |
| --- | --- |
| **A（採用）: 現在値（有効時） → 起点の価格（価格変動トリガー） → LLM の参照価格（正のときだけ）。無ければ発行しない** | 手元の実価格を優先する。Buy/Sell の経路（`TradeDecisionMade` の参照価格＝現在値 → LLM の参照価格）と向きがそろう。Hold の参照価格は 0 なので、Hold では LLM の値を使わない |
| B: 市場監視が受信時に自分の相場源で照会する | 相場源はそろうが、受信時刻は判断時刻より遅れ、照会 1 回分の費用（FR-01）と失敗経路が増える。既存の `TradeDecisionMade` 経路も取引判断の価格を使っている |

## 決定

### 決定 1 — 基準点にするのは「AI 判断が結論を出した後の見送り」

`DecisionSkipReason` 13 値のうち、LLM 呼び出しの**後**の 9 地点（`LlmHold`・`HoldingsUnknownOpen`・`NakedShortOpen`・`WorkingEntriesUnknownOpen`・`ReferencePriceInvalid`・`FxRateStaleOpen`・`StopLossDistanceInvalid`・`SizingZeroQuantity`・`ProfitabilityNotViable`）で基準値を進める。
LLM 呼び出しの**前**の 4 地点（`DailyPolicyUnconfirmed`・`CurrentPriceUnavailable`・`FxRateUnresolved`・`FxRateStaleNoHolding`）では進めない（判断をしていない）。
**解析不能は結論ではない**（IADR-0248）: 一次スクリーニングが解析不能、または二次の全票が解析不能で Hold に倒れた回は進めない。解析できた票が 1 票でもあれば結論とみなす。

### 決定 2 — 新イベント `TradeDecisionHeld` と市場監視の購読

`TradeDecisionHeld(EventId, Symbol, Market, Price, Reason, DecidedAt, CycleTrigger?)` を `Shared.Contracts.Events` に追加する（新規型。既存イベントは不変）。
`Reason` は `DecisionSkipReason` の名前（観測・監査の読み手向け。購読側は分岐に使わない）。`CycleTrigger` は `TradeDecisionMade` と同じ語彙。
市場監視は `TradeDecisionHeldBaselineHandler` で `IPriceBaselineStore.SetBaseline` を呼ぶ。価格が正でなければ更新しない（受け側の守り）。リスク管理は購読しない。

### 決定 3 — 価格は「現在値 → 起点の価格 → LLM の参照価格（正のとき）」、無ければ発行しない

0 や推測の価格で基準値を動かさない。

### 決定 4 — 発行はポート `IDecisionHeldReporter`、出口は `SkipJudgedAsync`

`IScreeningReductionReporter` と同じ作法（省略可能・既定 NoOp・Worker が `PublishingDecisionHeldReporter` を scoped で配線）。
判断後の見送り 9 地点は `SkipJudgedAsync` を通り「発行 → 唯一の出口 `Skip`」とする。見送りの理由の計上は従来どおり `Skip` の 1 件である（IADR-0374 の規律を保つ）。
**発行の失敗で見送りを壊さない**（キャンセル以外の例外は Warning で握り、`null` を返す）。キャンセルは伝える。
`TradeDecisionAppService` の変更は見送りの分岐・LLM 判断直後の 1 行・コンストラクタ末尾の引数に限る（#1035 が触るプロンプトの組み立て・価格の取得は変えない）。

### 決定 5 — 監査台帳に記録する

全イベント監査（FR-11。`AuditConsumerCoverageTests`・`AuditCycleCompletenessTests` の写像と標本の完全一致）の規約により、`TradeDecisionHeldAuditHandler` と `AuditEntryFactory.From(TradeDecisionHeld)` を足す。
相関は `EventId`（`PriceMovementDetected` と同じ）。**根拠（rationale）は運ばない** —— Hold の根拠の唯一の記録が FR-11 ログであることは変わらない（`DecisionOrchestrator` 等の既存の注記は正しいまま）。

## 理由

- 案 A（新イベント）だけが、発注の経路（`TradeDecisionMade`）の意味を変えずに、計画の基準点（判断をした回）へ一致させられる。
- 範囲 A は計画の文言にそのまま対応し、境界（LLM 呼び出しの前後）がコード上 1 行（`JudgedPriceOf` の呼び出し位置）で読める。

## 結果

- 良い影響: Hold が続いても基準値が直近の判断時点の価格へ進み、その後の閾値超過で UC-02 が発火する。銘柄追加直後も、最初の AI 判断（定時の Hold を含む）で基準値が作られる。
- 悪い影響・トレードオフ:
  - イベント・監査行が判断後の見送り 1 回につき 1 件増える（定時 6 銘柄なら 1 巡回 6 件程度）。
  - 基準値は市場監視自身の相場源ではなく取引判断の価格で進む（`TradeDecisionMade` の既存経路と同じ。相場源が違う構成では小さなずれが残る）。
- 残余:
  - **現在値の供給が無効な構成の定時判断の Hold** は判断時点の価格を持たず、基準値を進めない（本番 values.yaml の既定 `MarketData__Provider=""`。稼働 PoC の values-local は finnhub で有効なので当たらない）。定時判断へ価格を渡す改修は #1035 の範囲。
  - **初期基準値**: 基準値ストアは EF で永続し、再起動をまたいで残る（インメモリ構成では再起動で空になる）。前回判断が無い間は変動を判定しない（計画の文言どおり）。
- フォローアップ: なし（本 PR で完結）。

## 試験

| 観点 | 試験 |
| --- | --- |
| Hold で発行・価格の優先順・定時の Hold・価格が無ければ非発行・判断前 4 地点で非発行・判断後の統制 5 地点で発行・解析不能で非発行・一次 Hold と一部解析不能で発行・成立時は非発行・発行失敗でも見送り継続 | `TradeDecisionService.Tests/.../DecisionHeldReportTests.cs`（10 件） |
| 本番の組み立てで発行実装へ結線（個数・型・判断サービスの保持） | `DecisionHeldReporterRegistrationTests.cs`（3 件） |
| 本物の発行実装が `TradeDecisionHeld` を送る | `ComposedRealImplementationsTests`（1 件追加） |
| 市場監視が基準値を更新・連続 Hold で前進・非正は無視・**Hold が続いても急変が発火し得る**・キュー名 | `MarketMonitorService.Tests/.../TradeDecisionHeldBaselineHandlerTests.cs`（5 件） |
| 契約（識別子の固定・後方互換の基準・監査の写像と標本） | 既存の完全一致テストへ追加 |

## 関連

- Supersedes: なし
- Superseded by: なし
