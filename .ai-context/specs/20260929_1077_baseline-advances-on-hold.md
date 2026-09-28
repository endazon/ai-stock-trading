---
title: 判断が Hold・見送りで終わっても急変の基準値を判断時点の価格へ進める（#1077）
type: spec
status: accepted
related_ids: [UC-02, FR-03, FR-02, FR-04, FR-11, ADR-0003, IADR-0014, IADR-0023, IADR-0079, IADR-0099, IADR-0129, IADR-0248, IADR-0374, IADR-0452]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs:
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 判断が Hold・見送りで終わっても急変の基準値を判断時点の価格へ進める

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-03（変動率が閾値〔既定: 前回判断時点比 ±3%〕を超えたら取引サイクルを即時起動）。関連 FR-02（定時・イベント両系統の合流）・FR-04（AI 判断）・FR-11（記録）
- ユースケース（UC）: UC-02 基本フロー 2「前回判断時点からの変動率が閾値を超えたことを検知する」。UC-01 事後条件「判断結果（**発注あり/見送り**）と根拠が記録される」＝見送りも判断結果である
- 画面（SC）: なし
- 関連 ADR: ADR-0003（AI 判断）。実装: IADR-0014（基準値＝前回 AI 判断時点の価格）・IADR-0023（合流点）・IADR-0099（現在値）・IADR-0129（Wolverine のキュー）・IADR-0248（解析不能と見送りの区別）・IADR-0374（見送りの唯一の出口）・IADR-0079（イベント契約の後方互換）
- 計画書リンク: planning `projects/ai-stock-trading/04_workflows/02_event-driven-trading.md` §補足・例外処理「閾値の基準点: 前回 AI 判断を行った時点の価格を基準に変動率を計算する」（隣接クローン `aa068ac` で確認）
- 起点 issue: #1077（稼働 PoC 2026-09-28 22:30〜23:55 JST、AST `1465f3e`。定時判断 6 銘柄 × 17〜18 回がすべて Hold、UC-02 は一度も発火しなかった）

## 目的・背景

急変の基準値は `TradeDecisionMade` を受けたときだけ更新される（`MarketMonitorService/Infrastructure/Steps/TradeDecisionMadeBaselineHandler.cs`）。
Hold を含む見送りは `TradeDecisionAppService.Skip` を通って `null` を返し、イベントを出さない。基準値が無い銘柄は変動を判定しない
（`MarketMonitorAppService` の「基準値未確定（前回判断なし）は変動判定しない」）。

その結果、**全件 Hold が続く間は基準値が一度も作られず（または古いまま）、UC-02 が発火しない**。計画は基準点を「前回 AI 判断を行った時点の価格」と定め、
UC-01 は「見送り」を判断結果に数える。Hold も AI 判断であり、基準点になるべきである。**計画どおりに直す実装側の不具合**であり、裁定は要らない。

## 対象範囲

- 対象:
  - 取引判断サービス: AI 判断が結論を出したのに発注意図を作らなかった見送りで、新イベント `TradeDecisionHeld` を発行する。
  - 市場監視サービス: `TradeDecisionHeld` を購読し、基準値をその価格へ更新する。
  - 共有契約: `TradeDecisionHeld` の追加（新規型。既存イベントは変えない）と、それに伴う監査台帳の記録（全イベント監査の規約 FR-11）。
  - 通信仕様書（`docs/api/events-and-ports.md`）への行の追加。
- 対象外:
  - #1035 の範囲（`TradeDecisionPromptBuilder`・`ICurrentPriceProvider`・`Quote`・`FinnhubMarketDataSource`・`ScreeningContextAssembler`）。**触らない**。
  - `TradeDecisionAppService.cs` は見送りの分岐（`Skip`）と LLM 判断直後の 1 行・コンストラクタ末尾だけを変える。プロンプトの組み立て・価格の取得は触らない。
  - 定時判断に価格を渡す改修（#1035）。本件は判断時点の価格が**既に手元にある**ときだけ基準値を進める。

## 設計

### どの「見送り」を基準点にするか（区別）

`DecideAsync` の見送り 13 地点（`DecisionSkipReason` の全値）を、**LLM が結論を出した後か前か**で分ける。計画の文言「AI 判断を行った時点」に忠実に、**判断をしなかった見送りは基準点にしない**。

