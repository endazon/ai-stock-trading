---
title: 取り込み行に「由来」のラベルを持たせ、報告書 §2-b（手動売買・損益不明）と SC-03 の件数へ出し、報告書側の在庫へ数量だけ反映する
type: spec
status: accepted
related_ids: [FR-06, FR-11, FR-10, FR-16, SC-03, UC-06, ADR-0041, ADR-0003, IADR-0018, IADR-0033, IADR-0107, IADR-0115, IADR-0269, IADR-0271, IADR-0286, IADR-0301, IADR-0305, IADR-0350, IADR-0352, IADR-0360]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (決定 1)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (日報 §2-b・§2 の 11 列・§1 の合計)
  - planning:projects/ai-stock-trading/05_screens/01_screens.md (SC-03 当期のシステム外売買の取り込み件数)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-11 由来のラベル / FR-16 推定で埋めない)
  - planning:docs/glossary.md (「由来（取引記録の）」)
---

# 仕様書: 取り込み行の「由来」・報告書 §2-b・SC-03 の件数（#870 / #859）

## 起点

- **#870**（計画リポジトリの裁定 **ADR-0041 決定 1** に由来する正本）と、それ以前に分離されていた **#859**。**1 本の PR で実装する。**
- 先行: #862 / [IADR-0350](../adr/IADR-0350_owner-approved-ledger-drift-adoption.md)。「システム外の売買で生じた台帳とブローカーの乖離を、
  利用者の承認つきで取り込む」は実装済み（`position_drift_adoptions` 表・`POST /risk-controls/position-drift/adopt`）。
  🔴 **実現損益は記録しない**（推定を確定値から分離して運ぶ通り道が系に無いため。ADR-0041 決定 1 が追認済み）。**この方針は変えない。**
- 新規 IADR: **[IADR-0360](../adr/IADR-0360_manual-trade-origin-and-report-drift-adoption-section.md)**（本作業の設計）。

## 計画側の確認（隣接クローン `../project-planning` を読み取り専用で実読）

| 確認した点 | 実読の結果 |
| --- | --- |
| ADR-0041 決定 1 の射程 | 取引記録の**由来のラベル**・報告書の**「手動売買（損益不明）」の欄**・**SC-03 の当期の取り込み件数**の 3 点。実現損益は記録しない形を追認 |
| 報告書テンプレートの改訂 | 🔴 **ADR-0041 と同じ PR（planning#643・`9014e4f`）で `06_technical/04_report-templates.md` へ §2-b が既に新設されており、`main` にある。** 本 PR でテンプレートへ追加すべき差分は無く、**planning への起票は行わない** |
| §2-b の位置と形 | `## 2. 取引履歴（全明細）` の下の `### 2-b. 手動売買（損益不明）`。**10 列**（`# / 取り込み日時 / 市場 / 銘柄 / 取り込み前の数量 / 観測された数量 / 実現損益 / 操作者 / 理由 / 観測時刻`）。実現損益は常に `不明`。**§1 の合計へ算入しない。該当が無い日は「該当なし」と書く**（欄ごと落とさない） |
| §2-b の適用範囲 | **日報のみ**（週報・月報の粒度対応表に手動売買の行は無い）。番号・並び順は計画が正（ADR-0030） |
| 語彙の分割 | `不明`（§2-b 専用・価格が知り得ない）／`未供給`（供給元が無い）／`算出不能`（分母 0）／`なし`。**混ぜない** |
| 由来と経費区分 | planning `docs/glossary.md`「由来（取引記録の）」＝**誰が約定させたか**。`TradeExpenseCategory`（何の費用か）とは**別の軸**。値は「システムが約定させた取引」「手動売買による取り込み」の 2 つ |
| SC-03 | `05_screens/01_screens.md`「当期のシステム外売買の取り込み件数。**参照のみ**であり、本画面から取り込みは行わない」 |

## 射程

