---
title: IADR-0003 ポジションサイジングは取引判断サービスが行い、RiskEvaluator は検証のみとする
type: impl-adr
status: Accepted
related_ids: [FR-10, UC-01, UC-02, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-08
updated: 2026-07-09
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# IADR-0003: ポジションサイジングは取引判断サービスが行い、RiskEvaluator は検証のみとする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: endazon（利用者）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制）、UC-01/UC-02（取引サイクル）、ADR-0003（AI 判断のガードレール）
- 関連する実装仕様書: [20260708_risk-guard-core](../specs/20260708_risk-guard-core.md)
- 対象コード: [`PositionSizer.cs`](../../backend/Services/RiskManagementService/src/RiskManagementService.Domain/PositionSizer.cs)、
  [`RiskEvaluator.cs`](../../backend/Services/RiskManagementService/src/RiskManagementService.Domain/RiskEvaluator.cs)

## コンテキストと課題

`PositionSizer`（1 取引リスク・連敗/DD 連動の縮小によるサイジング）と `RiskEvaluator`（発注前の決定的な
上限判定）はどちらもリスク管理サービスのドメインに存在する。しかし `RiskEvaluator.Evaluate` は承認時に
`intent.Quantity` をそのまま承認数量として返しており、`PositionSizer` を内部で呼び出していない。
両者の結線責務（誰がサイジングを実行し、いつ数量が確定するか）が未定義だと、後続スライスで
「サイジング未適用のまま発注される」結合漏れが起こり得る。

## 検討した選択肢

1. **`RiskEvaluator` がサイジングも兼ねる** — 判定（reject 可否）と数量算出が 1 メソッドに混在し、
   「決定的な検証コア」という責務が肥大化する。生成 AI の意図数量を検証する軸と、リスク予算から
   数量を導く軸が分離できない
2. **取引判断サービス（呼び出し元）が発注意図の確定前に `PositionSizer` で数量を決め、`RiskEvaluator` は
   確定済み `OrderIntent` を検証するだけにする** — 責務が明確（サイジング=判断側、上限検証=リスク側）で、
   ADR-0003 の「AI はガードレールを上書きできない」を、検証コアを純粋関数に保つことで担保しやすい

## 決定

選択肢 2 を採用する。

- **サイジングの実行責務は取引判断サービス（後続スライスで実装、未実装）**が持つ。発注意図を組み立てる
  段階で `PositionSizer.CalculateQuantity` / `GetSizeFactor` を用いて数量を確定し、`OrderIntent.Quantity`
  に反映する。
- **`RiskEvaluator` は確定済みの `OrderIntent` を検証するだけ**とし、サイジングは行わない。承認時は
  `intent.Quantity` をそのまま承認数量として返す（現状の挙動を維持）。
- 仕様書（`20260708_risk-guard-core.md`）の受け入れ基準「3〜5 連敗でサイズ半減が適用される」は、
  呼び出し元がサイジング済みの数量で発注意図を作る前提で満たす。本スライスでは `PositionSizer` 単体の
  決定性を `PositionSizerTests` で固定し、結線は取引判断サービスのスライスで検証する。

## 理由

- 検証コアを純粋関数（入力 = 確定意図、出力 = 可否＋理由）に保つと、テスト容易性と監査性が高い（FR-11）
- サイジングは市況・損切り幅（ATR）といった判断側の入力に依存するため、判断サービスに置くのが自然

## 結果

- 良い影響: 責務境界が明確になり、後続スライスでの結合漏れ（サイジング未適用発注）を設計時に防げる
- 悪い影響・トレードオフ: サイジング未適用の `OrderIntent` を渡しても `RiskEvaluator` は検知しない
  （上限内なら承認される）。取引判断サービスのスライスで「発注意図は必ず `PositionSizer` を経由する」
  ことを結合テストで担保する必要がある
- フォローアップ: 取引判断サービス実装時に、サイジング→発注意図→`RiskEvaluator` 検証の結合テストを追加する

## 追記（2026-07-09, Issue #29）: 金額上限とのキャップは呼び出し側がサイジング時に行う

`PositionSizer.CalculateQuantity` はリスク予算（資金 × 1 取引リスク × 縮小係数）÷ 損切り幅のみで株数を返すため、
損切り幅が浅い場合は想定金額が 1 注文金額上限・利用可能資金を系統的に超過し、`RiskEvaluator` で必ず拒否される
（サイジング→拒否のループ。取引機会の空振りと監査ログのノイズ。Issue #29）。

本 ADR の責務分担（サイジング＝判断側、上限検証＝リスク側）を維持したうえで、**金額上限との突き合わせは
呼び出し側がサイジング時に行う**ことを確定する。そのための primitive として
`PositionSizer.CalculateCappedQuantity(..., referencePrice, maxOrderAmount, availableCapital, sizeFactor)` を追加した。
これはリスク予算基準の株数と、金額上限（1 注文金額上限・利用可能資金の小さい方）を参照価格で割った株数の
小さい方を返す。参照価格が正でない場合は 0（見送り）。

- `RiskEvaluator` は引き続き確定済み `OrderIntent` の検証のみを行い、サイジング・キャップは行わない（責務不変）。
- 取引判断サービスは発注意図の数量確定に `CalculateCappedQuantity` を用い、想定金額が常に上限内に収まることを保証する。
- `availableCapital` には段階資金上限の残枠（`CapitalCap - InvestedCapital`。IADR-0005）等を渡すことを想定する。
- **フォローアップ（取引判断サービス結線スライス）**: `RiskEvaluator` は 1 日発注金額上限（`MaxDailyOrderAmount`）でも
  エントリーを系統的に拒否し得る。`availableCapital` の算出に日次発注残枠（`MaxDailyOrderAmount - DailyOrderedAmount`）も
  含めるか（＝サイジング時点で日次上限も見込むか）を結線スライスで確定する。本 primitive は上限値を引数で受けるため、
  呼び出し側が min を取る対象に日次残枠を加えるだけで対応できる。

## 関連

- Supersedes: なし
- Superseded by: なし
