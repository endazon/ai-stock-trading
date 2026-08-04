---
title: 作業仕様書 — 計画 submodule を 2026-08-04 トリアージ結果へ同期し、空売り拒否理由を 9 種へ追随させる（BuyInBanned の新設）
type: work
status: review
related_ids: [NFR, FR-10, FR-11, FR-19, FR-20, UC-06, ADR-0007, ADR-0016, IADR-0127, IADR-0131, IADR-0132, IADR-0134]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
related_specs:
  - ./20260804_329_short-selling-controls.md
  - ./20260804_332_trading-guards.md
  - ../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md
  - ../adr/IADR-0131_short-selling-controls-fail-closed.md
  - ../adr/IADR-0127_plan-conformance-known-deviation-registry.md
  - ../functional/FR-10_risk-controls.md
  - ../tests/FR-10_risk-controls-tests.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: 計画 submodule の同期と空売り拒否理由 9 種への追随（#374）

## 起点となる計画書（トレーサビリティ）

- 非機能（NFR）: 計画 submodule のピン更新（計画と実装の同期）
- 機能要求（FR）: **FR-10**（空売り専用統制・拒否理由）／ FR-11（監査ログの理由）／ FR-19・FR-20（境界）
- ユースケース（UC）: UC-06
- 関連 ADR: **ADR-0016 決定10**（2026-08-04 改訂・拒否理由 9 種）／ ADR-0016 決定4（強制買戻しの 30 日禁止）／
  ADR-0016 決定9（空売り比率の等価形）／ ADR-0007（2026-08-04 改訂・ガードの適用範囲）
- 実装 ADR: [IADR-0134](../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md)（本作業）／
  [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)（空売り統制の実装方針）／
  [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)（計画適合レジストリ）