| 見送りの理由 | LLM の結論 | 基準点にするか |
| --- | --- | --- |
| `DailyPolicyUnconfirmed`・`CurrentPriceUnavailable`・`FxRateUnresolved`・`FxRateStaleNoHolding` | 呼んでいない（判断していない） | **しない** |
| `LlmHold`（二次の多数決 Hold・一次スクリーニングの Hold を含む） | Hold と結論した | **する** |
| `HoldingsUnknownOpen`・`NakedShortOpen`・`WorkingEntriesUnknownOpen`・`ReferencePriceInvalid`・`FxRateStaleOpen`・`StopLossDistanceInvalid`・`SizingZeroQuantity`・`ProfitabilityNotViable` | Buy/Sell と結論した（統制が発注意図を作らせなかった） | **する**（AI 判断は行われた） |
| 解析不能（一次の解析不能、または二次の全票が解析不能）で `LlmHold` に倒れたもの | 結論を得ていない（IADR-0248 は解析不能を見送りと区別する） | **しない** |

### 基準値にする価格

判断時点の価格は、手元にある実価格を優先する: **現在値（`ICurrentPriceProvider`。有効時） → 起点の価格（価格変動トリガーの `PriceMovementDetected.Price`） → LLM の参照価格（正のときだけ。Buy/Sell の結論に限られる）**。
いずれも無い（例: 現在値の供給が無効な構成の定時判断で Hold）ときは発行しない（基準値は動かさない。残余に記録）。

### 伝え方（代替案は IADR-0452）

- 新イベント `TradeDecisionHeld(EventId, Symbol, Market, Price, Reason, DecidedAt, CycleTrigger?)` を `Shared.Contracts.Events` に置く。
- 取引判断は新ポート `IDecisionHeldReporter`（既定 NoOp・Worker が `PublishingDecisionHeldReporter` を配線。`IScreeningReductionReporter` と同じ作法）で発行する。
  見送りの唯一の出口 `Skip` の規律（IADR-0374）は保ち、判断後の見送りは `SkipJudgedAsync` を通して「発行 → `Skip`」とする。**発行の失敗で見送りを壊さない**（キャンセル以外の例外は Warning で握る）。
- 市場監視は `TradeDecisionHeldBaselineHandler` で基準値を更新する（キュー `ai-stock-trading.market-monitor-service.TradeDecisionHeld`。IADR-0129 決定 1 の命名で自動）。価格が正でなければ更新しない。
- 監査は `TradeDecisionHeldAuditHandler` と `AuditEntryFactory.From(TradeDecisionHeld)`（全イベント監査・写像・標本の 3 つの完全一致テストが要求する）。
- `TradeDecisionMade`（発注の経路）は変えない。Hold を `TradeDecisionMade` に載せる案はリスク管理が発注意図として読むため採らない。

### 初期基準値（起動直後・銘柄追加直後）

- 基準値ストアは EF（永続）で、再起動しても前回の基準値が残る。インメモリ構成では再起動で空になる。
- 銘柄追加直後は基準値が無く、**最初の AI 判断（定時サイクルの Hold を含む）が基準値を作る**。本修正の前は Buy/Sell が出るまで作られなかった。
- 「前回判断が無い間は判定しない」は計画の文言（基準点＝前回 AI 判断時点）と整合するため変えない。

## 母集合（規則 9・10: 誤りの側で走査する）

走査（`origin/develop` `9ee4b0ca`、`.ai-context/specs` を除く）:
`git grep -n "SetBaseline\|基準値未確定\|基準値の更新\|基準値更新\|前回判断なし\|TradeDecisionMade を発行しない\|TradeDecisionMade 購読"`（Migrations を除く）。

| 箇所 | 内容 | 本 PR |
| --- | --- | --- |
| `MarketMonitorService/Features/MarketMonitor/IPriceBaselineStore.cs:6` | 「基準値の更新契機は … TradeDecisionMade 購読により実装する」 | **直す**（新しい契機を足す） |
| `MarketMonitorService/Program.cs:25`・`:182` | 「基準値更新のため TradeDecisionMade を購読」 | **直す** |
| `MarketMonitorAppService.cs:104` | 「基準値未確定（前回判断なし）は変動判定しない」 | 正しいまま（判断＝Hold を含むと注記を足す） |
| `TradeDecisionMadeBaselineHandler.cs:31` | `TradeDecisionMade` での更新 | 変えない |
| `DecisionOrchestrator.cs:46`・`DecisionAggregator.cs:39` とそのテスト 3 箇所 | 「Hold は TradeDecisionMade を発行しないため、FR-11 ログが唯一の監査記録」 | **変えない**（除外理由: 新イベントは根拠〔rationale〕を運ばない。根拠の唯一の記録がログである事実は不変） |
| `IADR-0014:71`・`IADR-0106:54,63,124`・`CHANGELOG.md:158` | 凍結記録・生成物 | 変えない（凍結／生成物） |
| テストの `SetBaseline`（MarketMonitorServiceTests・MonitorPollingServiceTests・EfStoreTests） | 前提づくり | 変えない |
| `docs/api/events-and-ports.md`（取引サイクルのイベント表・`BaselinePrice` の注記） | 基準値の契機の記述 | **直す**（行を足し、注記に `TradeDecisionHeld` を足す） |
| `docs/tech/system-architecture.md`（責務表「主な発行イベント」・構成図） | 要約（市場監視の `TradeDecisionMade` 購読も載っていない粒度） | 変えない（除外理由: 「主な」の要約であり、基準値の購読経路を元から載せていない） |
| `deploy/helm/ai-stock-trading/files/pipeline.json` | 変換段の宣言 | 変えない（README が「市場監視のベースライン更新は変換段ではないため宣言しない」と定める） |
| `docs/operations/wolverine-queue-cleanup-runbook.md` のキュー表 | Wolverine 移行時点の旧→新キューの対応（時点の記録） | 変えない（新キューは移行の対象ではない） |

