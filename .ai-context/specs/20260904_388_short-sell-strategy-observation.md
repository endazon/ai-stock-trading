---
title: 空売り実弾解禁の判定入力を「宣言」から「観測」へ移し、供給経路の残欠を実測して記録する
issue: "#388"
plan_refs:
  - FR-15
  - FR-20
  - ADR-0016
adr_refs:
  - IADR-0304
status: done
created: 2026-09-04
---

# 作業仕様書: 空売り実弾解禁の判定入力を「宣言」から「観測」へ移す（#388）

## 背景

### 着手前の実測 —— #388 の主要部分は既にマージ済みである

本作業の着手時、#388 の「やること」3 点のうち **2 点は PR [#641](https://github.com/endazon/ai-stock-trading/pull/641)
（IADR-0281）で実装済みであり `develop` に載っている**。着手前に現物を読んで実測した結果を残す。

| #388 のやること | 実測した現状 | 実装箇所 |
| --- | --- | --- |
| `BacktestEvaluated` に「空売りを含む戦略か」を持たせる | **実装済み** —— `IncludesShortSelling` / `StrategyId` の 2 項を末尾へ追加済み | `backend/Shared/AiStockTrading.Shared.Contracts/Events/BacktestEvaluated.cs` |
| verdict を `StageReleaseContext` へ供給する | **実装済み** —— イベント射影（#164 と同型）で `StagePerformance` へ落ち、`StageGateService.CurrentShortSellRelease()` が文脈を組む | `Infrastructure/Steps/BacktestEvaluatedProjectionHandler.cs` / `Features/RiskManagement/StageGateService.cs` |
| verdict の形式が計画に無ければ環流する | **不要** —— 形式（30 日・3 契機・承認記録へ相乗り）は 2026-08-07 に計画側で確定済み（ADR-0016 決定 14） | — |

受け入れ基準の否定形 2 本も既にテストで固定されている。

- `空売りを含む戦略のStage0再充足が無ければequityが足りても解禁されない`（`StageProductPolicyTests`）
- `空売りを含まない戦略のStage0合格では解禁されない`（`ShortSellReleaseVerdictTests`）

**したがって本作業は #388 の再実装ではない。** 残っていた穴を 1 つ塞ぎ、残欠を実測して記録する。

### 残っていた穴 —— 「空売りを含む戦略か」は誰も判定していない

`BacktestEvaluatedFactory.From(..., bool includesShortSelling, ...)` は **呼び出し元が渡す真偽値**である。
XML コメントは「Stage0Decision は取引可能な商品種別を保持しないため、呼び出し元（発行ホスト）が渡す」と
述べるが、**渡された値が実際のバックテストと一致することは何も保証していない。**

#388 の受け入れ基準は「**「空売りを含む戦略か」が判定でき**」と書いている。現状は**判定ではなく申告**である。
帰結として、発行ホスト（#688 で新設予定）が `true` を渡し違えれば、**一度も空売りを行っていない戦略の
Stage 0 合格で実弾の空売りが解禁され得る。** これは #388 が「最重要」とした否定形
（空売りを含まない戦略の合格では解禁されない）を、**呼び出し元の正直さだけで守っている**状態である。

`BacktestRun.Fills` は符号付き数量（`SignedQuantity`）を持ち、`BacktestSimulator` は
`SignedInventory.Apply` で建玉を符号付きで畳む。**空売りを行ったかどうかは、走らせた結果から観測できる。**

### 計画が書いていないこと（環流の対象）

計画（ADR-0016 決定 14・06_daytrading-review §4）は「**空売りを含む戦略で** Stage 0 の 7 条件を再度満たす」と
書くだけで、**「含む」の判定方法を定めていない。** 次の 2 つの読みがあり得る。

- **(a) 申告**: 戦略が「空売りを行い得る」と名乗っていれば「含む」
- **(b) 観測**: 検証した走行で実際に空売り建玉を持っていたなら「含む」

**計画は (a) とも (b) とも書いていない。** 本作業は発明せず、**保守的な側（b）** を採り、
計画側へ環流した（planning#534）。

## 決めたこと

詳細と根拠は [IADR-0304](../adr/IADR-0304_short-sell-strategy-observed-not-declared.md)。要点のみ。

1. **`IncludesShortSelling` を観測から導く。** `BacktestRun` の約定列を銘柄・市場ごとに畳み、
   **累計建玉が一度でも負になったか**を純関数で判定する。
2. **`BacktestEvaluatedFactory.From` から真偽値の引数を消す。** 代わりに `BacktestRun` を受け取り、
   内部で観測する。**申告する口を残さない**（残せば発行ホストは今までどおり渡し違えられる）。
3. **未約定は「含まない」へ倒す。** 空売り注文を出したが約定しなかった走行は、空売りの費用も
   ドローダウンも検証していない。保守的な側である。
4. **発注審査（`OrderScreeningService`）への結線は行わない。** IADR-0281 決定 6 の据え置きを維持する
   （理由は下記「入れなかったもの」）。

## 受け入れ基準

- [x] 空売りを行った走行の verdict は `IncludesShortSelling=true` を運ぶ
- [x] **否定形**: 買いだけの走行の verdict は `IncludesShortSelling=false` を運ぶ
- [x] **否定形**: 空売り注文を出したが約定しなかった走行も `false` である
- [x] **否定形**: `IncludesShortSelling` を申告できる引数が公開面に存在しない（構造で固定する）
- [x] equity $5,000 を満たしても verdict が無ければ解禁されない（既存テストが維持されている）
- [x] 空売りを含まない戦略の Stage 0 合格では解禁されない（既存テストが維持されている）
- [x] `LiveTradingGate` の閂 0〜4 に差分が無い

## 入れなかったもの

| 入れなかったもの | 理由 |
| --- | --- |
| `StageGateService.CurrentShortSellRelease()` を `OrderScreeningService` へ結線する | IADR-0281 決定 6 が「借株照会・維持率の供給が無い状態で『材料が揃った』と見える配線を先に作らない」と据え置いた。**その前提は今も未達である**——`IMaintenanceMarginSnapshotSource` は `UnavailableMaintenanceMarginSnapshotSource` が登録されており、借株照会の供給元も無い（`OrderScreeningService` の注記が「空売り文脈は今も組めない」と明記）。#417 / #419 は close 済みだが、**供給アダプタは入っていない**。結線しても解禁の材料は揃わず、`borrow=none;margin=none` のまま verdict を有効にできる経路だけが生える |
| `BacktestEvaluated` の契約変更 | **不要**。#388 が求めた 2 項は既に載っている。契約は 1 バイトも触らない（`event-schemas.baseline.json` の再生成も不要） |
| `IShortSellReleaseSource` の実装・登録 | 借株照会・維持率の供給アダプタ自体が無い。目印だけ実装しても名乗る経路が無い |
| バックテストの最大 DD を `BacktestRun.Metrics` から導く構造化 | IADR-0089 が「発行側の契約」（コメント）で担保している同種の穴だが、#388 の範囲外 |

## 実効化していないこと（PR 本文にも書く）

本経路が実際に verdict を運ぶのは **Stage 0 判定が実効化してから**である。

- 本番戦略（`IBacktestStrategy` 実装）が計画にも実装にも存在しない（環流 planning#533）
- 米国株の日足 OHLC 履歴源の未確認 2 点が残る（#382）
- `BacktestEvaluated` を発行するホストが無い（#688）