| 含む | 含まない |
| --- | --- |
| A. 取引記録の**由来**（`TradeOrigin`）を型で表し、取り込み行とシステムの約定を記録上で区別する | 経費区分（`TradeExpenseCategory`）への値追加（**別の軸である**。混ぜない） |
| B. 日報 §2-b「手動売買（損益不明）」の新設（10 列・実現損益は常に `不明`・§1 の合計に入れない） | 週報・月報への §2-b（計画の粒度対応表が求めていない） |
| C. SC-03（`GET /risk-controls/status` ＋統制状態画面）へ**当日**の取り込み件数を出す | SC-03 からの取り込み操作（計画が「参照のみ」と定める） |
| D. 報告書側の在庫の畳み込みへ、取り込み行を**数量だけ**反映する | 取り込み行の実現損益・決済件数・勝率・費用の概算・三者比較への算入（🔴 **否定形テストで固定する**） |
| 取り込み行を報告書へ渡す wire（新エンドポイント） | 基準資金を口座照会へ寄せる（ADR-0041 決定 2。**#874 の射程**） |
| | Discord Bot からの取り込み窓口（ADR-0041 決定 4。本 PR の射程外） |

## 母集合（自分で引いた結果・除外理由つき）

### 母集合 1: 由来のラベル（`IsDriftAdoption` を読み書きしている全箇所）

引き方: `grep -rn "IsDriftAdoption" --include=*.cs backend`（`obj/` は grep 対象外）。実測 21 件（うちテスト 7 件）。

| # | 箇所 | 対応 |
| --- | --- | --- |
| 1 | `LedgerFill.cs`（宣言） | `bool IsDriftAdoption` を **`TradeOrigin Origin`** へ置き換える。`IsDriftAdoption` は `Origin == ManualAdoption` の**導出プロパティ**として残す（読み手の分岐を書き換えない） |
| 2 | `PortfolioProjection.cs`（4 箇所）・`PortfolioValuation.cs`（1 箇所） | 導出プロパティ経由。**挙動は変えない** |
| 3 | `BuyInInference.cs`・`GetFills/PeriodFillQuery.cs` | 同上（除外の意味は不変） |
| 4 | `EfPortfolioLedgerStore.cs`（2 箇所）・`InMemoryPortfolioLedgerStore.cs`（1 箇所） | 生成時に `TradeOrigin.System` / `TradeOrigin.ManualAdoption` を明示する |
| 5 | `IPortfolioLedgerStore.cs`・`LedgerDriftAdoption.cs`（doc コメント） | 文言を由来の語彙へ揃える |
| 6 | テスト 7 件 | 導出プロパティのまま通る（意図的に 1 件だけ `Origin` を直接 assert する） |

**除外**: `backend/**/obj/**`（生成物）。

### 母集合 2: 報告書側で在庫を畳んでいる箇所

引き方: `grep -rn "SignedInventory.Apply\|InventoryLot" --include=*.cs backend/Services/ReportService`（テストを除く）。
併せて `grep -rln "PeriodTradeFill" --include=*.cs backend | grep -v Tests`（消費側 13 ファイル）。

