---
title: IADR-0495 最小の名目額（equity の 1%）に満たない新規建てはサイジング側で見送り、判断由来の決済（利確）の後は同じ取引日のうち同じ方向の新規建てを審査で止める
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-04, FR-11, UC-01, UC-02, ADR-0003, ADR-0009, ADR-0018, IADR-0394, IADR-0463, IADR-0471, IADR-0003, IADR-0017, IADR-0130, IADR-0134, IADR-0163, IADR-0246, IADR-0307, IADR-0374, IADR-0452, IADR-0462]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 リスク統制・手仕舞いは止めない・生成 AI は上書きできない)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 リスク統制・取引ガードの既定値。本件の 2 統制の行は無い)
---

# IADR-0495: 最小の名目額と、判断由来の決済の後の同日・同方向の再エントリー禁止（#1176）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-07
- 決定者: オーナー裁定（[#1176](https://github.com/endazon/ai-stock-trading/issues/1176) の 2026-10-07 のコメント「両方採る」）を claude が実装。
  裁定が実装に委ねた点（判定の置き場・LLM の前に省けるか・判断由来の見分け方・不明・部分約定・構成の検証）は本 IADR が決める。

## 起点・関連

- 関連する計画書 ID: **FR-10**（統制）。FR-04（判断）はサイジングと LLM の前の見送りの地点として触る。
- 対象 Issue: [#1176](https://github.com/endazon/ai-stock-trading/issues/1176)
- 関連する実装仕様書: [20261007_1176_min-notional-and-decision-exit-reentry](../specs/20261007_1176_min-notional-and-decision-exit-reentry.md)
- 関連 IADR: [IADR-0394](IADR-0394_stop-out-same-day-reentry-block.md)（損切りの後の同日・同方向。本件の形の手本）、
  [IADR-0463](IADR-0463_entry-blockers-before-llm.md)（新規建ての可否の口・LLM の前の見送り）、
  [IADR-0471](IADR-0471_held-add-on-blockers-prompt-and-hold.md)（保有中の買い増しの可否）、
  [IADR-0003](IADR-0003_position-sizing-responsibility.md) / [IADR-0017](IADR-0017_trade-decision-structure.md)（サイジングは判断サービス・残枠 min）、
  [IADR-0163](IADR-0163_allow-list-and-required-dependency-scope.md) 決定 2（不在が統制の無効を意味しない）、
  [IADR-0307](IADR-0307_end-to-end-latency-instrumentation.md)（サイクルの起点は統制に使わない）、
  [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定 2（拒否理由は末尾へ足す）、
  [IADR-0374](IADR-0374_decision-skip-reasons-and-first-alert-rule.md) / [IADR-0462](IADR-0462_ledger-position-query-status-and-pre-llm-skips.md)（見送りの語彙と台帳）。

## コンテキストと課題

PoC で 3 つの形が観測された（#1176 本文と追加の実例）。

1. NVDA を利確で全量売却した 5 分後に、段階資金の残枠（約 $2.5k）で同じ NVDA を 10 株新規に買った。
2. AMZN を利確で全量売却（970 株 @255.65）した 5 分後に、同じ AMZN を 951 株 @255.63 で買い直した（取得単価が付け替わり、次の +3% の基準が上がる）。
3. AAPL を 13 株 @334.11（equity の約 0.45%）で新規に建てた（段階資金の残枠が約 $4.3〜4.7k まで減っていた）。

損切りの後の同日・同方向の再エントリーの禁止（`StoppedOutSameDay`・IADR-0394）は**判断由来の決済を数えない**（決定 1）。
差金決済防止は米国では現金口座だけに掛かる。最小の名目額は計画にもコードにも無い。オーナーは 2026-10-07 に両方を採り、
「実装は統制（リスク管理の審査またはサイジング）側で行い、方針の文言に頼らない」と裁定した。

## 検討した選択肢

### 最小の名目額の置き場

| 案 | 判定 |
| --- | --- |
| A1. 審査（`RiskEvaluator`）で名目額を見る | **不採用**。しきい値を判断と審査の 2 サービスで持つか、判断側は知らないまま LLM の後に審査で落ちる（承認は消費しないが LLM・通知は消費する）。拒否の通知も増える |
| A2. **サイジング（判断サービス）で見る**。LLM の後の判定に加え、LLM の前に計算できる下界を使う | **採用** |
| A3. 方針（日報のプロンプト）で「小さい建玉は作らない」と書く | **不採用**（裁定「方針の文言に頼らない」） |

### 判断由来の決済の見分け方

| 案 | 判定 |
| --- | --- |
| B1. `OrderApproved.CycleTrigger` が非 null なら判断由来 | **不採用**。IADR-0307 が「統制の判定には一切使わない」と定めた観測の値。意味が変わると統制が黙って外れる |
| B2. 審査のハンドラが台帳へ別に書く | **不採用**。台帳の承認行は DecisionId で冪等（先勝ち）であり、`OrderApprovedLedgerHandler` と競合する |
| B3. **`OrderApproved` に明示の印 `FromTradeDecision` を足し、台帳の由来 `TradeDecision` を書き分ける** | **採用** |

### 判定の形

| 案 | 判定 |
| --- | --- |
| C1. `StopOutProjection` を一般化して 1 つの供給（3 値）に混ぜる | **不採用**。損切りの不明（由来なし）と本件の有無を混ぜ、既存の試験（T-10-770〜）の意味が変わる |
| C2. **同じ形の別の射影（`DecisionExitProjection`）・別の供給・別の拒否理由（`DecisionExitSameDay`）** | **採用**（理由を分けると監査・計器で読み分けられる） |

## 決定

### 決定 1: 最小の名目額はサイジングの後に判定する（統制の本体）

- 新規建て（Open。買い増し・売り増しを含む）のサイジングの結果 **数量 × 参照価格（基準通貨）** が **equity × しきい値** に**満たなければ**見送る
  （`DecisionSkipReason.SizedBelowMinimumNotional`・判断後の見送りとして `TradeDecisionHeld` も出す）。**ちょうど等しいときは通す**（裁定「1% 未満」）。
- しきい値は取引判断サービスの構成 **`Sizing:MinEntryNotionalRatio`**（環境変数 `Sizing__MinEntryNotionalRatio`）。**既定 0.01**
  （`TradingDefaults.MinEntryNotionalRatio`・`TradingDefaultsTests` で固定）。範囲は **0 以上 0.25 以下**（0.25 は 1 注文上限の既定。これを超えると新規建てが
  構造的に成立しない）。**未設定は既定、読めない値・範囲外は構築時に例外＝起動が止まる**（採算ゲートの構成と向きが違うのは、統制の誤設定を黙って既定へ倒すと
  「設定したつもりの値と効いている値が食い違う」まま運用が続くため）。**0 は統制を外す明示の値**。
- `TradeDecisionAppService` は構成を省略可能引数で受けるが、**未指定は既定（1%）で効く**（IADR-0163 決定 2）。
- 判定の関数（`MinimumEntryNotional`）は `PositionSizer` と同じ置き場（リスク管理の Domain。判断サービスが extern alias で読む共有の統制の語彙）に置く。
- 決済（Close）は判定しない（FR-10「手仕舞いは止めない」）。審査は数量を減らさないので、判定した名目額がそのまま発注される。

### 決定 2: LLM の前に分かるときは LLM を呼ばない（費用）

サイジングの株数は LLM の損切り幅（に下限を掛けた幅）で決まるので LLM の後にしか分からない。しかし名目額は
`CalculateCappedQuantity` の金額キャップ **min(1 注文上限, 段階残枠, 日次残枠)** を超えない。したがって
**この上限が equity × しきい値を下回れば、LLM の結論に依らず新規建ては必ず決定 1 で見送られる**。

- 省く条件は IADR-0463 の関門と同じ線引き: **保有が既知で 0・未約定の新規建てが既知で空**（この銘柄では LLM の結論は新規の買い〔必ず見送り〕・
  売り〔裸の新規売りとして必ず見送り〕・Hold しか無い）。加えて **資金・段階残枠・日次残枠がいずれも既知（null でない）**。
- 記録は `TradeDecisionForgoneBeforeLlm(EntryCapacityBelowMinimumNotional)`・`decision_skips{reason="EntryCapacityBelowMinimumNotional"}`。
- 手元の値だけで決まるので、IADR-0463 の照会（ネットワーク）より**先に**置く（両方に当たる銘柄は本理由で記録される）。
- 省かない: 保有中・未約定あり・保有や未約定が不明・資金や残枠が未供給（従来どおり LLM の後に数量 0 で見送る）・価格に依存して最小を割る場合（決定 1 が止める）。
  **これらの経路では LLM の費用は節約されない**（株数が LLM の後にしか決まらないため。決定 1 の統制は効く）。
- 🔴 **計器の読み方が変わる**: 保有 0 の銘柄で残枠を使い切った状態（残枠 0）は、従来 LLM の後の `SizingZeroQuantity` だったが、LLM の前の
  `EntryCapacityBelowMinimumNotional` へ移る（LLM を呼ばなくなる）。

### 決定 3: 判断由来の決済の後は、同じ取引日のうち同じ方向の新規建てを止める

`StoppedOutSameDay`（IADR-0394）と同じ形・同じ置き場・同じ入力で、数える由来だけが違う。

| 手仕舞いの経路 | 承認行の由来 | 本統制で数えるか |
| --- | --- | --- |
| 判断由来の決済（審査が `TradeDecisionMade` の Close を承認） | **`TradeDecision`**（決定 4） | **数える**（利益・損失を問わない。承認または約定が当日） |
| 損切り（S1 の発動・S0 の約定） | `SoftwareStopS1` / `ProtectiveStopS0` | 数えない（`StoppedOutSameDay` が別の理由で止める） |
| 保護喪失の成行手仕舞い | `ProtectionLostClose` | 数えない |
| owner の手仕舞い・維持率の自動縮小 | `OrderApproved` | 数えない（人の裁量・口座全体の事情。裁定は「判断由来」） |
| 由来が記録されていない決済 | `null` | 数えない（`StopOutStatusUnknown` が既に同じ方向を止める＝同じ行を 2 つの理由で数えない） |

- **方向**は建玉の方向（売りの決済＝ロングを閉じた → 買いの新規建てを止める）。反対方向は止めない（裁定「同じ方向」）。
- **区切り**は市場の現地取引日（`TradingDay.Of`。米国株は米国東部の暦日・夏時間は `TimeZoneInfo` が吸収、日本株は JST）。
- **承認だけで数える**（約定を待たない。S1 と同じ）。判断の決済は承認から約定まで数秒だが、約定が台帳へ届く前に次の判断の審査が来得る。
  承認されたが約定しなかった決済でも、その日の同じ方向（実質は買い増し）は止まる —— 判断が手仕舞うと決めた日に同じ方向へ積み増さない（保守側）。
  承認が前日でも約定が当日なら数える。**部分約定も同じ扱い**（判断の決済は保有の全量で出るので、部分的な決済は約定の途中にしか生じない）。
- 拒否理由 **`RejectionReason.DecisionExitSameDay`（序数 32・クラス A）**。審査（`RiskEvaluator`）では `StoppedOutSameDay` の直後に評価する。
- 新規建ての可否の口（`EntryStateBlockers.Determine`。IADR-0463）に同じ述語を同じ位置で足す（8 理由）。gRPC `EntryBlocker` に
  `ENTRY_BLOCKER_DECISION_EXIT_SAME_DAY = 8`（非破壊・baseline 更新）。判断側のプロンプトの日本語名は「本日この方向で判断による手仕舞い（利確など）済み」。
  したがって保有 0・未約定なしの銘柄では LLM を呼ぶ前に `EntryBlockedByRiskControls` で見送られ（LLM 費用を消費しない）、保有中の買い増しは
  IADR-0471 の経路で選ばせない。**審査は残す**（両端で止める）。
- 台帳の決済の読み取りは審査・口とも 1 回にし、損切りと本件の 2 つの射影へ渡す（新規建てだけ読む規律は不変）。

### 決定 4: 判断由来の印を契約に持たせ、台帳の由来を書き分ける

- `OrderApproved` の末尾に **`bool FromTradeDecision = false`**。**true にするのは審査（`OrderScreeningService`）だけ**。owner の手仕舞い・
  自動縮小・発注執行の内部の構築は既定のまま。本項目を持たない旧いメッセージは false として読まれる。
- `ApprovalSource` の末尾に **`TradeDecision = 4`**。`OrderApprovedLedgerHandler` は印で `TradeDecision` / `OrderApproved` を書き分ける。
  DB のマイグレーションは要らない（`approved_orders.Source` は int で、値の追加だけ）。
- 入力は EF の台帳（PostgreSQL）であり、**再起動で消えない**（`StoppedOutSameDay` と同じ）。

## 結果

- 良い影響: 3 つの観測の形がいずれも統制で止まる（AAPL は LLM の前、AMZN・NVDA の買い直しは LLM の前の可否の口または審査）。理由が名前で
  計器・監査・通知に出る。
- 悪い影響・トレードオフ:
  - **配備当日の取りこぼし（fail-open・1 取引日だけ）**: 配備前に書かれた当日の判断由来の決済は由来が `OrderApproved` で、owner の手仕舞いと
    区別できないので数えない。翌取引日から効く。配備はセッションの外で行う。
  - **しきい値は equity 比の 1 値**（市場・段階で分けない）。日本株（単元 100 株）ではほぼ当たらない。
  - **Stage 0 の記録（`Stage0DecisionRecorder`）・バックテストは本件の 2 統制を再現しない**。Stage 0 は AI の判断の記録であり、本番の統制
    （審査・損切りの再エントリー禁止）を全部は再現していない。名目額の床だけを足すと記録の指紋が変わり、比較の一貫性が崩れる。必要になれば別 issue で
    審査系の統制とまとめて扱う。
  - 判断由来の決済の後の禁止は「利確」に限らず判断の手仕舞い全体（損失での手仕舞いを含む）に掛かる（裁定の括り）。
- フォローアップ:
  - 計画（FR-10 / 05_trading-assumptions §5）に本件の 2 統制が載っていない（損切りの後の禁止〔IADR-0394〕も同じ）。計画側への記録は planning への
    issue で求める（PR 本文に下書き）。

## 関連

- [#1176](https://github.com/endazon/ai-stock-trading/issues/1176)（裁定）
- 実装（`backend/` 配下）: `Services/RiskManagementService/Domain/{MinimumEntryNotional,DecisionExitReentrySupply,EntryStateBlockers,RiskEvaluator,TradingDefaults}.cs`・
  `Services/RiskManagementService/Features/RiskManagement/{DecisionExitProjection,ApprovalSource,OrderScreeningService,RiskReadWireMapping}.cs`・
  `GetEntryBlockers/EntryBlockersService.cs`・`Infrastructure/Steps/OrderApprovedLedgerHandler.cs`・
  `Services/TradeDecisionService/Features/TradeDecision/{MinimumEntryNotionalOptions,DecideTrade/TradeDecisionAppService,DecideTrade/TradeDecisionPromptBuilder}.cs`・
  `Infrastructure/ExternalServices/{MinimumEntryNotionalOptionsLoader,GrpcEntryBlockersProvider}.cs`・`Program.cs`・
  `Shared/AiStockTrading.Shared.Contracts/{Events/OrderApproved,Events/TradeDecisionForgoneBeforeLlm,Observability/DecisionSkipReason,Trading/RejectionReason}.cs`・
  `Shared/AiStockTrading.Shared.Grpc/Protos/aistocktrading/riskmanagement/v1/risk_controls_read.proto`
- テスト: T-10-2310〜T-10-2322（`docs/tests/FR-10_risk-controls-tests.md`）

## ［2026-10-07 追記 / #1174］1 株に届かない残枠も LLM の前へ移した

決定 2 の「省かない: …価格に依存して最小を割る場合」「これらの経路では LLM の費用は節約されない」のうち、**段階残枠と日次残枠の小さい方が現在値 × 1 株（基準通貨）に
満たない場合**は、[IADR-0500](IADR-0500_pre-llm-skip-when-capacity-below-one-share.md) が LLM の前の見送り `EntryCapacityBelowOneShare` へ移した（サイジングは現在値を
そのまま参照価格に使うので、数量 0 が LLM の前に確定する）。線引き（保有 0・未約定なし・残枠が既知）は本決定と同じで、加えて現在値が既知であることを要する。
**本決定の判定を先に評価する**（残枠が最小の名目額にも届かない銘柄は従来どおり `EntryCapacityBelowMinimumNotional`）。株数の端数で最小を割る場合（1 株以上は買える）は
従来どおり LLM の後の `SizedBelowMinimumNotional`。本文は書き換えない。