- 起点 issue: [#374](https://github.com/endazon/ai-stock-trading/issues/374)
- 由来: [project-planning#194](https://github.com/endazon/project-planning/pull/194)（環流 issue #177〜#192 のトリアージ結果）

## 目的・背景

計画リポジトリが 2026-08-04 に、本リポジトリ発の環流 3 件（project-planning
[#177](https://github.com/endazon/project-planning/issues/177) /
[#178](https://github.com/endazon/project-planning/issues/178) /
[#179](https://github.com/endazon/project-planning/issues/179)）を裁定した。submodule のピンは
`df8bce5` のままであり、**実装はまだ新しい計画を見ていない**。ピンを `4cbd3e2` へ進め、
裁定の結果を実装・テスト・記録へ追随させる。

## 裁定の内容と実装への影響

| 環流 | 裁定 | 実装への影響 |
| --- | --- | --- |
| #177 空売り比率の分母 | **案 A（文字どおり維持）で確定**。等価形「空売り建玉 ≦ ロング建玉総額」と「ロング建玉が無ければ空売りは開始できない」を ADR-0016 決定9・§5 へ明記 | **変更不要**（現行実装が正しい。T-10-156 / T-10-171 が固定済み） |
| #178 拒否理由コードの不足 | **7 種 → 9 種へ改訂**。`StopOrderRequired` を**同名で追認**、**`BuyInBanned` を新設** | **追随が必要**（本作業の主題） |
| #179 ガードの適用範囲 | **商品種別＝新規建て（Open）のみ**（実装を追認）。**禁止銘柄＝全注文**（案 A・実装を追認）。段階別の商品種別強制（FR-20）にも同じ範囲を適用すると明記 | **変更不要**（IADR-0132 決定4・決定5 が既に一致。FR-20 側の結線は #333 の担当） |

### ADR-0016 決定10 の 9 種（計画原文の表・2026-08-04 改訂）

| 拒否理由 | 意味 | 由来 |
| --- | --- | --- |
| `ShortSellDisabled` | 空売りが無効に設定されている | 決定 1 |
| ★ `StopOrderRequired` | 逆指値（ストップ注文）を建玉と同時に発注できない | 決定 2(b) |
| `BorrowUnavailable` | 借株できない（locate 失敗） | Reg SHO |
| `BorrowCostExceeded` | 借株料が年率 20% を超える | 決定 3 |
| ★ `BuyInBanned` | 強制買戻しの発生により 30 日間の空売り禁止期間中である | 決定 4 |
| `ShortExposureExceeded` | 空売り建玉の上限を超える | 決定 2(a) / 決定 9 |
| `MaintenanceMarginBreach` | 維持率が閾値を割り込む | 決定 7 |
| `DividendRecordDateNear` | 権利確定日が近い | 決定 5 |
| `ShortPriceFloorBreach` | 株価が $5.00 未満 | 決定 7 |

★ が 2026-08-04 の追加分。**9 種すべてクラス A**であり「統制違反 0 件」（クラス C 限定）に計上しない。

> 計画の明示的な禁止（決定10 の 2026-08-04 追記）:
> **「`BuyInBanned` を `BorrowUnavailable` へ写像してはならない」**。`BorrowUnavailable` は
> **都度の借株需給**による locate 失敗、`BuyInBanned` は**期間の経過**で解除される禁止状態であり、
> 原因も解除条件も異なる。写像すると監査ログ（FR-11）の理由が実態と食い違い、
> 日報・月報の「強制買戻しの発生有無・発生回数」（決定15）を拒否記録から復元できなくなる。

### 実装との差分（何が足りなかったか）

| コード | 実装 enum | 計画適合の抽出候補（`ActualDefaults`） | 対応 |
| --- | --- | --- | --- |
| `StopOrderRequired` | **有り**（#329 で先行実装・IADR-0131 決定3） | **無し**（計画が 7 種だったため候補に入れていなかった） | 候補へ追加。実装コードは変更不要 |
| `BuyInBanned` | **無し**（`BorrowUnavailable` へ写像していた） | 無し | **enum へ新設**し、`ShortSellEvaluator` の写像を切り替える |

## 対象範囲

### 対象

1. `planning` submodule のピン更新（`df8bce5` → `4cbd3e2`）
2. `RejectionReason.BuyInBanned` の新設（**末尾へ追加**。既存メンバの序数は不変）
3. `ShortSellEvaluator` の buy-in 禁止期間中の拒否を `BorrowUnavailable` → `BuyInBanned` へ
4. 計画適合レジストリの追随（`PlanRiskDefaults` の計画値 9 種・`ActualDefaults` の抽出候補 9 名）
5. 否定形テスト（写像していないことの証明）・序数固定テスト
6. 記録の追随（IADR-0131 追記・IADR-0134 新規・環流 3 件の裁定結果・機能／テスト仕様書）

### 対象外（担当を明記）

| 項目 | 担当 |
| --- | --- |
| 強制買戻し（buy-in）イベントの**検知・通知**と禁止リストの永続化 | 未起票（#329 第 2 段階 未決事項 §2。ADR-0016 決定14 は実弾解禁前の疎通確認としている） |
| 段階別の商品種別強制（FR-20）へのガード適用範囲の結線 | [#333](https://github.com/endazon/ai-stock-trading/issues/333) |
| 日報・月報への「強制買戻しの発生回数」の集計（決定15） | 報告書側（未起票。`BuyInBanned` の分離で集計の入力は揃った） |
| 現金口座（ADR-0021）に伴う `CashAccountSettlementHold` 等 | 本 issue の範囲外（ADR-0021 決定4） |

## 設計

### 1. enum への追加位置（IADR-0134 決定2）

`RejectionReason` は HTTP 経路で**整数として**往来する（段階ゲートの `RejectionReasons` は
`IReadOnlyList<int>`）。既存メンバの間へ挿入すると**過去の記録の意味が変わる**ため、
`BuyInBanned` は**末尾（序数 22）へ追加**する。序数は
`RejectionReasonOrdinalStabilityTests` が全 23 メンバ分を固定する。

### 2. 判定の写像

```
context.BuyInBanUntil is { } banUntil && context.Today < banUntil
    → RejectionReason.BuyInBanned      （旧: BorrowUnavailable への写像＋重複排除）
```

旧実装は `BorrowUnavailable` が既に列挙されていれば追加しない重複排除を行っていた。
理由が分離されたため重複は起こり得ず、**判定は 1 行になる**。借株需給による拒否と
禁止期間による拒否は**同時に立ち得る**（両方が真なら両方が列挙される）——違反の全件列挙という
`ShortSellEvaluator` の規律（FR-11 監査）どおりである。

### 3. クラス分類

`RejectionReasonClassification.ClassOf` は既定でクラス A へ落とすため、
`BuyInBanned` は**追加のみでクラス A** になる。クラス C は限定列挙（`BannedSymbol` /
`ManipulativeOrderPattern`）であり、`すべての拒否理由がいずれかのクラスに分類される` が
クラス C の件数 2 を固定しているため、既定混入は機械的に塞がれている。

### 変更するファイル

| 層 | ファイル | 変更 |
| --- | --- | --- |
| — | `planning`（submodule） | ピンを `4cbd3e2` へ |
| Shared.Contracts | `Trading/RejectionReason.cs` | `BuyInBanned` を**末尾へ新設**。`BorrowUnavailable` / `StopOrderRequired` の XML ドキュメントを裁定後の記述へ |
| Shared.Contracts | `Trading/RejectionReasonClassification.cs` | 「7 種」→「9 種」（記述のみ。分類ロジックは不変） |
| Domain | `ShortSellEvaluator.cs` | (8) の写像を `BuyInBanned` へ。冒頭の規則一覧も追随 |
| Domain | `ShortSellOrderContext.cs` | `BuyInBanUntil` の理由コードを明記 |
| Tests | `PlanConformance/PlanRiskDefaults.cs` | 計画値を 9 種へ（計画側の転記） |
| Tests | `PlanConformance/ActualDefaults.cs` | 抽出候補を 9 名へ（`StopOrderRequired` の欠落も是正） |
| Tests | `ShortSellingControlsTests.cs` | 30 日禁止・クラス C 非計上を `BuyInBanned` へ。**否定形 2 件を追加** |
| Tests | `RejectionReasonClassificationTests.cs` | 9 種の分類・**否定形 1 件を追加** |
| Tests | `RejectionReasonOrdinalStabilityTests.cs` | **新規**（序数の固定・表の網羅） |

## 受け入れ基準

- [x] submodule pin が `4cbd3e2`
- [x] `RejectionReason` に `BuyInBanned` があり、buy-in 禁止期間中の拒否がこれを使う
- [x] `BuyInBanned` が `BorrowUnavailable` へ写像されないことを**否定形テスト**が証明する（逆向きも）
- [x] 既存メンバの序数が変わっていない（`RejectionReasonOrdinalStabilityTests`）
- [x] 計画適合テストが green（`RejectionReason.ShortSellReasons` が 9 種で一致）
- [x] 9 種すべてがクラス A であり、統制違反（クラス C）に計上されない
- [x] `dotnet test`（`Category!=Integration`）が green

## テスト方針

[テスト戦略](../tests/README.md) §2 の 3 点セットで写像する。**否定形は「拒否されること」ではなく
「迂回経路が塞がれていること」**を見る——本作業の迂回は「2 つの異なる事象を 1 つのコードへ畳んで
監査ログから区別できなくすること」であり、**両向き**（`BuyInBanned` → `BorrowUnavailable` /
`BorrowUnavailable` → `BuyInBanned`）を塞ぐ。

| ID | 種別 | 見るもの | テストメソッド |
| --- | --- | --- | --- |
| T-10-148 | 境界値 | 強制買戻し検知後 30 日間の禁止（0 / 29 / 30 / 31 日後）。理由は `BuyInBanned` | `強制買戻し検知銘柄は30日間空売りできない` |
| T-10-155 | プロパティ | 空売りの拒否理由 **9 種**すべてがクラス A | `空売りの拒否理由はいずれもクラスAであり統制違反に計上しない` ほか |
| T-10-165 | 否定形 | 強制買戻し禁止をクラス C（`BannedSymbol`）へ寄せる迂回 | `強制買戻しの禁止期間中は通らずクラスCにも計上されない` |
| **T-10-172** | 否定形 | **禁止期間の拒否を `BorrowUnavailable` へ写像する**（借株は成立している状態で見る） | `強制買戻しの禁止期間中の拒否は借株不可へ写像されない` |
| **T-10-173** | 否定形 | **逆向きの写像**（locate 失敗を `BuyInBanned` へ寄せ、発生回数を水増しする） | `借株できないだけの拒否は強制買戻し禁止へ写像されない` |
| **T-10-174** | 否定形 | 2 つのコードが別物であること（分類は同じクラス A でも記録の粒度が違う） | `強制買戻しの禁止と借株不可は別の拒否理由である` |
| **T-10-175** | 否定形 | **既存メンバの序数を変える**（過去の記録の意味が変わる）／ 序数表の更新漏れ | `拒否理由の序数は不変である` / `序数表はすべての拒否理由を網羅する` |

### 計画適合検査の赤→緑（実測）

**IADR-0127 の機構は「submodule のピン更新だけでは赤くならない」**（IADR-0134 決定3）。
`PlanRiskDefaults` は計画書を**人手で転記**した表であり、計画適合テストは planning submodule の
ファイルを一切読まないためである。実測は次のとおり。

| 段階 | 結果 |
| --- | --- |
| 1. submodule のみ `4cbd3e2` へ更新 | **Failed: 0, Passed: 6**（＝赤くならない） |
| 2. 計画側の転記（`PlanRiskDefaults` を 9 種へ）を追加 | **Failed: 1, Passed: 5**。検査1 が `RejectionReason.ShortSellReasons: 計画「…, BuyInBanned, …, StopOrderRequired」/ 実装「…（7 種）」` を名指し |
| 3. 実装（`BuyInBanned` の新設・写像の切替・抽出候補の是正） | **Failed: 0, Passed: 6** |

赤は段階 2 で初めて出る。**転記を忘れると計画との乖離は永久に検知されない**——この限界と
補い方は [IADR-0134](../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定3 に記録した。

## 計画書との差異

- 差異: **なし**。本作業は計画（ADR-0016 決定10 の 2026-08-04 改訂）へ実装を一致させるものである。
  #329 第 2 段階で残っていた差異 2 件（`StopOrderRequired` の追認待ち・強制買戻しの理由コード）は、
  いずれも本作業で**解消**した（前者は同名で追認、後者は新設で裁定）。

## 未決事項

1. **強制買戻し（buy-in）の検知・通知・禁止リストの永続化**は引き続き未実装である（担当 issue 未起票）。
   `BuyInBanned` は**禁止期間中の判定と記録**を担うが、`ShortSellOrderContext.BuyInBanUntil` を
   供給する経路が無いため、現状は常に `null`（禁止なし）である。ADR-0016 決定14 は
   「SIMULATE では発生しないため実弾解禁前に受信経路の疎通確認を行う」としている。
2. **日報・月報の「強制買戻しの発生回数」**（決定15）は、拒否記録の `BuyInBanned` 件数だけでは
   **発生回数にならない**（1 回の強制買戻しに対して禁止期間中の拒否は何度でも起こり得る）。
   集計の入力は強制買戻し**イベント**であるべきで、上記 1 の経路に依存する。報告書側の担当。
3. `ShortSellSettings.Enabled` と `ProductType.ShortSell` の重複（#329 からの持ち越し。#332 の範囲）。

## 検証結果

| 検証 | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx` | **0 Warning / 0 Error** |
| `dotnet test`（`Category!=Integration`） | **2,552 passed / 0 failed**（追随前 2,522 から +30） |
| 計画適合の赤→緑 | submodule のみ **Failed: 0, Passed: 6** → 転記後 **Failed: 1, Passed: 5** → 実装後 **Failed: 0, Passed: 6** |
| `dotnet format backend/backend.slnx --verify-no-changes` | 差分なし |
| `node scripts/check-commit-messages.js` | OK |
| `node scripts/check-test-traceability.js` | OK |
| `node scripts/check-doc-links.js` | OK |

## 関連仕様

- 実装 ADR: [IADR-0134](../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md)（本作業）・
  [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)・
  [IADR-0132](../adr/IADR-0132_product-type-tri-state-and-guard-scope.md)・
  [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)
- 作業仕様書: [20260804_329_short-selling-controls](./20260804_329_short-selling-controls.md)（空売り統制の本体）・
  [20260804_332_trading-guards](./20260804_332_trading-guards.md)（ガードの適用範囲）
- 機能仕様書: [FR-10 リスク統制](../functional/FR-10_risk-controls.md)
- テスト仕様書: [FR-10 リスク統制（再実装）](../tests/FR-10_risk-controls-tests.md)
- 計画への環流（いずれも裁定済み）: [拒否理由コードの不足](../../feedback/20260804_adr0016-stop-order-rejection-reason.md)・
  [空売り比率 50% の構造的含意](../../feedback/20260804_adr0016-short-ratio-denominator.md)・
  [取引ガードの適用範囲](../../feedback/20260804_fr19-guard-scope.md)