| # | 畳み込み | 取り込みを反映するか | 理由 |
| --- | --- | --- | --- |
| 1 | `PnlAggregator.Aggregate` | **する（数量だけ）** | 🔴 **評価損益（参考）が実在しない建玉から出る**のを止める。実現損益・費用・決済件数・勝率には入れない |
| — | 🔴 **［2026-09-19 追記 / #870 の監査 BLK-1］5 箇所すべてで、規則は `PeriodDriftAdoption.ReducedQuantity`（減らす方向にしか効かない・在庫を超える分は 0 でクランプ）である。** 当初はリスク管理と同じ `SignedInventory.Apply` を呼んでいたが、**報告書側の在庫は期間の約定だけから畳まれる**ため期間前の建玉が存在せず、**平均取得単価 0 の幻のショート**が開いていた（日報 §1 に評価損益 −30,000）。詳細と実測は IADR-0360 決定 4 の改定ブロック。 | | |
| 2 | `FillPnlAttributionBuilder.Build` | **する（数量だけ・帰属行は作らない）** | 週報 §2/§3・月報 §2 の内訳。**畳み込み順序と規則を `PnlAggregator` と一致させる**のが不変条件（IADR-0301） |
| 3 | `TradeHistoryViewBuilder.Build` | **する（数量だけ・§2 の明細行にはしない）** | 日報 §2 の実現損益は §1 と同じ畳み込みでなければならない。§2-b の供給元も本ビルダが持つ |
| 4 | `FxTranslationBuilder.Build` | **する（数量だけ・明細は作らない）** | 実在しない建玉の**期末レートでの再測定**を止める。決済時の認識時レートは**知り得ない**ため明細にしない（推定で埋めない） |
| 5 | `ReportDraftService.ResolveCurrentPricesAsync` | **する（数量だけ）** | 実在しない建玉の現在値を市場データ源へ取りに行かない |
| 6 | `ThreeWayComparisonAggregator.Aggregate` | 🔴 **しない** | 段は**発注先**（`Provider`）で分ける。取り込み行は発注先が**不明**であり、どちらの段にも算入しない（IADR-0271 の既存規律）。**担保は型**（引数を持たない）であり、`ThreeWayComparison` に評価損益の欄が無いことと `currentPrices` を渡さないことに依存している点は IADR-0360 決定 4 に明文化した。**否定形テストで固定する** |
| 7 | `PeriodCostReviewBuilder` | しない（変更なし） | 入力は 2 の帰属行のみ。取り込み行は帰属行にならないため、費用の概算へ構造的に入らない |
| 8 | `SummarizePnl/Endpoint.cs`・`DraftReport/Endpoint.cs` | しない（変更なし） | 呼び出し側が約定列を直接与える手動 API。台帳の取り込み行を知る経路が無い |

**除外**: `backend/Services/ReportService/Tests/**`（母集合は本番コード）。`PeriodBreakdown.cs` は `SignedInventory` を
**doc コメントで参照するだけ**で畳み込みを持たない（実測）。

### 母集合 3: `RiskStatusView`（SC-03）の消費側

引き方: `grep -rn "risk-controls/status" --include=*.ts --include=*.tsx --include=*.cs .`。
バックエンド 1 経路（`GetRiskStatus`）＋フロント `frontend/src/lib/risk/contracts.ts` の型と
`frontend/src/features/sc03-controls/components/ControlStatusPage.tsx`、および固定値を持つテスト／E2E フィクスチャ
（`frontend/e2e/fixtures.ts`・`broker-provider.spec.ts`・`sc0*` の各テスト）。**末尾に既定値つきで足す**ため既存の生成箇所は壊れない。

## 決めたこと（詳細は IADR-0360）

1. **由来は `TradeOrigin`（`System` / `ManualAdoption`）という専用の列挙で表す。** 経費区分と混ぜない。
   `LedgerFill.Origin` として台帳の読み口へ載せ、**wire にも出す**（`origin`）。
2. **報告書へは新しいエンドポイント `GET /risk-controls/drift-adoptions?from&to` で渡す。** `fills` に混ぜない。
   🔴 混ぜると、消費側が 1 箇所でも除外を書き忘れたときに「確定値の約定」として集計される＝**失敗が開く側へ倒れる**。
   別の型（`PeriodDriftAdoption`）で運べば、実現損益・勝率・費用・三者比較へ渡すことが**型として不可能**になる。
3. **§2-b は `TradeHistoryView` が持つ**（日報 §2 の子節であり、計画の節順がそう定めている）。
   `null`＝照会できていない／空列＝**該当なし**（欄は必ず出す）。
