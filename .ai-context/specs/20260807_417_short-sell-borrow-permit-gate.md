---
title: 空売りの一次ゲートを借株料 20% から IsShortPermit（借株可否）へ移す（ADR-0016 決定3 の 2026-08-06 改訂への追随）
type: spec
status: approved
related_ids: [FR-10, FR-15, FR-17, UC-06, ADR-0016, ADR-0019, IADR-0131, IADR-0134, IADR-0144, IADR-0158]
author: Claude Code
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
related_specs:
  - ../adr/IADR-0158_short-sell-borrow-permit-primary-gate.md
  - ../adr/IADR-0131_short-selling-controls-fail-closed.md
  - ../adr/IADR-0144_moomoo-short-selling-poc-outcomes.md
  - ../../docs/functional/FR-10_risk-controls.md
  - ../../docs/tests/FR-10_risk-controls-tests.md
  - ../../docs/blocked-tasks.md
  - 20260804_329_short-selling-controls.md
  - 20260805_342_moomoo-poc-plan.md
---

# 仕様書: 空売りの一次ゲートを `IsShortPermit`（借株可否）へ移す（#417）

> 本仕様書は実装着手前に作成した。以降の作業は本書に沿って進める。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（リスク統制・空売り 8 規則）／FR-15・FR-17（費用計算＝**接続しない**側の相手方）
- ユースケース（UC）: **UC-06**（統制設定の変更・維持率と空売りの現況）
- 関連 ADR:
  **ADR-0016（計画リポ） 決定3（2026-08-06 改訂）**（本作業の直接の起点）／
  同 決定10（拒否理由 9 種・すべてクラス A・畳まない規律）／同 決定14（Stage 1 で検証できない統制の表）／
  ADR-0019（計画リポ） 決定1 項目3（実測の出所）
- 関連 IADR: **[IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)**（空売り統制のフェイルクローズ・本作業で改訂節を追記）／
  **[IADR-0144](../adr/IADR-0144_moomoo-short-selling-poc-outcomes.md) 決定4**（実測に基づく実装方針・本作業でその**後継**となる）／
  [IADR-0134](../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md)（拒否理由 9 種の写経）／
  **IADR-0158**（本作業で新規作成）
