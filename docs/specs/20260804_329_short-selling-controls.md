---
title: 作業仕様書 — リスク統制コアの再実装（第 2 段階: 空売り専用統制 8 規則・拒否理由 7 種・3 統制の優先順位）
type: work
status: review
related_ids: [FR-10, FR-11, FR-19, FR-20, UC-06, ADR-0003, ADR-0009, ADR-0016, ADR-0018, IADR-0130, IADR-0131]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md
related_specs:
  - ./20260804_329_risk-control-core.md
  - ../adr/IADR-0131_short-selling-controls-fail-closed.md
  - ../adr/IADR-0130_equity-ratio-risk-limits.md
  - ../functional/FR-10_risk-controls.md
  - ../tests/FR-10_risk-controls-tests.md
  - ../tests/README.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: リスク統制コアの再実装（#329・第 2 段階）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（空売り専用統制 8 項目・拒否理由 7 種・3 統制の優先順位）／ FR-11（監査）／
  FR-19・FR-20 は境界
- ユースケース（UC）: **UC-06**（設定変更・一時停止・緊急停止。3 統制の関係表）
- 関連 ADR: **ADR-0016**（空売りの段階解禁と専用統制）・**ADR-0009**（3 統制と手仕舞い不停止）・ADR-0003
- 実装 ADR: [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)（本作業の実装方針）／
  [IADR-0130](../adr/IADR-0130_equity-ratio-risk-limits.md)（第 1 段階）／
  [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)（既知逸脱レジストリ）