4. **SC-03 の「当期」は当日**とする。当日の判定は `PortfolioProjection.TradeDate`（**約定・取り込みの市場の現地取引日**）で行う
   ——当日実現損益・日次発注枠と同じ境界を使う（片側だけ JST に残すとずれる。IADR-0246）。

## タスク

- [x] 作業仕様書（本書）・IADR-0360・`.ai-context/adr/README.md` の索引行
- [x] 失敗するテストを先に書く（取り込みが報告書に現れない／実在しない建玉の評価損益が出る）
- [x] A: `TradeOrigin` ＋ `LedgerFill.Origin`
- [x] D の wire: `IPortfolioLedgerStore.GetDriftAdoptions()` ＋ `GET /risk-controls/drift-adoptions`
- [x] C: `RiskStatusView.DriftAdoptionCountToday` ＋ SC-03 画面
- [x] B: `PeriodDriftAdoption` ＋ 供給ポート ＋ §2-b レンダリング
- [x] D: 5 箇所の畳み込みへ数量だけ反映（＋三者比較・費用は算入しない否定形テスト）
- [x] 🔴 ［2026-09-19 追記 / 監査 BLK-1］畳み込みを**減らす方向へクランプ**（`PeriodDriftAdoption.ReducedQuantity`）。
      期間前建玉・在庫超過の 4 経路を `DriftAdoptionPhantomPositionTests` で固定
- [x] `docs/tests/FR-10_risk-controls-tests.md` へ T-10-540〜 を登録

## 受け入れ基準 → テストの写像

| 受け入れ基準 | テスト |
| --- | --- |
| 取り込み行と通常の約定が、記録上の由来で区別できる | `TradeOriginTests`（序数の固定・経費区分と語彙が重ならない）／T-10-541（wire の由来）／T-10-543（API の由来） |
| 報告書に §2-b が出て、実現損益の列に `不明` が入り、§1 の合計に入らない | `TradeHistoryRendererDriftAdoptionTests`／日報のゴールデン `daily-supplied.md`／`DriftAdoptionFoldingTests` |
| 取り込みが無い期間には §2-b が「該当なし」で出る（欄ごと落ちない） | `TradeHistoryRendererDriftAdoptionTests`（空列・`null` の 2 通り） |
| SC-03 に当期の取り込み件数が出る | `RiskStatusServiceTests`（T-10-542）／`ControlStatusPage.driftAdoption.test.tsx` |
| 🔴 実現損益・決済件数・勝率・費用の概算・三者比較のいずれにも算入されない | `DriftAdoptionFoldingTests`（否定形 5 件: 実現損益／決済件数／勝率／費用／三者比較） |
| 取り込み後の期間の報告書が、実在しない建玉の評価損益を出さない | `DriftAdoptionFoldingTests`（評価損益が 0 になる・期末レートで再測定されない）／🔴 `DriftAdoptionPhantomPositionTests`（**期間前建玉・在庫超過で幻のショートを作らない**。日報の生成経路も含む） |
| FR-16「推定で埋めない」 | §2-b の実現損益列は常に `不明`（定数）。推定値を渡す経路そのものを作らない |

## 残余リスク

- **為替差損益**: 取り込みで減った建玉の**決済時の認識時レートは知り得ない**ため、その分の再測定を明細にしない。
  期末に残らない建玉の差損益が（知り得ない分だけ）欄から落ちる。**0 円と書かない・推定もしない**という既存の規律
  （IADR-0286）に従った結果であり、事実は §2-b が別掲する。計画が改めて求めるまで欄を増やさない。
- **`/risk-controls/drift-adoptions` の未供給**: 供給が届かない期間は §2-b が「照会できませんでした」になり、
  同時に**在庫の畳み込みにも取り込みが入らない**（＝実在しない建玉の評価損益が出得る）。両方が同じ 1 つの入力に
  依存することを `ReportInput.DriftAdoptions` として記録・提示する（#840 / IADR-0352 の縮退の枠組みに乗せる）。
