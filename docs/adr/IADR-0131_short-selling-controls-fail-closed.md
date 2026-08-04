---
title: IADR-0131 空売り専用統制は「新規売り建て」を起点に判定し、外部照会が欠けたら通さない（フェイルクローズ）。拒否理由はクラス分類を実装の単一情報源として持つ
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-19, FR-20, UC-06, ADR-0003, ADR-0009, ADR-0016, ADR-0018, IADR-0130]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
---

# IADR-0131: 空売り専用統制は「新規売り建て」を起点に判定し、外部照会が欠けたら通さない。拒否理由はクラス分類を実装の単一情報源として持つ

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: endazon（利用者。#329 第 2 段階の実装方針として）

## 起点・関連

- 関連する計画書 ID: **FR-10**（リスク統制・空売り 8 規則・拒否理由 7 種）／ FR-19・FR-20（境界）／ UC-06 ／
  **ADR-0016**（空売りの段階解禁と専用統制）・ADR-0009（3 統制の優先順位・手仕舞い不停止）・ADR-0003 ／
  [05_trading-assumptions §5](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md)・
  [06_daytrading-review §4.1](../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md)（クラス A/B/C の定義）
- 関連する実装仕様書: [作業仕様書 20260804（#329 第 2 段階）](../specs/20260804_329_short-selling-controls.md)・
  [機能仕様書 FR-10](../functional/FR-10_risk-controls.md)・[テスト仕様書 FR-10](../tests/FR-10_risk-controls-tests.md)
- 関連 issue: [#329](https://github.com/endazon/ai-stock-trading/issues/329)（親 [#344](https://github.com/endazon/ai-stock-trading/issues/344)）・
  [#330](https://github.com/endazon/ai-stock-trading/issues/330)（維持率割れの自動縮小）・
  [#332](https://github.com/endazon/ai-stock-trading/issues/332)（商品種別の 3 値化）・
  [#342](https://github.com/endazon/ai-stock-trading/issues/342)（moomoo PoC・借株照会の可否）
- 先行 IADR: [IADR-0130](IADR-0130_equity-ratio-risk-limits.md)（equity 比の保持・第 1 段階）・
  [IADR-0004](IADR-0004_position-effect-entry-scoping.md)（建玉効果でエントリーを判定）・
  [IADR-0119](IADR-0119_decision-derived-position-effect.md)（保有なし・不明の売りは見送る）・
  [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)（既知逸脱レジストリ）

## コンテキストと課題

[ADR-0016](../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md) は空売りに
**8 つの専用統制**と**7 種の拒否理由**を課した。空売りが既存統制と決定的に異なるのは**損失に上限が無い**
ことであり、「損切りが機能すれば損失は限定される」という既存統制の前提が成り立たない。

第 1 段階（IADR-0130）で金額系の保持形式は確定した。第 2 段階で決めるべきことは 4 つある。

1. **空売りをどう識別するか**。計画は商品種別を「現物 / 信用買い / 空売り」の 3 値へ分けると定めたが、
   その 3 値化は #332 の担当であり、`ProductType` を先に割ると計画適合検査の既知逸脱（`ProductType.Values`・
   #332 担当）と衝突する
2. **外部照会（借株料・維持率・権利確定日）が得られないときにどう振る舞うか**。ADR-0016 決定3 は
   「発注前に借株料を照会できない場合、空売り自体を行わない」と定めるが、照会経路（moomoo）の実装は
   #342 の PoC 結果に依存し、現時点では存在しない
3. **拒否理由のクラス分類をどこに持つか**。7 種は**クラス A**であり、「統制違反 0 件」の計上対象
   （クラス C ＝ `BannedSymbol` / `ManipulativeOrderPattern` 限定）に混ぜてはならない
4. **逆指値（ストップ注文）の同時発注必須**（決定2(b)）に対応する拒否理由が、ADR-0016 決定10 の 7 種に無い

## 検討した選択肢

### 論点 A: 空売りの識別

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A-1 | `ProductType` へ `ShortSell` を追加して識別する | 計画の最終形だが **#332 の担当**であり、先取りすると既知逸脱レジストリが二重に動く |
| A-2 | 注文意図に「空売りフラグ」を足す | AI・上流が申告する値で統制の適用可否が決まる（ADR-0003 違反の経路。IADR-0119 と同じ誤り） |
| **A-3** | **`Side == Sell` かつ `PositionEffect == Open`（新規売り建て）で識別する** | 既存の値だけで一意に決まる。上流の申告に依存しない。#332 が 3 値化しても**識別規則は変わらない**（商品種別は別途ガードが見る） |

### 論点 B: 外部照会が得られないとき

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | 照会できないときは当該規則を「判定しない」（素通し） | ADR-0016 決定3 に**正面から反する**。年率 100% の銘柄でも通る穴が残る |
| B-2 | 照会ポート（`IBorrowQuoteProvider` 等）を先に作り、未実装なら例外にする | 例外は運用を止める。PoC 前に外部 IF の形を決め打ちする不利益もある |
| **B-3** | **判定の入力（`ShortSellOrderContext`）を任意引数とし、無ければ空売りを拒否する** | 統制として正しい縮退（照会できない＝空売りしない）。供給元の実装（#342/#332）と判定コアを切り離せる |

### 論点 C: 逆指値必須に対応する拒否理由

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C-1 | 規則を実装しない（計画に理由コードが無いため） | **逆指値なしの空売りが素通りする**。FR-10 本文が明示的に課した規則を落とすことになる |
| C-2 | 既存の 7 種のいずれか（例 `ShortSellDisabled`）で代用する | 監査ログの理由が実態と食い違う。原因究明が壊れる |
| **C-3** | **実装側で 1 種（`StopOrderRequired`）を新設し、クラス A とし、計画へ環流する** | 規則は塞がり、記録も正しい。コード名の追認だけが計画側の宿題として残る |

## 決定

### 決定 1: 空売りは「新規売り建て」で識別し、有効・無効は `ShortSellSettings.Enabled` で持つ（案 A-3）

`ShortSellEvaluator.IsShortEntry(intent)` ＝ `Side == Sell && PositionEffect == Open`。
売買方向だけでも建玉効果だけでも空売りは定まらない（**両方の組**が空売りを一意に定める）。

有効・無効は当面 `RiskManagementSettings.ShortSell.Enabled`（既定 **false**）で保持する。
`ProductType` の 3 値化（#332）が入った時点で、`Guard.EnabledProductTypes` への統合を検討する。
統合するまでの間、**空売り無効は 2 箇所（本フラグと商品種別ガード）で表現され得る**が、
いずれも「無効なら拒否」であり、緩む向きの重複ではない。

### 決定 2: 外部由来の入力は `ShortSellOrderContext` に集約し、**無ければ空売りを拒否する**（案 B-3）

```csharp
RiskEvaluator.Evaluate(intent, settings, snapshot, patternDetector, shortSellContext);
//                                                                  ↑ null なら BorrowUnavailable で拒否
```

ADR-0016 決定3 の「照会できない場合は空売り自体を行わない」を、**照会経路が無い場合**にも同じ向きで
適用する。借株料が `null`（照会不能）・`BorrowAvailable == false`（locate 失敗）・文脈そのものが
`null`（供給経路なし）は、いずれも `BorrowUnavailable` で拒否する。

**供給元（moomoo の借株照会・建玉射影・コーポレートアクション）は本 issue の範囲外**である。
決定3 の成否は #342 の PoC 項目3 が確認し、不成立なら空売りフラグを恒久的に無効とする。
判定コアだけを先に確定しても、既定の縮退が「空売りしない」である限り危険は生じない。

### 決定 3: 逆指値必須には拒否理由 `StopOrderRequired` を実装側で新設する（案 C-3）

FR-10 本文と ADR-0016 決定2(b) は「逆指値（ストップ注文）を建玉と同時に必ず発注する。未約定・未受理で
あれば建玉を持たない」と定めるが、決定10 の拒否理由 7 種に対応するコードが無い。実装は
`OrderIntent.StopLossPrice` の有無で判定し、欠けていれば `StopOrderRequired` で拒否する。
**クラス A**（統制の正常作動）とする。コード名の追認は計画へ環流する
（[feedback/20260804_adr0016-stop-order-rejection-reason.md](../../feedback/20260804_adr0016-stop-order-rejection-reason.md)）。

同じ理由で、**強制買戻し（buy-in）由来の 30 日禁止**（決定4）にも専用コードが無い。こちらは
`BorrowUnavailable`（借株需給の逼迫による借株不可）へ写像する。**`BannedSymbol` は用いない**——
市況由来の事象をクラス C（AI が禁止事項を犯そうとした件数）へ混入させると段階昇格ゲートが機能しなくなる
（決定10 が `$5` 未満の除外について明示的に禁じた誤りと同型である）。

### 決定 4: 判定できないものは「違反していない」ではなく「通さない」に倒す

計画が明示していない縮退のうち、本実装が決めたものは次の 2 つである。いずれも**安全側**へ倒した。

| 状況 | 決定 | 理由 |
| --- | --- | --- |
| 維持率が取得できない（`null`）のに**空売り建玉を保有している** | `MaintenanceMarginBreach` で拒否 | 「割れていないこと」を確認できないまま積み増さない。取得できないだけで統制を回避できてはならない |
| 空売りの**対象市場が米国株以外**（ADR-0016 決定13） | `ShortSellDisabled` で拒否 | 株価下限 $5.00 は **USD 建て**である。別市場を許すと円建て株価 ¥300 が「$5 超」として素通りし、統制がまるごと無効化される |

空売り建玉を 1 件も持たない場合の維持率 `null` は「維持率という概念が成立しない」として対象外とする。

### 決定 5: 空売り比率 50%（決定9）は**建玉総額に対する**比率として文字どおり実装する

`(既存の空売り建玉 + 当該注文) ≦ (建玉総額 + 当該注文) × 50%`。この式は、**ロング建玉が無ければ
1 件目の空売りで既に 100% となり成立しない**ことを意味する（＝空売りだけの建玉構成は作れない）。
決定9 の趣旨「ロングとショートを混在させることで方向性リスクが部分的に相殺される」どおりの帰結であり、
緩める解釈（例: 建玉が無いときは対象外とする）を採らない。

### 決定 6: 拒否理由のクラス分類（A/B/C）を `Shared.Contracts` に単一情報源として持つ

`RejectionReasonClassification.ClassOf(reason)` と `CountsAsControlViolation(...)` を置く。
クラス C は **`BannedSymbol` / `ManipulativeOrderPattern` の限定列挙**であり、
`switch` の既定（`_`）はクラス A へ落とす（新しい理由が既定でクラス C へ混入しない向き）。
計上単位は「1 回の発注拒否につき 1 件」（06_daytrading-review §4.1）であるため、
理由の列を受ける多重定義を用意し、クラス C を 1 つでも含めば 1 件と数える。

**段階ゲートの `StagePerformance.ControlViolationCount` への結線は本 issue の範囲外**である
（同フィールドは現状も外部からの入力であり、拒否の集計経路そのものが未実装。#333 の担当）。
分類だけを先に確定しておくことで、集計を実装する側が独自の分類を作れないようにする。

## 理由

- **決定 1**: 統制の適用可否を上流の申告値で決めると、「Close と言えば kill switch を回避できる」型の
  経路が生まれる（IADR-0119 が同じ誤りを是正している）。`Side` と `PositionEffect` は既に台帳・監査の
  権威値であり、新しい真実源を作らない。
- **決定 2**: 統制は**発注前に効かなければ意味を持たない**（ADR-0016 §理由）。約定後に借株料を取得して
  手仕舞う案は、約定した時点で既にリスクを取っており統制になっていない。ポートを先に切らないのは、
  PoC 前に外部 IF の形を決め打ちしないためと、CLAUDE.md の「過剰な抽象化を行わない」に従うためである。
- **決定 3**: 規則を落とすこと（C-1）は、**損失に上限が無い建玉を損切り機構なしで持つ**ことを許す。
  ADR-0016 決定2 は (a) 金額上限と (b) 逆指値を「両方」課すと明記しており、片方の実装漏れは
  ギャップリスクをそのまま残す。
- **決定 5**: 比率の分母を「建玉総額」と読む以外に、計画の文言（「空売り建玉の合計は、建玉総額の 50% を
  超えない」）を満たす読み方が無い。分母から自分自身を除く読み方は文言に反する。

## 結果

- 良い影響:
  - 空売り 8 規則が**発注前の決定的コード**で強制され、AI の判断がどうであれ違反注文は発注執行へ到達しない
  - 照会経路が未実装でも**安全側**（空売りしない）に縮退する。#342 の PoC 結果を待たずに統制を確定できる
  - 拒否理由のクラス分類が実装に 1 つだけ存在し、「統制違反 0 件」の意味が集計実装ごとにぶれない
  - `KnownPlanDeviations` の #329 担当 6 件がすべて解消した（第 1 段階 4 件・第 2 段階 2 件）
- 悪い影響・トレードオフ:
  - 空売りの有効・無効が `ShortSellSettings.Enabled` と（将来の）`ProductType.ShortSell` の 2 箇所で
    表現され得る（#332 で統合を検討する）
    → **2026-08-04 解消**: [IADR-0132](./IADR-0132_product-type-tri-state-and-guard-scope.md) 決定2 が
    `ShortSellSettings.Enabled` を削除し、単一情報源を `Guard.EnabledProductTypes` とした（#332）。
    本 IADR の決定1 のうち**識別規則（`Side` × `PositionEffect`）は不変**であり、有効・無効の所在だけが移った
  - `ShortSellOrderContext` の供給元が無いため、**現状はすべての新規売り建てが拒否される**。
    これは既定（空売り無効）と ADR-0016 決定8（実弾解禁は Stage 3・自己資金 $5,000 以上）に整合するが、
    Stage 1（SIMULATE）で空売りを検証するには供給元の実装が要る（#342 / #332 の後続）
  - `StopOrderRequired` は計画に無いコード名である（決定3・環流待ち）
  - 維持率割れの**自動縮小**（回復目標・対象選択）は #330 の担当であり、本 IADR は値
    （`MaintenanceRecoveryTargetOffset` と解決メソッド）だけを確定した
- フォローアップ:
  - #342: 借株料の事前照会可否（決定3 の成否）。不成立なら空売りフラグを恒久的に無効とする
  - #332: 商品種別 3 値化と `ShortSellSettings.Enabled` の統合 → **完了**（IADR-0132）
  - #330: 維持率割れによる自動縮小（本 IADR の `MaintenanceRecoveryTargetFor` を用いる）
  - #333: 拒否の集計（クラス C の件数）と段階ゲートへの結線
  - 計画への環流: `StopOrderRequired` のコード名・強制買戻し禁止の理由コード

## 関連

- Supersedes: なし（[IADR-0130](IADR-0130_equity-ratio-risk-limits.md) の続き。金額系の保持形式は同 IADR が有効。
  本 IADR は同 IADR 決定1〔解決点を 1 つに閉じる〕・決定2〔equity の定義〕を前提として用いる）
- Superseded by: なし
- **決定 5 の環流（2026-08-04 追記・#329 第 3 段階）**: 空売り比率 50% を文字どおり実装した帰結
  （`空売り建玉 ≦ ロング建玉総額` と等価であり、ロング建玉が 0 件では空売りを開始できない＝
  Stage 1 で空売り単独の検証ができない）を計画へ環流した
  （[feedback/20260804_adr0016-short-ratio-denominator.md](../../feedback/20260804_adr0016-short-ratio-denominator.md)）。
  等価形はプロパティテスト T-10-156 で機械的に固定してある。**計画側が案 B（建玉 0 件時の例外）または
  案 C（分母を equity へ）を採る場合、本 IADR の決定 5 は新しい IADR で改める**（本文は書き換えない）。
