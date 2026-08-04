---
title: 作業仕様書 — リスク統制コアの再実装（第 1 段階: 金額系上限 3 値の equity 割合化・既定値の計画同期／第 3 段階: 3 点セットの完成と最終化）
type: work
status: review
related_ids: [FR-10, FR-17, FR-19, FR-20, UC-06, ADR-0003, ADR-0009, ADR-0016, ADR-0018, IADR-0130]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0018_risk-defaults-sync-and-stage0-dd.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md
related_specs:
  - ../adr/IADR-0130_equity-ratio-risk-limits.md
  - ../adr/IADR-0127_plan-conformance-known-deviation-registry.md
  - ../functional/FR-10_risk-controls.md
  - ../tests/FR-10_risk-controls-tests.md
  - ../tests/README.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: リスク統制コアの再実装（#329・第 1 段階）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（リスク統制）／ FR-17（全体前提条件の既定値）／ FR-19（取引ガード）・FR-20（段階ゲート）は境界のみ
- ユースケース（UC）: **UC-06**（統制の設定変更・緊急停止）
- 画面（SC）: SC-02（リスク設定）・SC-03（統制状態参照）— 表示値の意味が変わるため #340 へ申し送り
- 関連 ADR: **ADR-0018**（既定値の確定単一値）・**ADR-0016**（空売り統制・保有建玉数 3）・ADR-0003（AI 判断のガードレール）・ADR-0009（pause/resume・手仕舞い不停止）
- 実装 ADR: [IADR-0130](../adr/IADR-0130_equity-ratio-risk-limits.md)（本作業の実装方針）／ [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)（計画適合の既知逸脱レジストリ）
- 起点 issue: [#329](https://github.com/endazon/ai-stock-trading/issues/329)（親: [#344](https://github.com/endazon/ai-stock-trading/issues/344)）
- 計画書リンク: [02_requirements FR-10](../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md) ／ [05_trading-assumptions §5](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md)

## 目的・背景

計画大改定（project-planning#144）で FR-10 が大幅に拡張・確定された。金額で表す統制上限は
**固定額ではなく equity（自己資金）に対する割合で保持し、資金を増減した場合は各上限値が比例的に
調整される**ことが確定し（利用者委任に基づく決定 2026-08-02・project-planning#61）、既定値も
確定単一値へ同期された（[ADR-0018](../../planning/projects/ai-stock-trading/07_adr/ADR-0018_risk-defaults-sync-and-stage0-dd.md)）。

現行実装はこれに追随しておらず、[IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)
の既知逸脱レジストリ（`KnownPlanDeviations`）に **#329 担当として 6 件**が登録されている。

## 段階分割

issue #329 の範囲は広く、統制の意味を変える変更（金額系の基準の入れ替え）と、新しい統制の追加
（空売り 8 規則・拒否理由 7 種）を 1 つの PR に混ぜると、落ちたテストが「基準の入れ替えのせいか
新規統制のせいか」を切り分けられない（[IADR-0126](../adr/IADR-0126_reimplementation-sequencing-and-pr-granularity.md)
の PR 粒度方針）。よって 3 段階に割る。

| 段階 | 範囲 | 解消する逸脱 |
| --- | --- | --- |
| **第 1 段階（本書）** | 仕様書一式＋IADR-0130／金額系上限 3 値の equity 割合化・既定値の計画同期・決済注文の日次枠除外 | `Capital.Initial` / `RiskLimits.MaxOrderAmount` / `RiskLimits.MaxDailyOrderAmount` / `RiskLimits.LosingStreakThreshold` の **4 件** |
| 第 2 段階 | 空売り専用統制 8 規則（`ShortSellingLimits`）・拒否理由 7 種（クラス A）・3 統制の優先順位 | `ShortSell.Limits` / `RejectionReason.ShortSellReasons` の 2 件 |
| **第 3 段階（本書・後述 §第 3 段階）** | 3 点セットテストの完成・機能仕様書 / テスト仕様書の最終化・計画への環流の補完 | （なし。網羅の完成） |

本書は**第 1 段階**の作業仕様として起草した。第 2 段階は独立した
[作業仕様書](./20260804_329_short-selling-controls.md)を持つ。**第 3 段階は実装の追加を伴わない
（テスト 3 件と文書のみ）**ため、新たな作業仕様書を起こさず本書へ追補する（後述「第 3 段階（最終化）」）。

## 対象範囲

### 対象（第 1 段階）

1. **金額系統制上限 3 値**（計画 §5・INDEX 決定 40）
   - 1 注文あたりの発注金額上限: **equity の 25%**
   - 1 日あたりの発注金額上限: **equity の 150%/日**。**新規建て（`PositionEffect.Open`）の発注代金合計で判定し、
     手仕舞い（決済）注文は算入しない**（ゲートと**カウンタの両方**。#302 の裁定と整合）
   - 保有建玉数上限: **3**（値は現行と一致。用語を「保有銘柄数」から**「保有建玉数」**へ統一する）
2. **既定値の確定単一値**（ADR-0018）: 日次損失 2%・1 取引リスク 1%・最大 DD 10%・**連敗 5** でサイズ半減
3. **初期投入資金**: **USD 3,000**（旧 JPY 100,000）
4. **複数上限の競合は常に厳しい方が効く**ことの明文化とテスト固定
5. `TradingDefaults` の**全既定値**を計画の確定単一値で固定するテスト（#306 再発防止・issue #329 の必須要請）
6. 上記に伴う消費側（発注ガード・サイジング文脈・統制状態ビュー・SIMULATE プロファイル）の追随

### 対象外（担当を明記）

| 項目 | 担当 |
| --- | --- |
| 空売り専用統制 8 規則・拒否理由 7 種・3 統制の優先順位 | #329 第 2 段階 |
| 維持率割れによる自動縮小（閾値+5pt 回復・必要証拠金降順） | [#330](https://github.com/endazon/ai-stock-trading/issues/330) |
| 商品種別の 3 値化（現物 / 信用買い / 空売り） | [#332](https://github.com/endazon/ai-stock-trading/issues/332) |
| Stage 2 発注可能額の総資金比 30% 化・Stage 0 合格 DD≤10% | [#333](https://github.com/endazon/ai-stock-trading/issues/333) |
| 発注先（Broker Provider）の 2 軸分離 | [#334](https://github.com/endazon/ai-stock-trading/issues/334) |
| SC-02 / SC-03 の表示（equity 比の表示・実額の併記） | [#340](https://github.com/endazon/ai-stock-trading/issues/340) |
| **判定通貨そのものの USD への移行**（`MarketCurrency.Base` の反転・IADR-0107 の改定） | 本書「未決事項」§1（未起票） |
| 旧設定行（JSON 直列化）の移行 | [#346](https://github.com/endazon/ai-stock-trading/issues/346)（切替計画）へ申し送り |

### #342（moomoo PoC）への依存の扱い

依存グラフ上、#329 は [#342](https://github.com/endazon/ai-stock-trading/issues/342)（moomoo PoC 6 項目・
2026-08-31 期限）に依存する（[ADR-0019](../../planning/projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md)）。
**利用者判断により、PoC の完了を待たずに本作業へ着手する。** 本段階が扱うのは
**計画で確定済みの値と、その保持形式（equity 比）**だけであり、PoC が確認するのは
「その値をブローカー側で実現できるか」（借株料の照会可否・維持率の実測・SIMULATE の建玉挙動）である。
**PoC の結果で前提が変わった場合は、本書の追補と `KnownPlanDeviations` への再登録で補正する**
（判断と補正条件は [IADR-0130](../adr/IADR-0130_equity-ratio-risk-limits.md) 決定 5 に記録した）。

PoC 結果が本段階の成果物を覆し得る具体的な条件は次の 2 つに限られる。

1. **equity の取得経路**が moomoo から前営業日終値時点で取れない（＝日中値しか取れない）場合 →
   equity の as-of 定義（§設計 3）を見直す
2. **USD 建て equity の実額**が口座上 $3,000 にならない（増資が別建てになる等）場合 → `InitialEquityUsd` の見直し

いずれも**割合で保持するという設計自体は変わらない**（むしろ割合で持つほど実額のずれに強い）。

## 設計

### 1. 金額系上限は「比率」で保持し、判定時に equity から解決する

`RiskLimitSettings` の金額 2 項目を**固定額から比率へ入れ替える**。名前も実体に合わせて改める
（`MaxOrderAmount` → `MaxOrderAmountRatio`）。名前を据え置いたまま中身を比率にすると、
`35_000` と `0.25` を取り違えた実装が型検査を素通りする（統制で最も危険な誤りの型）。

```csharp
// FR-10, 05_trading-assumptions §5: equity 比で保持し、判定時に equity から解決する
public required decimal MaxOrderAmountRatio { get; init; }          // 0.25
public required decimal MaxDailyOrderAmountRatio { get; init; }     // 1.50（/日）

public decimal MaxOrderAmountFor(decimal equity) => equity * MaxOrderAmountRatio;
public decimal MaxDailyOrderAmountFor(decimal equity) => equity * MaxDailyOrderAmountRatio;
```

解決（比率→金額）は判定・サイジング・表示のいずれでも**この 2 メソッドだけ**を通す。
呼び出し側で `equity * 0.25m` と書くことを許すと、equity の定義（どの時点の値か）が呼び出し側ごとにぶれる。

### 2. equity の定義と取得経路

計画 §5 注記は「判定に用いる equity は**前営業日終値時点**の USD 評価額」と定める。理由は
「日中の評価損益で上限を動かすと、含み益で上限が緩み含み損で締まるという逆方向の作用が起きる」ためである。

実装には**既にこの意味の値が存在する**。`PortfolioSnapshot.Capital`（= `PortfolioState.Capital`）は
`初期資金 + 当日より前の実現損益` であり、**当日中は不変**（当日の実現・含みは `DailyRealizedPnl` /
`UnrealizedPnl` に分けて保持）。これは「前営業日終値時点の評価額」と同じ意味であり、
日次損失上限（2%）の判定基準として既に同じ役割を果たしている。

したがって **新しいポート・プロバイダは作らない**。取得経路は現行のまま以下である。

```
trade_fills（台帳）→ PortfolioProjection.Project(fills, today, initialCapital)
                   → PortfolioState.Capital（= equity・当日不変）
                   → PortfolioSnapshot.Capital → RiskEvaluator / SizingContextService / RiskStatusService
```

`initialCapital` の既定は `TradingDefaults.InitialCapital`。SIMULATE プロファイル有効時のみ
ホストがシミュレータ残高相当を注入する（現行どおり・IADR-0108）。

### 3. 初期投入資金と通貨の扱い

計画 §5 は初期投入資金を **$3,000（約 491,000 円。1 USD ≈ 163.7 円）** と定め、§3 は
**判定の基準通貨を USD**、表示を JPY と定める。一方、現行実装のパイプライン（注文意図・台帳・
損益集計）は `MarketCurrency.Base = JPY` を前提に組まれている（[IADR-0107](../adr/IADR-0107_base-currency-conversion.md)。
当時の計画 §3「基準通貨 = JPY」に従ったもの）。

本段階では**判定通貨そのものの移行は行わない**（対象外・未決事項 §1）。代わりに、

- **計画の確定値そのもの**（＝ USD 3,000）を `TradingDefaults.InitialEquityUsd` に持ち、通貨を
  `EquityCurrency = Currency.Usd` として明示する。計画適合検査はこの 2 つから `USD 3000` を機械抽出する
- 基準通貨（円）建てのパイプラインへは、**計画 §5 が明記する参照レート 163.7 円/USD** で 1 点換算した
  `InitialCapital`（= 491,100 円）を供給する

**統制の実効は比率であり、equity と注文金額を同一通貨で評価する限り、どちらの通貨で評価しても
判定結果は変わらない**（プロパティベーステストで固定する）。この不変性が、通貨移行を第 1 段階から
切り離せる根拠である。

### 4. 決済注文は日次枠を消費しない（ゲートとカウンタの両方）

計画 §5 は 1 日あたりの上限を「**新規建ての発注代金の合計**で判定し、手仕舞い（決済）注文は
算入しない」と定めた（#302 の裁定・project-planning#61）。現行実装は**ゲート側だけ**が
`isEntry` で Close を除外し、**カウンタ側**（`PortfolioProjection` の `orderedToday`）は
全約定を無条件に加算していた。大きな建玉の決済が当日の新規建て枠を枯渇させ、
「危険なら手仕舞いやすくする」という統制の意図と逆向きの誘因を生む。

`orderedToday` の加算を `PositionEffect.Open` の約定に限定して非対称を解消する。
これは ADR-0009「手仕舞い・損切りは止めない」を金額系上限でも壊さないための必須条件である。

### 5. 複数上限の競合は常に厳しい方が効く

計画 FR-10 は「同一の注文に複数の上限が掛かる場合は常に厳しい方が効く」と定める。実装上、これは
**新しい仕組みを足さずに**次の 2 つの既存構造で成立している。本段階では構造を変えず、
不変条件としてテストで固定する。

| 競合 | 効かせ方 |
| --- | --- |
| 1 注文 25% vs 1 取引リスク 1%（ATR 連動サイジング） | `PositionSizer.CalculateCappedQuantity` が**両基準の株数の min** を採る |
| 1 注文 25% vs 段階資金上限 vs 日次枠 vs 保有建玉数 | `RiskEvaluator` が**すべての違反を列挙**する（1 件でも該当すれば拒否＝ AND） |

### 6. SIMULATE プロファイル（IADR-0108）の縮退

比率は**スケール不変**であるため、金額系を比率で保持した時点で IADR-0108 の「金額系のみ 1,700 倍へ
スケールする」機構は不要になる（基準資金を差し替えれば上限は自動的に比例する）。
`SimulatorTradingDefaults.CreateRiskLimits()` と `ScaleFactor` を削除し、プロファイルは
**基準資金とペーパー段階の資金上限だけ**を差し替える。実弾段階（Stage 2/3）を触らない不変条件は維持する。

### 変更するファイル

| 層 | ファイル | 変更 |
| --- | --- | --- |
| Domain | `TradingDefaults.cs` | 初期資金 USD 3,000・比率 2 値・連敗 5 |
| Domain | `RiskLimitSettings.cs` | 比率化・解決メソッド・用語（保有建玉数） |
| Domain | `RiskEvaluator.cs` | equity からの上限解決 |
| Domain | `PortfolioSnapshot.cs` | `Capital` の doc を equity（前営業日終値時点）として確定 |
| Domain | `SimulatorTradingDefaults.cs` | 金額スケールの削除 |
| Application | `PortfolioProjection.cs` | 日次発注累計を新規建てに限定 |
| Application | `SizingContextService.cs` / `RiskStatusService.cs` | equity からの上限解決 |
| Application | `SimulatorProfileRiskSettingsStore.cs` | 金額系の上書きを削除 |
| TradeDecision | `TradeDecisionService.cs` / `TradeDecisionPromptBuilder.cs` / `PlaceholderProviders.cs` | equity からの上限解決 |
| Tests | `PlanConformance.Tests/ActualDefaults.cs` | 抽出の追随（USD 額・比率・日次比率） |
| Tests | `PlanConformance.Tests/KnownPlanDeviations.cs` | **逸脱 4 行の削除** |

## 受け入れ基準

計画書（02_requirements 受け入れ基準）から本段階が満たすものを転記する。

- [x] **1 注文あたり・1 日あたりの発注金額上限、保有建玉数上限を超える注文が発注前に拒否され、理由が記録される**
      （1 日あたりの判定に決済注文が算入されない）
- [x] **自己資金を増減しても金額系の統制上限が固定額のまま据え置かれない**（割合から再計算される）
- [x] リスク上限を超える判断を生成 AI が出力した場合に、発注が拒否されログと通知が残る（既存経路の維持）
- [x] 日次損失上限（2%）・1 取引リスク（1%）・最大 DD（10%）・連敗（5）の既定値が計画の確定単一値と一致する
- [x] 同一の注文に複数の上限が掛かる場合、常に厳しい方が効く
- [x] 金額系上限の適用が手仕舞い（Close）・損切りを止めない（ADR-0009）
- [x] 空売り専用統制・拒否理由 7 種（＋ `StopOrderRequired`）（**第 2 段階で完了**。
      [作業仕様書 第 2 段階](./20260804_329_short-selling-controls.md) の受け入れ基準を正とする）
- [x] 3 統制（kill switch ＞ 日次損失ロックアウト ＞ 一時停止）の優先順位の明示（**第 2 段階で完了**。
      実装は `RiskStatusView.ActiveControl` に既にあり、8 通り全数の 3 点セット化で仕上げた）
- [x] 統制系 FR-10 の 3 点セット（境界値・プロパティベース・否定形）が揃っている（**第 3 段階で完了**。
      [テスト仕様書](../tests/FR-10_risk-controls-tests.md)「第 3 段階で埋めた穴」）
- [x] 計画書の誤り・不足を環流した（**第 3 段階で完了**。拒否理由コードの不足＋空売り比率 50% の
      構造的含意の 2 件。いずれも起草のみで送付は未実施）

## テスト方針

[テスト戦略](../tests/README.md) §2 の 3 点セット（境界値テーブル・プロパティベース・否定形）で写像する。
詳細は [テスト仕様書 FR-10](../tests/FR-10_risk-controls-tests.md)。

| 種別 | 本段階で追加するもの |
| --- | --- |
| 既定値の固定 | `TradingDefaults` の**全既定値**を確定単一値で固定（#306 再発防止） |
| 境界値 | 1 注文 25%（直下 / 一致 / 直上）・日次 150%（累計での直下 / 一致 / 直上）・保有建玉 2 / 3 / 4 |
| プロパティベース | 「equity を k 倍すると上限も k 倍になる」「equity と金額を同一レートで換算しても判定は変わらない」「複数上限では常に厳しい方が効く」 |
| 否定形 | 「決済注文は日次枠を消費しない（ゲート・カウンタの両方）」「金額上限に達していても手仕舞い・損切りは通る」「上限は設定から引かれ、注文側の値で上書きできない」 |

閾値はマジックナンバーで書かず統制設定から引く（テスト戦略 §2）。設定値の正しさは
`AiStockTrading.PlanConformance.Tests` が計画書と突き合わせて別途保証する。

### 計画適合検査の赤→緑（IADR-0127 の機械的証明）

値を計画に一致させると `KnownPlanDeviations` の登録が陳腐化し、**検査 3（登録済み逸脱は実際に逸脱している）**
と**検査 4（登録済み逸脱の現行値は実装の実際値と一致する）**が失敗する。削除して初めて緑になる。
**削除前に赤・削除後に緑**であることを実測して記録する（本書「検証結果」）。

## 計画書との差異

- 差異: **あり**（1 件・意図的な範囲外）
  - **判定通貨**: 計画 §3 は判定の基準通貨を USD と定めるが、実装のパイプラインは JPY 基準のままである
    （IADR-0107）。本段階は equity の**権威値を USD で保持**し、パイプラインへは計画記載の参照レートで
    1 点換算して供給することで、**統制の実効（比率）を計画どおりに保ちつつ**通貨移行を切り離した。
    移行そのものは台帳・報告・FX 源に跨るため未決事項 §1 として監査判断を仰ぐ。

## 未決事項

1. **判定通貨の USD への移行**（`MarketCurrency.Base = Jpy` → `Usd`・IADR-0107 の改定）。
   計画 §3（利用者決定 2026-07-31・project-planning#84）は判定を USD で行うと定め、§2 は決済方式を
   **外貨決済**（増資時に一括 USD 両替・以後の取引は USD で完結）と定めている。実装の移行は
   注文意図の同伴レート・台帳（`PriceInBase`）・報告書の円換算・FX 源（FRED `DEXJPUS` は
   JPY/USD であり逆数が要る）に跨り、#338 / #339 / #346 と範囲が重なる。
   **本 issue では扱わず、担当 issue の起票要否を監査判断に委ねる。**
   なお本段階の実装は比率であり、移行時に**値の書き換えを伴わない**（equity の通貨が変わるだけ）。
2. **旧設定行の移行**: `RiskLimitSettings` は JSON で永続化されており（`RiskSettingsSerialization`）、
   プロパティ名の変更で既存行が読めなくなる（`required` のため復元時に失敗する）。
   再実装版への切替（#346）で扱う旧データの取り扱いに含める必要がある。
3. **SC-02 / SC-03 の表示と API 契約の破壊的変更**（**[#362](https://github.com/endazon/ai-stock-trading/issues/362) として起票済み**。
   「リスク上限の設定画面を equity 割合の入力へ作り直す」）: `PUT /risk-controls/settings/limits` は
   `RiskLimitSettings` をそのまま受けるため、要求本文のフィールド名が
   `maxOrderAmount` → `maxOrderAmountRatio`・`maxDailyOrderAmount` → `maxDailyOrderAmountRatio` へ変わる。
   **現行の SC-02 画面は旧名で送るため、リスク上限の保存が 400 で拒否される**。
   本 PR では画面を追随させない。理由は次の 2 点である。
   - 画面の追随は #340 の担当であり、上限が比率になった以上、画面は「比率」と「現在 equity での実額」を
     併記する形へ**作り直す**必要がある（単なるフィールド名の付け替えでは、利用者が
     `35000` を比率欄へ入力して equity の 35,000 倍を上限に設定できてしまう）
   - 追随しない状態は**安全側に倒れる**（保存が拒否されるだけで、現行の統制値は変わらない）。
     フィールド名だけ合わせて意味を合わせない方が危険である
4. **画面の表示値**: SC-03（統制状態）は `MaxDailyOrderAmount` を equity から解決した実額で受け取るため
   表示は従来どおり成立する（読み取り専用のため破壊的変更はない）。表示の作り直し自体は #340 / #362。
5. **Stage 2 の発注可能額との併用**: 計画 §5 注記は「Stage 2 では Stage の発注可能額（総資金の 30%＝$900）が
   先に効く」と述べる。Stage 側の比率化は #333 の担当であり、本段階では段階資金上限を触らない
   （現行の固定額 35,000 のまま＝`KnownPlanDeviations` に #333 担当で登録済み）。
6. **空売り比率 50% の分母**（第 3 段階で環流）: 決定9 を文字どおり実装すると
   `空売り建玉 ≦ ロング建玉総額` と等価になり、**ロング建玉が 0 件では空売りを開始できない**。
   Stage 1（SIMULATE）で空売り単独の検証ができないという運用上の含意があるため、計画側の裁定を仰ぐ
   （[環流文書](../../feedback/20260804_adr0016-short-ratio-denominator.md)。案 A 文字どおり維持 /
   案 B 建玉 0 件時の例外 / 案 C 分母を equity へ）。現行実装は案 A であり、T-10-156 / T-10-171 が固定している。
7. **保有建玉数を数える粒度**: 台帳の建玉キーは `(銘柄, 市場)` であり商品種別を含まないため、
   同一銘柄の現物と信用買いの併存は 1 件と数える。計画 §5 は「銘柄数で数えると上限が実効しない」と
   述べており、**商品種別の 3 値化（#332）と同時に建玉キーの見直しが要る**（用語だけを是正した第 1 段階の
   積み残し。値〔3〕は計画どおり）。

### 起票の要否を監査判断へ委ねるもの（第 3 段階時点の棚卸し）

| # | 未決事項 | 現状 | 起票の要否 |
| --- | --- | --- | --- |
| 1 | 判定通貨の USD 移行（IADR-0107 の改定） | 実装は比率のため統制の実効は計画どおり。記録上の不一致のみ残る | **要**（#338 / #339 / #346 と範囲が重なるため、独立 issue か既存 issue への追記かの判断を含む） |
| 2 | 強制買戻し（buy-in）の検知・通知・禁止リストの永続化 | 値と期間判定のみ実装（T-10-148 / T-10-165）。受信経路・永続化は無い | **要**（ADR-0016 決定4・決定14。実弾解禁前の疎通確認が前提） |
| 6 | 空売り比率 50% の分母 | 環流済み（送付は未実施）。実装は案 A | 計画側の裁定が先。裁定後に実装 issue の要否が決まる |
| 7 | 保有建玉数の粒度 | #332 の範囲に含めるのが自然 | #332 へ追記（新規起票は不要） |
| 3 / 4 | SC-02 の保存 400・SC-03 の表示 | [#362](https://github.com/endazon/ai-stock-trading/issues/362) 起票済み（#340 と併走） | 不要 |
| 2（旧設定行）・5 | 設定行の移行・Stage 側の比率化 | #346 / #333 の担当として明記済み | 不要 |

## 第 3 段階（最終化）

実装（本番コード）の変更は無い。**テスト 3 件の追加と、文書の最終化・環流の補完**のみである。

| 区分 | 内容 |
| --- | --- |
| テスト | 否定形 2 件（T-10-127 分割発注 / T-10-128 決済偽装）・プロパティ 1 件（T-10-156 比率の等価形）。詳細は[テスト仕様書](../tests/FR-10_risk-controls-tests.md)「第 3 段階で埋めた穴」 |
| 環流 | 空売り比率 50% の構造的含意を新しい環流文書へ起草（[20260804_adr0016-short-ratio-denominator](../../feedback/20260804_adr0016-short-ratio-denominator.md)）。既存の環流文書（拒否理由コードの不足）へ「送付は未実施」の注記と相互リンクを補った |
| 文書 | 機能仕様書・テスト仕様書を `approved` へ。本書の受け入れ基準・未決事項・変更履歴を最終化。IADR-0130 / IADR-0131 の相互参照を追記 |

## 検証結果

### 第 1 段階

| 検証 | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx` | 0 Warning / 0 Error |
| `dotnet test`（`Category!=Integration`） | 全 green（2,298 passed） |
| 計画適合の赤→緑 | 逸脱 4 行を残したまま実装を計画へ一致させると **検査 3・4 が失敗（赤）**、4 行削除で **緑**（実測） |
| `node scripts/check-test-traceability.js` | OK |
| `node scripts/check-coverage.js` | floor 62% 以上 |

### 第 3 段階（最終）

| 検証 | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx` | **0 Warning / 0 Error** |
| `dotnet test`（`Category!=Integration`） | **2,426 passed / 0 failed**（第 2 段階 2,418 から +8＝追加テスト 3 件・うち 1 件は 6 ケースの Theory） |
| `AiStockTrading.PlanConformance.Tests` | **6 passed / 0 failed**（#329 担当の既知逸脱 6 件はすべて解消済み） |
| `dotnet format --verify-no-changes` | 差分なし |
| `AiStockTrading.Architecture.Tests` | 4 passed |
| `node scripts/check-test-traceability.js` | OK（テスト 322 ファイル・起点 ID 25 種。FR-10 の機能仕様書・テスト仕様書は `approved`） |
| `node scripts/check-coverage.js` | 行カバレッジ **65.56%**（12,750/19,448 行）/ floor 62.00% |
| `node scripts/scripts.test.js` | 143 tests passed |
| `node scripts/check-banned-libraries.js` | OK |
| `node scripts/check-doc-links.js` | 破損 **20 件**（すべて既知の既存分。本段階の新規文書からの破損なし） |

## 変更履歴

| 日付 | 段階 | 内容 |
| --- | --- | --- |
| 2026-08-04 | 第 1 段階 | 本書と [IADR-0130](../adr/IADR-0130_equity-ratio-risk-limits.md) を作成。金額系上限 3 値の equity 割合化・既定値の計画同期・決済注文の日次枠除外（ゲートとカウンタの両方）・SIMULATE の金額スケール廃止。既知逸脱 4 行を削除（赤→緑を実測） |
| 2026-08-04 | 第 2 段階 | [別作業仕様書](./20260804_329_short-selling-controls.md)。空売り専用統制 8 規則・拒否理由 7 種＋`StopOrderRequired`・クラス分類・3 統制の優先順位。既知逸脱 2 行を削除し #329 担当 6 件がすべて解消 |
| 2026-08-04 | 第 3 段階（最終） | 3 点セットの穴埋め（否定形 2 件・プロパティ 1 件）。空売り比率 50% の構造的含意を計画へ環流（起草のみ・送付は未実施）。機能仕様書・テスト仕様書を `approved` へ。受け入れ基準・未決事項（起票要否の棚卸しを含む）・検証結果を最終化 |

## 関連仕様

- 実装 ADR: [IADR-0130](../adr/IADR-0130_equity-ratio-risk-limits.md)（equity 比の保持と解決）・
  [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)（空売り統制のフェイルクローズ・第 2 段階）・
  [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)・
  [IADR-0107](../adr/IADR-0107_base-currency-conversion.md)（基準通貨換算）・
  [IADR-0108](../adr/IADR-0108_simulator-risk-profile.md)（SIMULATE プロファイル）
- 計画への環流: [拒否理由コードの不足](../../feedback/20260804_adr0016-stop-order-rejection-reason.md)・
  [空売り比率 50% の構造的含意](../../feedback/20260804_adr0016-short-ratio-denominator.md)（いずれも送付は未実施）
- 機能仕様書: [FR-10 リスク統制](../functional/FR-10_risk-controls.md)
- テスト仕様書: [FR-10 リスク統制（再実装）](../tests/FR-10_risk-controls-tests.md)・
  [FR-10 リスクガードコア（再実装前）](../tests/FR-10_risk-guard-core-tests.md)
- データ仕様書: [リスク管理ドメインの集約](../data/risk-management-aggregates.md)
