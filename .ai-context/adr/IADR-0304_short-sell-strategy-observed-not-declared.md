---
title: IADR-0304 「空売りを含む戦略か」は申告ではなく走行の観測から導き、申告する引数を公開面から消す
type: impl-adr
status: Accepted
related_ids: [FR-15, FR-20, ADR-0016, IADR-0089, IADR-0281, IADR-0139]
author: claude (Claude Code)
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
related_specs:
  - ../specs/20260904_388_short-sell-strategy-observation.md
---

# IADR-0304: 「空売りを含む戦略か」は申告ではなく走行の観測から導き、申告する引数を公開面から消す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

## 起点・関連

- 関連する計画書 ID: FR-15 / FR-20 / ADR-0016 決定 8・決定 14 / ADR-0008 / 06_daytrading-review §4
- 関連する実装仕様書: [20260904_388_short-sell-strategy-observation](../specs/20260904_388_short-sell-strategy-observation.md)
- 起点 issue: [#388](https://github.com/endazon/ai-stock-trading/issues/388)
- 関連 IADR:
  [IADR-0089](./IADR-0089_backtest-verdict-supply.md)（バックテスト verdict をイベント射影で Risk の段階別実績へ供給する。本件の供給経路の手本）、
  [IADR-0281](./IADR-0281_short-sell-release-verdict-on-stage-gate-approval-ledger.md)（verdict を段階ゲートの承認記録へ相乗りさせ、`IncludesShortSelling` / `StrategyId` を契約へ足した。**本 IADR はその決定3 を部分的に強化する**）、
  [IADR-0139](./IADR-0139_stage-product-type-enforcement.md)（段階別の商品種別強制・フェイルクローズの設計）
- 環流: planning#534（「空売りを含む戦略」の判定方法が計画に無いこと）

## コンテキストと課題

### #388 の主要部分は着手時点で既にマージ済みだった

着手前に現物を実測した。#388 の「やること」3 点のうち **2 点は PR [#641](https://github.com/endazon/ai-stock-trading/pull/641)
（IADR-0281）で実装済み**であり、`develop` に載っている。

- `BacktestEvaluated` は末尾へ `IncludesShortSelling` / `StrategyId` の 2 項を持つ（契約は既に変更済み）
- `BacktestEvaluatedProjectionHandler` が `StagePerformance` へ射影し、
  `StagePerformance.ShortSellStrategyBacktestPassed`（`BacktestPassed && BacktestIncludesShortSelling`）が
  `StageGateService.CurrentShortSellRelease()` から `StageProductPolicy.StageReleaseContext` へ渡る
- 受け入れ基準の否定形 2 本もテストで固定済み
  （`空売りを含む戦略のStage0再充足が無ければequityが足りても解禁されない` /
  `空売りを含まない戦略のStage0合格では解禁されない`）

**したがって本 IADR は #388 の再実装ではない。** 供給経路の**先頭に残っていた 1 つの穴**を塞ぐ判断を記録する。

### 残っていた穴 —— 「含む」は誰も判定しておらず、申告されているだけである

`BacktestEvaluatedFactory.From(..., bool includesShortSelling, ...)` は**呼び出し元が渡す真偽値**であった。
XML コメントは「Stage0Decision は取引可能な商品種別を保持しないため、呼び出し元（発行ホスト）が渡す」と
述べるが、**渡された値が実際のバックテストと一致することは何も保証していない。**

#388 の受け入れ基準は「**「空売りを含む戦略か」が判定でき**」である。現状は**判定ではなく申告**であった。
帰結として、発行ホスト（[#688](https://github.com/endazon/ai-stock-trading/issues/688) で新設予定）が `true` を
渡し違えれば、**一度も空売りを行っていない戦略の Stage 0 合格で実弾の空売りが解禁され得る。**
#388 が「最重要」とした否定形を、**呼び出し元の正直さだけで守っている**状態である。

### 計画は「含む」の判定方法を定めていない

ADR-0016 決定 14 は「**空売りを含む戦略で** Stage 0 の 7 条件を再度満たす」と書き、
06_daytrading-review §4 の段階表も同じ表現を繰り返すだけである。次の 2 つの読みがあり得る。

| 読み | 意味 | 帰結 |
| --- | --- | --- |
| **(a) 申告** | 戦略が「空売りを行い得る」と名乗っていれば「含む」 | 空売りを一度もしなかった走行でも解禁され得る |
| **(b) 観測** | 検証した走行で実際に空売り建玉を持っていたなら「含む」 | 空売りの費用・DD を実際に通した走行だけが解禁の根拠になる |

**計画はどちらとも書いていない。** 発明せず、**保守的な側（b）**を採り、計画側へ環流する
（CLAUDE.md「計画書の誤り・不足・新たな制約は planning へ issue で起票する」）。

## 決定

### 決定1: `IncludesShortSelling` は走行の約定列から観測し、申告する引数を公開面から消す

`BacktestEvaluatedFactory.From` の `bool includesShortSelling` 引数を削除し、代わりに
`BacktestRun run` を受け取って `ShortSellingObservation.Includes(run.Fills)` で導出する。

**引数を「無視する」のではなく「消す」ことが本決定の実体である。** 真偽値の口を残したまま
内部で観測値を優先しても、後から誰かが「呼び出し元の申告を尊重する」分岐を戻せる。
**申告できる口が無いことを構造で固定する**（テスト `空売りを含むと申告できる引数が公開面に存在しない` が
`From` のパラメータ型を反射で走査し、`bool` が生えたら赤くなる。母集合が空だと否定形が真空的に
成立するため、`BacktestRun` を受け取っていることを対照として併せて見る）。

観測の定義は「約定を時系列に畳んだとき、いずれかの `(銘柄, 市場)` で**累計建玉が一度でも負になった**」
である。バックテストは建玉ゼロから始まる（`BacktestSimulator`）ため、累計が負＝売り建てである。

- **銘柄・市場ごとに畳む。** ある銘柄の買い建玉が別銘柄の売り建玉を打ち消してはならない。
  同一ティッカーが複数市場に存在し得るため市場も鍵に含める。
- **売り注文の有無ではなく売り建玉の有無を見る。** 買い建玉の手仕舞い（売り）を空売りと読まない。
- **ゼロ跨ぎの反転は含む。** 買い建玉 +10 に対する −15 は、跨いだ先が売り建てである
  （[IADR-0038](./IADR-0038_order-decomposition-position-effect.md) の符号付きゼロ跨ぎ分割と同じ読み）。

### 決定2: 未約定は「含まない」へ倒す

空売り注文を出したが約定しなかった走行（`BacktestRun.UnfilledOrderCount` に計上され、
約定列には現れない）は `false` になる。

**保守的な側である。** 約定していない以上、その走行は借株料もショート側のドローダウンも
検証していない —— 決定 14 が求める「空売りを**含む戦略で**の再充足」を満たしていない。
計画が判定方法を定めていない以上、**解禁しない側**を選ぶ。

### 決定3: 走行が `null` なら例外にする（`false` へ倒さない）

`From` は `ArgumentNullException.ThrowIfNull(run)` で落とす。

**「観測できなかった」を「空売りを含まない」と読まない。** `false` へ倒すと、走行を渡し忘れた
呼び出しが**静かに合格 verdict を作る**。ここでの `false` は安全側に見えて、実際には
「解禁の判定材料が欠けたまま verdict が発行される」経路であり、
**欠落は fail-closed ではなく fail-loud にする**。

### 決定4: 発注審査への結線は行わない（IADR-0281 決定6 の据え置きを維持する）

`StageGateService.CurrentShortSellRelease()` は今も production の呼び出し元を持たない
（`OrderScreeningService` は `StageProductPolicy.StageReleaseContext` を渡さず、`null` ＝空売りは開かない）。

IADR-0281 決定6 は「借株照会・維持率の供給（[#417](https://github.com/endazon/ai-stock-trading/issues/417) /
[#419](https://github.com/endazon/ai-stock-trading/issues/419)）が無い状態で『解禁の材料が揃った』と
見える配線を先に作らない」として据え置いた。**その前提は今も未達である**（実測）。

| 供給元 | 実測 | 帰結 |
| --- | --- | --- |
| 維持率 | `Program.cs` は `IMaintenanceMarginSnapshotSource` に `UnavailableMaintenanceMarginSnapshotSource` を登録している（常に `null`） | 維持率の供給は無い |
| 借株照会 | `OrderScreeningService` の注記が「借株照会の供給元が無いため空売り文脈（`ShortSellOrderContext`）は今も組めない」と明記 | 借株可否・料率の供給は無い |
| 情報源フィンガープリント | `IShortSellReleaseSource` の実装・登録は 0 件（`borrow=none;margin=none`） | 経路の変更を検知できる状態にない |

**#417 / #419 の issue は close 済みだが、供給アダプタは入っていない**（両 issue はそれぞれ
一次ゲートの移設と強制買戻しの事後推定であり、料率・維持率の供給そのものではない）。
いま結線すると、**`borrow=none;margin=none` のまま verdict を有効にできる経路だけが生える。**

### 決定5: 契約（`BacktestEvaluated`）は 1 バイトも変更しない

#388 が求めた 2 項は IADR-0281 で既に載っている。したがって
`event-schemas.baseline.json` の再生成も、AuditService の全数レジストリの追随も不要である
（実測: `dotnet test` で監査側のイベント網羅テストは緑のまま）。

## 影響

- **実弾は解禁しない。** `LiveTradingGate` の閂 0〜4 に差分は無い（`git diff` に同ファイルは現れない）。
- 変更は `BacktestService` に閉じる（Domain へ純関数 1 本、`Features/.../BacktestEvaluatedFactory` の引数 1 本）。
  `RiskManagementService` / `AuditService` / `Shared.Contracts` は無改修。
- **本経路が実際に verdict を運ぶのは Stage 0 判定が実効化してから**である。本番戦略
  （`IBacktestStrategy` 実装）が計画にも実装にも存在せず（環流 planning#533）、米国株の日足 OHLC
  履歴源の未確認 2 点が残り（[#382](https://github.com/endazon/ai-stock-trading/issues/382)）、
  `BacktestEvaluated` を発行するホストも無い（[#688](https://github.com/endazon/ai-stock-trading/issues/688)）。

## 検討した代替案

| 案 | 採否 | 理由 |
| --- | --- | --- |
| 真偽値の申告を残し、観測値と食い違ったら例外にする | ❌ | 申告する口が残る。**口があるかぎり、後から「申告を尊重する」分岐へ戻せる。** 消すほうが構造で守れる |
| 「空売りを行い得る戦略か」を `IBacktestStrategy` に宣言させる（案 a） | ❌ | 計画が定めていない読みのうち**緩い側**である。加えて宣言は実装者の自己申告であり、申告と走行の乖離を検知する手段が無い |
| `BacktestRun` ではなく `IReadOnlyList<BacktestFill>` を受け取る | ❌ | 約定列だけを別途組み立てて渡せる＝実質的に申告に戻る。走行そのものを渡させるほうが偽装しにくい |
| ついでに最大 DD も `BacktestRun.Metrics` から導く | ❌ | IADR-0089 が「発行側の契約」（コメント）で担保している同種の穴だが、**#388 の範囲外**である。混ぜると本 PR の否定形の主張が薄まる |
| `OrderScreeningService` へ結線する | ❌ | 決定4 のとおり。IADR-0281 決定6 の前提が未達であることを実測で確認した |

## 残余リスク

- **観測は「この走行で空売りをしたか」であり、「この戦略が空売りを扱えるか」ではない。**
  空売りを扱える戦略でも、検証期間にたまたま売り建てが 1 件も成立しなければ `false` になる。
  **解禁しない側に倒れる**ため安全だが、運用者から見ると「空売り戦略なのに解禁条件が満たされない」
  という分かりにくい状態になり得る。計画の裁定（planning#534）が (a) を採るなら本決定は差し替える。
- **判定方法が計画に無いまま実装が先行している。** 環流 planning#534 の回答が届くまでは、
  本決定は「計画に書いていないことを保守側で埋めた」実装判断である。
- 決定4 の据え置きにより、**verdict は今も発注審査へ届かない。** これは #388 の残件であり、
  借株照会・維持率の供給が入るまで解けない（`ShortFeeRate` の単位確定＝計画 ADR-0026 の PoC 項目 9 が
  連鎖の起点である）。