- 起点 issue: [#329](https://github.com/endazon/ai-stock-trading/issues/329)（親: [#344](https://github.com/endazon/ai-stock-trading/issues/344)）
- 計画書リンク: [02_requirements FR-10](../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md) ／
  [05_trading-assumptions §5](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md) ／
  [ADR-0016](../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md)

## 目的・背景

[第 1 段階](./20260804_329_risk-control-core.md)は金額系上限 3 値の equity 割合化と既定値の計画同期を行い、
`KnownPlanDeviations` の #329 担当 6 件のうち 4 件を解消した。**本段階は残り 2 件**
（`ShortSell.Limits` / `RejectionReason.ShortSellReasons`）を解消する。

空売りが既存の統制と決定的に異なるのは**損失に上限が無い**ことである（ADR-0016 §コンテキスト）。
「1 取引あたりリスク 1%」のような**損切りが機能する前提**の統制だけでは資金保全が成立しないため、
独立した統制群を課す。

## 対象範囲

### 対象（第 2 段階）

1. **空売り専用統制 8 規則**（計画 §5・ADR-0016 決定2,3,4,5,7,9・FR-10 (1)〜(8)）
2. **拒否理由 7 種**（ADR-0016 決定10）と**クラス分類**（7 種はクラス A。クラス C に混ぜない）
3. **3 統制の優先順位**（kill switch ＞ 日次損失ロックアウト ＞ 一時停止）の 3 点セット化
4. 逸脱レジストリ 2 行の削除（赤→緑の実測）

### 対象外（担当を明記）

| 項目 | 担当 |
| --- | --- |
| 借株料・維持率・権利確定日・空売り建玉の**供給元**（moomoo 照会・建玉射影） | [#342](https://github.com/endazon/ai-stock-trading/issues/342)（PoC 項目3）／ #332 |
| 維持率割れによる**自動縮小**（回復目標への最小決済・必要証拠金降順） | [#330](https://github.com/endazon/ai-stock-trading/issues/330)（本段階は値と解決メソッドのみ） |
| 商品種別の 3 値化（現物 / 信用買い / 空売り） | [#332](https://github.com/endazon/ai-stock-trading/issues/332) |
| 拒否の集計（クラス C の件数）と段階ゲートへの結線 | [#333](https://github.com/endazon/ai-stock-trading/issues/333) |
| 強制買戻し（buy-in）イベントの**検知・通知**と禁止リストの永続化 | 未起票（本書「未決事項」§2） |
| 画面（SC-03 の維持率・空売り比率の表示） | [#340](https://github.com/endazon/ai-stock-trading/issues/340) |

## 計画書との突合（8 規則の値と出典）

計画書原文で確認した値のみを実装した。**実装が発明した値は 1 つも無い。**

| # | 規則 | 値 | 出典（原文） |
| --- | --- | --- | --- |
| 1 | 1 銘柄あたりの空売り建玉 | **equity の 10%**（$3,000 で $300） | §5 表「1 銘柄あたりの空売り上限」／ ADR-0016 決定2(a)・決定6 の表 |
| 2 | 逆指値（ストップ注文）の同時発注 | **必須**（未約定・未受理なら建玉を持たない） | §5 同行の後段／ ADR-0016 決定2(b)／ FR-10 本文 (2) |
| 3 | 借株料の上限 | **年率 20%**。**照会不可なら空売りしない** | §5 表「空売りの借株料上限」／ ADR-0016 決定3 |
| 4 | 維持率の閾値 | **40% と規制要求 `max($5.00 ÷ 株価, 30%)` の厳しい方**。境界は **$12.50** | §5 表「空売りの維持率閾値」（2026-08-01 追補2 で $16.67 から是正）／ ADR-0016 決定7 |
| 4' | 自動縮小の回復目標 | **閾値 + 5 ポイント**（閾値に連動） | §5 表「維持率割れによる自動縮小の回復目標」（2026-08-02 追補） |
| 5 | 株価の下限 | **USD 5.00 未満は対象外** | §5 表「空売りの株価下限」／ ADR-0016 決定7 |
| 6 | 空売り比率 | **建玉総額の 50% を超えない** | §5 表「空売り比率の上限」／ ADR-0016 決定9 |
| 7 | 権利確定日 | **前日**の新規空売りを禁止 | §5 注記（142 行）／ ADR-0016 決定5 |
| 8 | 強制買戻し（buy-in） | 検知・記録・通知し、**30 日間**禁止リストへ自動追加 | §5 注記（142 行）／ ADR-0016 決定4 |
| — | 対象市場 | **米国株のみ** | §5 表「空売りの対象市場」／ ADR-0016 決定13 |
| — | 拒否理由 | **7 種・すべてクラス A**。クラス C（`BannedSymbol` / `ManipulativeOrderPattern`）に混ぜない | ADR-0016 決定10 ／ FR-10 本文 ／ 06_daytrading-review §4.1 |

## 設計

### 1. 空売りの識別（IADR-0131 決定1）

`Side == Sell` かつ `PositionEffect == Open`（新規売り建て）。上流（AI）の申告値に依存しない。
有効・無効は `RiskManagementSettings.ShortSell.Enabled`（既定 **false**）で持つ
（`ProductType` の 3 値化は #332 の担当のため先取りしない）。

### 2. 判定の入力と縮退（IADR-0131 決定2・決定4）

| 入力 | 所在 | 欠けたときの縮退 |
| --- | --- | --- |
| 統制値 8 種 | `ShortSellingLimits`（設定） | — |
| 逆指値の有無 | `OrderIntent.StopLossPrice` | 無ければ `StopOrderRequired` |
| 株価（USD） | `OrderIntent.Price`（米国株のためローカル通貨＝ USD） | — |
| equity | `PortfolioSnapshot.Capital`（IADR-0130 決定2） | — |
| 借株可否・料率・維持率・権利確定日・空売り建玉・buy-in 禁止期限 | `ShortSellOrderContext`（外部由来） | **`null` なら `BorrowUnavailable` で拒否**（フェイルクローズ） |

### 3. 規則から拒否理由への写像

| 規則 | 拒否理由 | 判定 |
| --- | --- | --- |
| 空売りが無効 / 対象市場が米国株以外 | `ShortSellDisabled` | 設定・`intent.Market` |
| 1 銘柄あたり 10% / 空売り比率 50% | `ShortExposureExceeded` | 既存建玉 ＋ 当該注文の**累計**で判定 |
| 逆指値なし | `StopOrderRequired` | `StopLossPrice is null`（**計画に無いコード**。IADR-0131 決定3） |
| 借株不可 / 料率照会不能 / buy-in 禁止期間中 | `BorrowUnavailable` | フェイルクローズ |
| 借株料 > 年率 20% | `BorrowCostExceeded` | ちょうど 20% は許容 |
| 維持率 < 適用閾値 / 維持率不明かつ空売り建玉あり | `MaintenanceMarginBreach` | 閾値は株価に依存 |
| 権利確定日の前日 | `DividendRecordDateNear` | 前日のみ（当日・前々日は対象外） |
| 株価 < $5.00 | `ShortPriceFloorBreach` | **`BannedSymbol` では表現しない** |

違反は最初の 1 件で打ち切らず**全件列挙**する（FR-11・`RiskEvaluator` と同じ規律）。
既存統制（1 注文 25%・段階資金上限・kill switch 等）に**上乗せ**して課すため、
「複数の上限が掛かる場合は常に厳しい方が効く」が自動的に成立する（AND 構造）。

### 4. 拒否理由のクラス分類（IADR-0131 決定6）

`RejectionReasonClassification.ClassOf` が A/B/C を返す。クラス C は
`BannedSymbol` / `ManipulativeOrderPattern` の**限定列挙**で、既定はクラス A へ落とす。
計上単位は「1 回の発注拒否につき 1 件」（06_daytrading-review §4.1）。

### 5. 3 統制の優先順位

実体は第 1 段階までに `RiskStatusView.ActiveControl` / `RiskStatusService` にあり、**本段階は
3 点セット化（8 通りの網羅・不変条件・迂回不可）が主作業**である。実装の変更は無い。

### 変更するファイル

| 層 | ファイル | 変更 |
| --- | --- | --- |
| Shared.Contracts | `RejectionReason.cs` | 空売り 7 種 ＋ `StopOrderRequired` を追加 |
| Shared.Contracts | `RejectionReasonClassification.cs` | **新規**（クラス A/B/C・統制違反の計上判定） |
| Domain | `ShortSellingLimits.cs` | **新規**（統制値 7 メンバ＋規制定数＋解決メソッド） |
| Domain | `ShortSellSettings.cs` | **新規**（有効・無効＋統制値） |
| Domain | `ShortSellOrderContext.cs` | **新規**（外部由来の入力） |
| Domain | `ShortSellEvaluator.cs` | **新規**（8 規則の判定コア） |
| Domain | `RiskEvaluator.cs` | 空売り文脈の任意引数と空売り判定の呼び出し |
| Domain | `RiskManagementSettings.cs` / `TradingDefaults.cs` | 空売り設定の保持と既定値 |
| Infrastructure | `RiskSettingsSerialization.cs` | 空売り設定の往復（旧行は既定＝無効で読む） |
| Tests | `ShortSellingControlsTests.cs` / `RejectionReasonClassificationTests.cs` / `TradingControlPriorityTests.cs` | **新規**（3 点セット） |
| Tests | `TradingDefaultsTests.cs` | 空売り既定値の固定 |
| Tests | `KnownPlanDeviations.cs` | **逸脱 2 行の削除** |

## 受け入れ基準

計画書（02_requirements 受け入れ基準 104〜105 行）から本段階が満たすものを転記する。

- [x] **空売りが無効な段階では空売り注文が拒否される**
- [x] **逆指値を同時発注できない場合**に発注が拒否され理由が記録される
- [x] **借株料が年率 20% を超える場合**に発注が拒否され理由が記録される
- [x] **借株料を事前照会できない場合**に発注が拒否され理由が記録される（フェイルクローズ）
- [x] **株価が $5.00 未満の場合**に発注が拒否され理由が記録される
- [x] **1 銘柄あたり上限（equity の 10%）または空売り比率 50% を超える場合**に拒否される
- [x] **権利確定日の前日**に拒否される
- [x] 強制買戻しを検知した銘柄が **30 日間**空売り禁止となる（禁止期間の判定。検知経路は未決事項 §2）
- [x] 維持率の閾値が「40% と規制要求の厳しい方」であり、境界が **$12.50** である
- [x] **空売りの 7 種の拒否理由が「統制違反 0 件」（クラス C 限定）の件数に計上されない**。`BannedSymbol` にも混入しない
- [x] 3 統制（kill switch ＞ 日次損失ロックアウト ＞ 一時停止）の優先順位が成立し、いずれも**手仕舞い（Close）と損切りを止めない**
- [ ] 維持率割れによる**自動縮小**（#330）

## テスト方針

[テスト戦略](../tests/README.md) §2 の 3 点セットで写像する。詳細は
[テスト仕様書 FR-10](../tests/FR-10_risk-controls-tests.md)。**否定形は「拒否されること」ではなく
「迂回経路が塞がれていること」**を見る（照会不能・分割発注・別市場・別統制の解除・手仕舞い経路）。

### 計画適合検査の赤→緑（IADR-0127 の機械的証明）

| 段階 | 結果 |
| --- | --- |
| 実装を計画へ一致させ、登録 2 行を残したまま実行 | **Failed: 2, Passed: 4**（検査3「登録済み逸脱は実際に逸脱している」・検査4「登録済み逸脱の現行値は実装の実際値と一致する」が `ShortSell.Limits` / `RejectionReason.ShortSellReasons` を名指し） |
| 登録 2 行を削除して実行 | **Failed: 0, Passed: 6** |

これで **#329 担当の逸脱 6 件がすべて解消**した（第 1 段階 4 件・第 2 段階 2 件）。

## 計画書との差異

- 差異: **あり**（2 件・いずれも計画へ環流済み）
  1. **`StopOrderRequired`（拒否理由 8 種目）**: FR-10 本文と ADR-0016 決定2(b) は逆指値の同時発注を
     明示的に課すが、決定10 の拒否理由 7 種に対応するコードが無い。規則を実装しないと
     「逆指値なしの空売り」が素通りするため、実装側で 1 種を新設しクラス A とした
     （[IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md) 決定3）。
     計画への環流: [feedback/20260804_adr0016-stop-order-rejection-reason.md](../../feedback/20260804_adr0016-stop-order-rejection-reason.md)
  2. **強制買戻し 30 日禁止の拒否理由**: 決定4 は「禁止銘柄リストへ自動追加」と書くが、決定10 は
     市況由来の事象を `BannedSymbol`（クラス C）で表現することを禁じている。`BorrowUnavailable`
     （クラス A）へ写像した。同じフィードバックで環流している

## 未決事項

1. **空売り文脈の供給元**: 借株照会（moomoo）・空売り建玉の射影・コーポレートアクション（権利確定日）の
   取得経路は未実装であり、現状は**すべての新規売り建てが拒否される**（フェイルクローズ）。
   これは既定（空売り無効）と ADR-0016 決定8（実弾解禁は Stage 3・自己資金 $5,000 以上）に整合するが、
   **Stage 1（SIMULATE）で空売りを検証するには供給元が要る**。借株照会の成否は #342 の PoC 項目3 が
   確認し、不成立なら空売りフラグを恒久的に無効とする（決定3）。
2. **強制買戻し（buy-in）の検知・通知・禁止リストの永続化**: ADR-0016 決定4 は検知・記録・通知と
   30 日間の自動追加を求める。本段階は**禁止期間の値と判定**（`BuyInBanDurationDays` /
   `BuyInBanUntil` / 期間中の拒否）を実装したが、**イベントの受信経路と禁止リストの保存は未実装**である。
   決定14 の表は「SIMULATE では発生しないため、実弾解禁前に受信経路の疎通確認を行う」としており、
   ブローカー側の実装（#342）に依存する。**担当 issue の起票要否を監査判断に委ねる。**
3. **空売り比率 50% の分母**: 計画の文言（「空売り建玉の合計は、建玉総額の 50% を超えない」）を
   文字どおり実装したため、**ロング建玉が無ければ 1 件目の空売りが通らない**（IADR-0131 決定5）。
   決定9 の趣旨（ロングとショートの混在）どおりだが、Stage 1 の検証で「空売りだけの検証ができない」
   という運用上の含意がある。緩めるなら計画側の裁定が要る。
4. **`ShortSellSettings.Enabled` と `ProductType.ShortSell` の重複**: #332 の 3 値化で統合を検討する。

## 検証結果

| 検証 | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx` | 0 Warning / 0 Error |
| `dotnet test`（`Category!=Integration`） | **2,418 passed / 0 failed**（第 1 段階 2,298 から +120） |
| 計画適合の赤→緑 | 削除前 **Failed: 2, Passed: 4** → 削除後 **Failed: 0, Passed: 6**（実測） |
| `dotnet format --verify-no-changes` | 差分なし |
| `node scripts/check-test-traceability.js` | OK（テスト 322 ファイル・起点 ID 25 種） |
| `node scripts/check-coverage.js` | 行カバレッジ **65.56%** / floor 62.00% |
| `node scripts/scripts.test.js` | 143 tests passed |
| `node scripts/check-banned-libraries.js` | OK |
| アーキテクチャテスト | 4 passed |

## 関連仕様

- 実装 ADR: [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)（本段階）・
  [IADR-0130](../adr/IADR-0130_equity-ratio-risk-limits.md)（第 1 段階）・
  [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)
- 作業仕様書: [20260804_329_risk-control-core](./20260804_329_risk-control-core.md)（第 1 段階）
- 機能仕様書: [FR-10 リスク統制](../functional/FR-10_risk-controls.md)
- テスト仕様書: [FR-10 リスク統制（再実装）](../tests/FR-10_risk-controls-tests.md)
- 計画への環流: [feedback/20260804_adr0016-stop-order-rejection-reason.md](../../feedback/20260804_adr0016-stop-order-rejection-reason.md)