- 対象 Issue: [#417](https://github.com/endazon/ai-stock-trading/issues/417)
- 計画 submodule: **`e36b592`**（#416 で同期済み。決定3 の 2026-08-06 改訂節を直接読める）

## 目的・背景

計画側で **ADR-0016 決定3 が改訂された**（利用者裁定 2026-08-06・環流 endazon/project-planning#204）。

| 改訂前の前提 | 実測（ADR-0019 PoC 項目3・8 銘柄・1 時点） |
| --- | --- |
| 借りにくい銘柄ほど借株料が高い。よって**コストと危険度を同じ閾値（年率 20%）で弾ける** | **成り立たなかった。** `ShortPoolRemain` が 20 倍以上開いても `ShortFeeRate` は**一律 1.5**（AAPL 26,452,338 / GME 1,898,200 / RIOT 1,201,180 のいずれも 1.5） |
| — | 実際に危険な銘柄を弾いているのは **`IsShortPermit`**。AMC・SPCE は `IsShortPermit=False` / `ShortPoolRemain=0` / `ImShortRatio=100` を返し、**API は銘柄を明確に区別している** |

**一律料率であれば 20% の閾値は永久に超えず、本統制は何も弾かない。** これは本リポジトリが繰り返し
潰してきた「実装したが効いていない」の一例であり、`docs/blocked-tasks.md` の
「実装済みだが発動しない機能」にも登録済みである。裁定はその是正を求めている。

## 対象範囲

### 対象（本 PR でやること）

1. **一次ゲートを `IsShortPermit=False` の拒否へ移す。** 発注前照会で借株不可と判明した銘柄は空売りしない。
2. **拒否理由は既存の `BorrowUnavailable`（クラス A）へ写像する。新しいコードを追加しない**（決定3 改訂が明示）。
3. **20% の閾値判定を残置し、「発火しない既知の統制」であることをコード・テスト・文書に記録する。**
   拒否理由コード `BorrowCostExceeded` も残す。
4. **`ShortFeeRate` を費用計算（FR-17 / FR-15）へ接続しないことを否定形テストで固定する**（値 `1.5` の単位が未確定）。
5. 文書の追随: IADR-0158（新規）・IADR-0131 の改訂節・機能仕様書 FR-10・テスト仕様書 FR-10・`blocked-tasks.md`・環流記録。

### 対象外（本 PR でやらないこと）

| やらないこと | 理由 |
| --- | --- |
| **新しい拒否理由コードの追加** | 決定3 改訂が「新しいコードは追加しない」と明示 |
| **20% 閾値判定・`BorrowCostExceeded` の削除** | 料率が銘柄別になったときに無防備になる（同上） |
| **`ShortFeeRate` の費用計算への接続** | 値 `1.5` の**単位が未確定**（年率 1.5% か否か）。取り違えると費用モデルが 100 倍ずれる |
| **ADR-0016 決定4 の改訂（強制買戻しの事後推定）への追随** | 別 issue の範囲。本 PR に混ぜない |
| **借株照会の供給元（`TrdGetMarginRatio` の実弾ヘッダ照会・キャッシュ）の実装** | #331 / #342 系の範囲。本 PR は判定側の規則だけを移す |
| **`LiveTradingGate.LiveTradingReleased` の変更** | 実弾解禁の閂。触れない |

## 設計

### 現状（develop）の判定

`ShortSellEvaluator.Evaluate` は借株について次の 1 つの分岐を持つ。

```csharp
if (!context.BorrowAvailable || context.BorrowRateAnnual is null)  // → BorrowUnavailable
else if (context.BorrowRateAnnual > limits.BorrowRateCapAnnual)    // → BorrowCostExceeded
```

`BorrowAvailable` は「locate が成立したか（Reg SHO）」という**一般名**であり、**どの実測フィールドが
供給元になるのかがコードから読めない**。決定3 の改訂は「一次ゲートは `IsShortPermit` である」と
名指ししたため、実装側もその名前で受けるのが正しい（供給を書く者が別のフィールドを当てられない）。

### 決定した実装

| # | 変更 | 内容 |
| --- | --- | --- |
| 1 | `ShortSellOrderContext.BorrowAvailable` → **`ShortPermit`** | 供給元を **moomoo `TrdGetMarginRatio.IsShortPermit` 一本**に名指しする。既定 `false`（＝借株不可・安全側）。`false` は「借株不可」と「照会できていない」の両方を含み、**いずれも `BorrowUnavailable`** |
| 2 | `ShortSellEvaluator` の借株分岐を **一次 / 二次に分ける** | 一次＝`ShortPermit == false` → `BorrowUnavailable`。二次＝料率 `null` → `BorrowUnavailable`（決定3 の**未改訂部分**＝照会不可なら空売りしない）／上限超 → `BorrowCostExceeded`（**発火しない既知の統制**として残置） |
| 3 | `ShortSellOrderContext.BorrowRateAnnual` の契約を明記 | **単位が確定した年率のみ**を与える。moomoo の `ShortFeeRate`（実測 1.5・単位未確定）を**そのまま写像しない** |
| 4 | 否定形テスト（費用計算） | `CostCalculator` / `TradingAssumptions` / `BacktestCostModel` の公開面に借株料の入口が**無い**ことを固定する |

**判定順序と拒否理由の関係**（変更後）:

| 入力 | 拒否理由 | 根拠 |
| --- | --- | --- |
| 文脈そのものが `null`（照会経路が無い） | `BorrowUnavailable` | 決定3・IADR-0131 決定2（不変） |
| **`ShortPermit == false`（`IsShortPermit=False`）** | **`BorrowUnavailable`（一次ゲート）** | **決定3 改訂（2026-08-06）** |
| `ShortPermit == true` かつ料率 `null`（照会不能） | `BorrowUnavailable` | 決定3 の未改訂部分（フェイルクローズ） |
| `ShortPermit == true` かつ料率 > 年率 20% | `BorrowCostExceeded` | 決定3・決定10（**残置**。実測の情報源では発火しない） |

**一次ゲートが立ったとき二次は評価しない**（`else if` の連鎖）。借株できない銘柄に「料率が高い」も
併記すると、監査ログ（FR-11）の理由が実態より多弁になり、原因（可否 or 料率）の切り分けが濁る。

### 「発火しない既知の統制」をどう表現するか

「発火しない」と「無い」は別である。**残置していることを 3 か所で固定する。**

1. **コード**: `ShortSellEvaluator` の二次分岐に、実測（一律 1.5）で発火しない見込みであることと、
   **落とすと料率が銘柄別になったとき無防備になる**ことを起点 ID 付きで書く。
2. **テスト**: 上限超の料率を与えれば **いまも `BorrowCostExceeded` が立つ**ことを固定する
   （境界テーブルは既存。加えて一次ゲートと二次の独立を新規テストで固定する）。
   さらに **`ShortSellingLimits.BorrowRateCapAnnual` の実在は計画適合検査（`PlanRiskDefaults`）が
   メンバ列として写経済み**であり、削除すると別方向からも赤くなる。
3. **文書**: 機能仕様書 FR-10 の 8 規則表・テスト仕様書・IADR-0158・`blocked-tasks.md`。

### `ShortFeeRate` の単位が未確定であることの帰結（設計に織り込む）

実測値 `1.5` を**そのまま年率**として `BorrowRateAnnual` へ写像すると、上限 `0.20` を 7.5 倍超過して
**全銘柄が `BorrowCostExceeded` で拒否される**。逆に `1.5%` の意味なら `0.015` であり発火しない。
**同じ値が、単位の読み方ひとつで「何も弾かない」から「全部弾く」へ反転する。**

したがって本 PR は**写像そのものを行わない**（供給を書かない）。単位が確定するまで
`BorrowRateAnnual` は `null` のままであり、フェイルクローズにより空売りは拒否され続ける。
**この反転の危険を実行可能な形で残すため、テストで「1.5 をそのまま年率とすると拒否になる」ことを固定する。**

## 受け入れ基準

- [ ] `IsShortPermit=False` に相当する入力（`ShortPermit == false`）の銘柄は、**料率が上限内でも**空売りできない。
- [ ] 上記の拒否理由は **`BorrowUnavailable`** であり、**新しいコードを追加していない**（拒否理由は 9 種のまま）。
- [ ] `BorrowUnavailable` は**クラス A** のままであり、クラス C（統制違反 0 件の計上対象）に混ざらない。
- [ ] **20% 閾値判定が残っている**（上限超の料率で `BorrowCostExceeded` が立つ）。
- [ ] **`ShortFeeRate` が費用計算（FR-17 概算費用・FR-15 バックテスト）へ流れていない**（否定形）。
- [ ] 照会そのものが失敗したとき（料率 `null`・文脈 `null`）は fail-closed（既存の振る舞いを壊していない）。
- [ ] `LiveTradingGate.LiveTradingReleased = false` を変更していない。
- [ ] `dotnet build`（警告 0）・`dotnet test`（`Category!=Integration`）・`dotnet format --verify-no-changes`・
      `check-doc-links` / `check-commit-messages` / `check-banned-libraries` / `check-test-traceability` /
      `check-consumer-endpoint-names` が緑。

## テスト方針

テスト戦略（`docs/tests/README.md` §2）の 3 点セットに沿う。追加・改訂は
`ShortSellingControlsTests`（FR-10）と費用計算側の 2 テストである。

| 観点 | テスト | 種別 |
| --- | --- | --- |
| 一次ゲート（**最重要**） | 借株不可（`ShortPermit=false`）なら**料率が上限内でも**空売りは通らない | 否定形 |
| 一次と二次の独立 | 借株不可のとき `BorrowCostExceeded` は立たない（可否と料率を混ぜない） | 否定形 |
| クラス分類 | 一次ゲートが返した理由は**クラス A** であり `CountsAsControlViolation` が false | プロパティ |
| 閾値の残置 | 上限超の料率で `BorrowCostExceeded` が立つ（境界テーブルは既存） | 境界値 |
| 単位の反転 | 実測値 `1.5` をそのまま年率として与えると**拒否になる**（＝写像してはならない） | 否定形 |
| 費用計算への非接続 | `CostCalculator` / `TradingAssumptions` / `BacktestCostModel` の公開面に借株料の入口が無い | 否定形 |
| フェイルクローズ | 料率 `null`・文脈 `null` は拒否（既存テストを維持） | 否定形 |

**変異検査**（push 前に自分で実施する）:

1. 一次ゲート（`ShortPermit == false` の分岐）を外す → 落ちること。
2. `BorrowUnavailable` のクラス分類を C へ変える → 落ちること。
3. 20% 閾値判定を削除する → 落ちること。

## 計画書との差異

- 差異: **なし**（決定3 改訂の 4 項目をそのまま実装する）。
- ただし**計画へ環流する点が 2 つある**（実装は計画どおりのまま・下記「未決事項」）。

## 未決事項

1. **`ShortFeeRate` の単位**（年率 1.5% か否か）。確定するまで費用計算へ流さない。
   加えて、単位が確定しない限り `BorrowRateAnnual` を供給できず、**フェイルクローズにより空売りは
   全件拒否のまま**である（＝一次ゲートを実地で観測できない）。**環流の対象**。
2. 決定3 改訂は「一律料率であれば 20% の閾値は永久に超えない」と述べるが、**これは単位が「%」である
   という読みを前提としている**。`1.5` が比率（150%）なら閾値は**全銘柄で発火**し、記述と正反対の
   結果になる。単位の確定は「発火しない」という記述の前提でもある。**環流の対象**。
3. 借株照会の供給元（実弾ヘッダでの `TrdGetMarginRatio`・30 秒 10 回のレート制限・キャッシュ）は
   本 PR の範囲外（#331 / #342 系）。