第 2 軸（新イベントの登録点）: 既存の新イベント `ScreeningContextReduced` を `git grep -l` で引き、同じ登録点（契約・識別子固定・後方互換の基準・監査の写像/ハンドラ/標本・発行ポート/NoOp/発行実装・Program.cs・組み立て試験）をすべて本 PR で足した。

## 受け入れ基準

- [x] LLM が Hold と結論した見送りで、判断時点の価格で基準値が進む（取引判断が `TradeDecisionHeld` を出し、市場監視がその価格で基準値を更新する）。
- [x] 判断をしなかった見送り（日報未確定・現在値なし・換算レート未解決・鮮度切れで保有なし）と解析不能では基準値が進まない（イベントを出さない）。
- [x] LLM が Buy/Sell と結論したが統制で見送った場合も基準値が進む（AI 判断は行われた）。
- [x] Hold が続いても、基準値が前回の Hold 時点へ進むため、その後の閾値超過で急変が発火し得る（基準値が無いまま、の状態に留まらない）。
- [x] 既存の Buy/Sell 経路（`TradeDecisionMade` の発行・その価格での基準値更新）は不変で、成立した判断では `TradeDecisionHeld` を出さない。
- [x] 発行の失敗で見送りが壊れない（`DecideAsync` は従来どおり `null` を返し、見送りの理由の計上も 1 件）。
- [x] 価格が得られないときは発行しない。
- [x] 全イベント監査・識別子固定・後方互換の基準ファイルに新イベントが載る。

## テスト方針

- `TradeDecisionService.Tests/Features/TradeDecision/DecideTrade/DecisionHeldReportTests.cs`（新規）: 見送り 13 地点の表を 1 本で固定（判断後は発行・判断前は非発行）、解析不能、価格の優先順、Buy/Sell 成立時の非発行、発行失敗時の見送り継続。
- `MarketMonitorService.Tests/Infrastructure/Steps/TradeDecisionHeldBaselineHandlerTests.cs`（新規）: Wolverine 経由で基準値が更新される／非正の価格は無視／Hold の連続でも急変が発火し得る（ハンドラ → `MarketMonitorAppService` の 1 巡回）。
- 共有契約・監査の既存の完全一致テストへ新イベントを足す（`EventMessageTypeNameTests`・`AuditCycleCompletenessTests` の標本・`event-schemas.baseline.json` は `UPDATE_EVENT_BASELINE=1` で再生成し差分を確認）。
- 配線: `ComposedRealImplementationsTests` 相当で本番の Program.cs が `PublishingDecisionHeldReporter` を解決することを確かめる。
- 変異 3 件以上（判断前の見送りでも発行／Hold で発行しない／ハンドラが基準値を更新しない、など）で赤を確認する。

## 計画書との差異

- 差異: なし（計画どおりに直す実装側の不具合。計画 ADR・要求の変更は無い）。

## 残余

- 現在値の供給が無効な構成（本番 values.yaml の既定 `MarketData__Provider=""`）の**定時判断の Hold**は判断時点の価格を持たないため、基準値を進めない。
  稼働 PoC（values-local は finnhub で有効）では現在値があり、この残余は当たらない。定時判断へ価格を渡す改修は #1035 の範囲。
- 基準値は市場監視自身の相場源ではなく取引判断の価格で進む（`TradeDecisionMade` の既存経路と同じ）。両者の相場源が違う構成では小さなずれが残る。

## 採番

- IADR は #1079（#1035）と番号が衝突したため、後からマージする本 PR が最大＋1 の IADR-0452 へ改番した（2026-09-29）。

## 未決事項

- なし。
