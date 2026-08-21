---
title: 作業仕様書 — クラス C 統制違反件数を発注審査の観測から集計し、「未供給」と「0 件」を区別する
type: work
status: review
related_ids: [FR-20, FR-11, FR-10, FR-12, UC-06, SC-03, ADR-0008, ADR-0016, IADR-0137, IADR-0142, IADR-0148]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - planning:projects/ai-stock-trading/INDEX.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
related_specs:
  - ../adr/IADR-0148_control-violation-supply-and-unavailable-state.md
  - ../adr/IADR-0137_stage1-trading-day-counting.md
  - ../adr/IADR-0142_stage1-simulate-only-aggregation.md
  - ../../docs/functional/FR-20_staged-gates.md
  - ../../docs/tests/FR-20_staged-gates-tests.md
  - 20260804_333_stage-gate.md
  - 20260805_334_broker-provider-axis.md
---

# 作業仕様書: クラス C 統制違反件数の供給（[#387](https://github.com/endazon/ai-stock-trading/issues/387)）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-20**（段階ゲート・Stage 1 の合格判定は `SIMULATE` のみで集計）／ **FR-11**（拒否理由の記録）
- ユースケース（UC）: **UC-06**（段階の参照・承認）
- 画面（SC）: **SC-03**（未充足基準の表示）
- 関連 ADR: **ADR-0008**（段階ゲート）／ **ADR-0016** 決定10（拒否理由のクラス分類）
- 実装 ADR: [IADR-0148](../adr/IADR-0148_control-violation-supply-and-unavailable-state.md)（本作業の決定）／
  [IADR-0137](../adr/IADR-0137_stage1-trading-day-counting.md)（観測入力の系譜）／
  [IADR-0142](../adr/IADR-0142_stage1-simulate-only-aggregation.md)（観測は発注先を必須で伴う）
- 計画書リンク: 06_daytrading-review §4.1 条件1（計画リポ）／
  02_requirements FR-20（計画リポ）

## 目的・背景

