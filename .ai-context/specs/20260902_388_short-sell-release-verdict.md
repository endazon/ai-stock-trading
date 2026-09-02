---
title: 空売り実弾解禁の verdict（Stage 0 再充足の判定入力）の型と判定ロジック
type: spec
status: draft
related_ids: [FR-20, FR-15, FR-11, UC-06, ADR-0016, ADR-0008, IADR-0281]
author: endazon (with Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: 空売り実弾解禁の verdict の型と判定ロジック

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-20（段階ゲート）、FR-15（バックテスト）、FR-11（監査ログ）
- ユースケース（UC）: UC-06（段階遷移の承認）
- 画面（SC）: SC-03（統制状態の参照。verdict の状態はここから読める）
- 関連 ADR: ADR-0016 決定 8・決定 14（2026-08-07 確定「verdict の形式」）、ADR-0008
- 計画書リンク: `project-planning` の `projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md`
- 実装 issue: #388（環流 planning#222）

## 目的・背景

ADR-0016 決定 14 は 2026-08-07 に **verdict（実弾解禁前の確認が「済んだ」という判定）の形式**を確定した。

| 項目 | 確定値 |
| --- | --- |
| 記録の主体と場所 | **利用者承認**とし、**段階ゲートの承認記録（FR-20 / UC-06）と同じ経路**に載せる。別記録にしない |
| 有効期限 | **30 日**。期限を過ぎた verdict では解禁できない |
| 再検証の契機（無効化） | **① 情報源の変更**（借株料の照会経路・維持率の供給）／**② 戦略の変更**／**③ 期限切れ** |

実装側にはこの「済んだ」を表現する型が無い。現状は `StageProductPolicy.StageReleaseContext(bool
ShortSellStrategyBacktestPassed)` の 1 個の真偽値だけであり、**発行時刻も情報源も戦略も持たない**ため、
30 日期限も無効化契機も判定しようがない（供給元も未実装で常に `null` ＝フェイルクローズ）。

本作業は **型と判定ロジックを先行実装**する。実地観測（一次ゲート `IsShortPermit` を実弾で確認する）は
`ShortFeeRate` の単位確定（ADR-0026 PoC 項目 9・#342 項目 9）待ちであり、本作業の範囲外である。

## 対象範囲

- 対象:
  - verdict の型（発行時刻・情報源フィンガープリント・戦略識別子・承認者・承認記録 ID）
  - 30 日期限・情報源の変更・戦略の変更を判定する純関数
  - verdict を**段階ゲートの承認記録（`stage_transitions` 台帳・`POST /stage-gate/transition`）へ相乗り**させる
  - `BacktestEvaluated` に「空売りを含む戦略か」と戦略識別子を足し、Backtest 側 factory と Risk 側射影を追随
  - `StageProductPolicy.Evaluate` が verdict の有効性を AND 条件に組み込む
- 対象外:
  - 発注審査（`OrderScreeningService`）への `StageReleaseContext` の実供給。**現状 `null`（フェイルクローズ）のまま**
    据え置く。借株照会・維持率の供給元（#417 / #419）が未実装であり、供給できる材料が無い
  - 実地観測（一次ゲート `IsShortPermit` の実弾確認）。#342 項目 9 の `ShortFeeRate` 単位確定待ち
  - 新しい拒否理由の追加（既存 `RejectionReason.StageShortSellReleaseUnmet` で表現する）

## 設計

### 1. verdict は段階ゲートの承認記録に相乗りする（別テーブル・別 API を作らない）

裁定は「別記録にしない」と明示する。よって既存資産をそのまま使う。

| 要素 | 相乗り先 |
| --- | --- |
| 台帳 | `stage_transitions`（追記専用・`StageGateLedger` の畳み込み）。**新テーブルを作らない** |
| API | `POST /risk-controls/stage-gate/transition`（OwnerOnly）。**新エンドポイントを作らない** |
| 承認者 | `StageTransitionRow.ApprovedBy`（認証済み利用者名） |
| 発行時刻 | `StageTransitionRow.OccurredAtUtc` |
| 承認記録 ID | `StageTransitionRow.Sequence`（台帳の連番） |
| 監査 | 受理時に `StageTransitioned` を発行（既存経路・`Kind` 文字列が種別を運ぶ） |

**承認種別を 1 個増やす**（`StageTransitionKind.ShortSellReleaseVerdict`）。verdict の行は
`FromStage == ToStage == 現段階`であり、台帳の畳み込み（`CurrentStage = History[^1].ToStage`）を動かさない。

要求は `POST /stage-gate/transition` の本文へ `approval` を足して分岐する（省略＝従来どおり段階遷移）。

```json
{ "approval": 1 }        // 1 = 空売り実弾解禁の verdict（targetStage は指定しない）
{ "targetStage": 2 }     // 従来どおりの段階遷移（後方互換）
```

### 2. 情報源フィンガープリント（「情報源の変更」を機械的に判定する識別子）

対象は裁定が名指しした 2 つ——**借株料の照会経路**と**維持率の供給**である。

- 各供給アダプタは目印インターフェース `IShortSellReleaseSource`（`Kind` と `SourceId`）を実装して DI へ登録する。
- `ShortSellReleaseSourceInventory` が**登録アダプタ名を列挙**し、純関数
  `ShortSellReleaseSources.Fingerprint` が正規化（trim・空除去・重複除去・序数順ソート）して
  `borrow=<ids>;margin=<ids>` の文字列を作る。**未登録は `none`**。
- **今日は両方とも未登録であり、フィンガープリントは `borrow=none;margin=none` である。** これは正しい表現であり、
  #417 / #419 が供給を結線した瞬間に文字列が変わって**既存 verdict が自動で無効化される**（裁定 ①）。

**ハッシュにしない。** 監査で「何が変わって無効になったか」が読めることに実益があり、値は短い。

### 3. 戦略識別子（「戦略の変更」を機械的に判定する識別子）

**バックテストの verdict が名乗る戦略 ID** を使う（`BacktestEvaluated.StrategyId`）。Risk はこれを段階別実績
（`StagePerformance.BacktestStrategyId`）へ射影し、verdict 発行時に写し取る。評価時に一致しなければ無効（裁定 ②）。

あわせて `BacktestEvaluated.IncludesShortSelling` を足す。決定 14 の「**空売りを含む戦略で** Stage 0 の
7 条件を再度満たす」は、**空売りを含まない戦略の合格では満たされない**——これは戦略識別子の一致とは別の条件であり、
`ShortSellStrategyBacktestPassed = Passed && IncludesShortSelling` として AND の別項に据える。

### 4. 判定の純関数（Domain）

```text
ShortSellReleasePolicy.Evaluate(verdict, currentSourceFingerprint, currentStrategyId, now)
  verdict is null                                  -> Missing        （fail-closed）
  経過 < 0 または 経過 > 30 日                      -> Expired        （30 日ちょうどは有効）
  verdict.SourceFingerprint != current             -> SourceChanged
  verdict.StrategyId != current（空文字も不一致扱い）-> StrategyChanged
  それ以外                                          -> Valid
```

`StageProductPolicy.StageReleaseContext` を
`(ShortSellStrategyBacktestPassed, Verdict, CurrentSourceFingerprint, CurrentStrategyId, EvaluatedAtUtc)`
へ拡張し、Stage 3 の空売り解禁を **equity ≥ $5,000 ∧ ShortSellStrategyBacktestPassed ∧ verdict が Valid** の
AND にする。**既定値は与えない**——構築点はすべて明示的に材料を渡すことを強制する（渡し忘れを型で止める）。

### 5. 拒否理由は増やさない

issue #388 の項目 4 は「`StageShortSellReleaseUnmet` で表現できるか、監査ログで区別できるか確認」を求める。
**区別は拒否理由では付けない**（序数安定性テストがある enum を細分すると、クラス分類・表示ラベル・
HTTP 往来の対応をすべて動かすことになる）。**区別は `GET /stage-gate` の `ShortSellRelease` が担う**——
状態（Missing / Expired / SourceChanged / StrategyChanged / Valid）・現在のフィンガープリント・戦略 ID・
失効時刻を返す。verdict そのものは追記専用台帳に載っており、拒否時刻の前後で台帳を引けば理由が確定する。

## 受け入れ基準

- [ ] equity $5,000 を満たしても verdict が無ければ解禁されない（fail-closed・最重要）
- [ ] 空売りを含まない戦略の Stage 0 合格では解禁されない
- [ ] 供給（`StageReleaseContext`）の欠落は従来どおりフェイルクローズのまま
- [ ] 31 日前の verdict では解禁できない／**30 日ちょうどでは解禁できる**（境界）
- [ ] 情報源を変更した直後は、期限内の verdict でも解禁できない
- [ ] 戦略を変更した直後も同様である
- [ ] verdict が段階ゲートの承認記録と同じ経路に載っている（別テーブル・別 API を作っていない）
- [ ] 承認者が空なら verdict は記録されない（承認なしに verdict が生じない）

## テスト方針

| 観点 | テスト |
| --- | --- |
| 否定形（4 本） | verdict 欠落／空売りを含まない戦略の合格／供給欠落／承認者が空 |
| 境界 | 30 日ちょうど＝有効・30 日＋1 tick／31 日＝無効 |
| 無効化契機 | 情報源の変更（フィンガープリント不一致）／戦略の変更（戦略 ID 不一致） |
| 相乗りの構造テスト | `RiskManagementDbContext` の `DbSet` 列挙に verdict 専用テーブルが無い／Risk のエンドポイント列挙に verdict 専用ルートが無い |
| フィンガープリント | 未登録＝`none`／登録順・重複に依存しない（正規化）／登録が増えると値が変わる |
| 台帳 | verdict 行が現在段階を動かさない／連番が進む／`StageTransitioned` が発行される |

テスト名・コメントに `FR-20` / `ADR-0016 決定14` / `#388` を残す。

## 計画書との差異

- 差異: なし（決定 14 の 2026-08-07 確定をそのまま実装する）

## 未決事項

- **実地観測**（一次ゲート `IsShortPermit` が実弾で機能することの確認）は `ShortFeeRate` の単位確定
  （ADR-0026 PoC 項目 9・#342 項目 9）待ちであり、本作業では満たせない。#388 はクローズしない。
- 借株照会・維持率の供給アダプタ（#417 / #419）が `IShortSellReleaseSource` を実装して登録するまで、
  フィンガープリントは `borrow=none;margin=none` のままである（＝その時点で verdict は自動失効する）。