計画 §4.1 条件1 は Stage 1 → Stage 2 の合格条件として「**統制違反 0 件**（クラス C 限定）」を定める。
[#333](https://github.com/endazon/ai-stock-trading/issues/333)（PR #384）は判定側を実装したが、
**件数を供給する経路が無い**。`StagePerformance.ControlViolationCount` は `public int`（非 nullable・既定 0）であり、
`StageGate` は `count > 0` で未充足を判定する。供給が無い＝常に 0 ＝**条件1 は常に「充足」**である。

**この 0 は段階ゲートで唯一 fail-safe でない既定である。** 営業日数（[#385](https://github.com/endazon/ai-stock-trading/issues/385)）・
取引件数（[#386](https://github.com/endazon/ai-stock-trading/issues/386)）の 0 は「条件未充足＝昇格しない」に倒れるが、
違反件数の 0 は「違反が無い＝条件充足」を意味する。現在は他の 2 条件が 0 で止めているだけであり、
**#385 / #386 が供給を実装した瞬間、この 0 が「無条件で条件1 を通す」ようになる**。

本作業の実質は集計の実装そのものではなく、**「供給が無い」と「違反 0 件」を型と判定の上で区別し、
未供給を条件未充足として扱うこと**である。

## 対象範囲

- 対象:
  - 「未供給」を表現する第一級の値（`ControlViolationTally?`）と、未供給を未充足として扱う判定
  - 発注審査（`OrderScreeningService`）の結果を観測として記録し、クラス C の拒否件数を集計する経路
  - 観測窓を段階遷移で区切る（計画「集計期間は Stage 1 の全期間」）
  - 永続化（新テーブル＋マイグレーション）と、死んだ列（`stage_performance.ControlViolationCount`）の削除
  - 契約フィクスチャ・フロントの未充足基準ラベルの追随
- 対象外:
  - 営業日数・取引件数の供給（#385 / #386）
  - 段階の自動昇格（**設計上すべて利用者承認を要する**。本作業でも経路を作らない）
  - `LiveTradingGate.LiveTradingReleased` の閂（触れない）

## 設計

### 1. 「未供給」を表す型（[IADR-0148](../adr/IADR-0148_control-violation-supply-and-unavailable-state.md) 決定1・決定2）

- `ControlViolationTally(int Count)` を導入する。**この型の値が存在すること自体が「集計が供給された」ことを意味する。**
- `StagePerformance.ControlViolationCount`（`int`）を**削除**し、判定へは `ControlViolationTally?` を
  `StageGate.AssessPromotion` / `RequestTransition` の**必須引数**として渡す（既定値を与えない）。
- `null` ＝未供給 → 新しい未充足理由 `StageGateCriterion.ControlViolationCountUnavailable = 12` を列挙する。
  `ControlViolationsPresent`（＝供給済みで 1 件以上）とは別の値にする——監査で
  「集計が来ていない」と「違反があった」を取り違えないため（[IADR-0137](../adr/IADR-0137_stage1-trading-day-counting.md) 決定3 と同じ規律）。

### 2. 集計（純関数）

- 観測 `OrderScreeningObservation(DecisionId, Provider, RejectionReasons)`。**発注先は必須**（IADR-0142 決定1 の踏襲）。
- `ControlViolationAggregation`
  - `IsCounted(provider)` は `Stage1DayQualification.CountedProvider`（`MoomooSimulate`）の**許可制**（IADR-0142 決定2 の再利用）
  - `CountsAsViolation(observation)` ＝ 算入対象の発注先 ∧ `RejectionReasonClassification.CountsAsControlViolation(reasons)`
    （**分類は再実装しない**。単一情報源は `RejectionReasonClassification`）
  - `Tally(observations)` ＝ 算入対象の観測が **1 件も無ければ `null`（未供給）**、あれば `Observed(クラス C を含む拒否の件数)`
- **計上単位は 1 回の発注審査（＝ `DecisionId`）につき 1 件**。1 回の拒否に複数のクラス C 理由が返っても 1 件である
  （`RejectionReasonClassification.CountsAsControlViolation(IEnumerable<RejectionReason>)` が既にこの単位を持つ）。

### 3. 供給（結線）

```
TradeDecisionMade → OrderScreeningService.Screen（承認/拒否を決める。ここで観測を作る）
                  → ScreeningOutcome.Observation（承認でも拒否でも必ず伴う）
                  → TradeDecisionMadeHandler が IControlViolationObservationStore.Record（DecisionId で冪等）
StageGateService.CurrentTally() → 観測ログから ControlViolationTally? を得て判定へ渡す
```

- **承認された審査も記録する。** 記録するのが違反だけだと「違反 0 件」を主張できる根拠が無く、
  未供給と区別できない。**算入対象の発注先での審査が 1 件でもあれば「集計は供給されている」**とし、
  そのうちクラス C を含む拒否の件数を数える。
- **観測窓は受理された段階遷移で区切る**（`StageGateService.RequestTransition`）。計画は「集計期間は Stage 1 の全期間」と
  定めるため、Stage 1 へ入った時点より前の観測を数えてはならない。遷移直後は観測が無い＝未供給＝
  **昇格しない**（fail-safe）。

### 4. 永続化

- 新テーブル `order_screening_observations`（`DecisionId` 主キー＝冪等・1 審査 1 行）。
  列は `ObservedAtUtc` / `Provider` / `CountsTowardStage1` / `IsControlViolation`。
  真偽 2 列は**記録時に純関数が決めた結果**であり、SQL 側に分類規則を書かない。
- `stage_performance.ControlViolationCount` 列は**削除**する（供給元が別テーブルになり、この列は死ぬ。
  死んだ列を残すと「まだ使う値」に見え、次の実装者が判定へ結線し直す余地が残る＝IADR-0137 決定2 と同じ規律）。
- マイグレーション `AddOrderScreeningObservations`。`Up` / `Down` を**実 PostgreSQL・既存データ 1 行**で確認する。

## 受け入れ基準

計画（06_daytrading-review §4.1 条件1・02_requirements FR-20 の受け入れ基準）および #387 より。

- [ ] クラス C の拒否を含む発注拒否が **1 件**として計上される
- [ ] **否定形**: 1 回の拒否で複数のクラス C 理由が返っても **1 件**である
- [ ] **否定形**: クラス A / クラス B の拒否が積み上がっても件数が増えない
- [ ] **供給が無い状態と「違反 0 件」を区別できる。未供給は条件未充足として扱い、昇格させない**
- [ ] **否定形**: #385 / #386 の供給（60 営業日・100 件）が揃っても、統制違反の集計が未供給なら昇格しない
- [ ] **否定形**: 内蔵 `paper` の審査は件数にも「供給された」判定にも寄与しない（FR-20・IADR-0142）
- [ ] 同一 `DecisionId` の再記録（再送）で件数が二重計上されない
- [ ] 段階遷移が受理されると観測窓が区切られ、直後は未供給（＝昇格しない）に戻る

## テスト方針

| 観点 | 層 | 種別 |
| --- | --- | --- |
| 未供給が昇格を止める／`Observed(0)` は止めない | Domain（`StageGateTests`） | 正・否定形 |
| 期間・件数が揃っていても未供給なら昇格しない（**本 issue の核心**） | Domain | 否定形 |
| 1 回の拒否＝1 件（複数クラス C 理由・重複 `DecisionId`） | Domain（集計）・Infrastructure（EF 冪等） | 否定形 |
| クラス A / B の積み上げで件数が増えない | Domain | 否定形 |
| 内蔵 `paper` / `MoomooReal` は算入も供給判定もしない | Domain | 否定形 |
| 審査結果が観測として記録される（承認・拒否とも） | Application（`OrderScreeningService`） | 正 |
| 段階遷移で観測窓が区切られる | Application（`StageGateService`） | 正・否定形 |
| 実応答（`/risk-controls/stage-gate`）の未充足基準に 12 が現れる | Api（契約フィクスチャ）・frontend | 正 |

## 計画書との差異

- 差異: **なし**（計画 §4.1 条件1・FR-20 の「`SIMULATE` のみで集計」に従う）。
  ただし FR-20 の受け入れ基準「同一期間を `paper` で稼働させても 3 指標がいずれも増えない」に従い、
  **内蔵 `paper` で発生したクラス C の拒否は件数に計上しない**。これは「AI が禁止事項を犯そうとした」
  事実を昇格判定に反映しない向きであり、統制としては緩い側である。計画の明示的な裁定であるため実装は従うが、
  残余リスクとして [IADR-0148](../adr/IADR-0148_control-violation-supply-and-unavailable-state.md) に記録する
  （`paper` 稼働そのものが「供給あり」を作らないため、`paper` だけで昇格することはない）。

## 未決事項

- 観測ログの保持期間（retention）。計画に記述が無いため上限を設けない。1 審査 1 行・計画の想定件数
  （100 件 / 60 営業日）では実運用で問題にならない規模である。
