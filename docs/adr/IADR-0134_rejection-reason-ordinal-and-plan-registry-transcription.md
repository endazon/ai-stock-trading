---
title: IADR-0134 強制買戻しの禁止は専用の拒否理由 BuyInBanned で記録し、拒否理由 enum の既存序数は不変とする。計画適合レジストリの計画側は人手転記であり submodule のピン更新だけでは赤くならない
type: impl-adr
status: Accepted
related_ids: [NFR, FR-10, FR-11, FR-19, FR-20, UC-06, ADR-0016, IADR-0127, IADR-0131, IADR-0132]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
---

# IADR-0134: 拒否理由 `BuyInBanned` の新設と序数の不変規律、計画適合レジストリの転記境界

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: endazon（利用者。#374 の実装方針として）

## 起点・関連

- 関連する計画書 ID: **FR-10**（空売り拒否理由 9 種）／ **FR-11**（監査ログの理由）／ FR-19・FR-20（境界）／
  UC-06 ／ **ADR-0016 決定4**（強制買戻しの 30 日禁止）・**決定10**（拒否理由 9 種。2026-08-04 改訂）・
  **決定15**（日報・月報への強制買戻しの記載）
- 関連する実装仕様書: [作業仕様書 20260804（#374）](../specs/20260804_374_short-sell-rejection-reasons-nine.md)・
  [機能仕様書 FR-10](../functional/FR-10_risk-controls.md)・[テスト仕様書 FR-10](../tests/FR-10_risk-controls-tests.md)
- 関連 issue: [#374](https://github.com/endazon/ai-stock-trading/issues/374)／
  由来 [project-planning#178](https://github.com/endazon/project-planning/issues/178)
- 先行 IADR: [IADR-0131](IADR-0131_short-selling-controls-fail-closed.md)（決定3 が本 IADR で改まる）・
  [IADR-0132](IADR-0132_product-type-tri-state-and-guard-scope.md)（決定1 の序数保存と同じ規律）・
  [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)（計画適合レジストリ。本 IADR 決定3 がその限界を記録する）

## コンテキストと課題

計画（ADR-0016 決定10）が 2026-08-04 に空売りの拒否理由を **7 種から 9 種**へ改訂し、
`StopOrderRequired` を実装と同名で追認、`BuyInBanned` を新設した。あわせて次を明示的に禁じた。

> **`BuyInBanned` を `BorrowUnavailable` へ写像してはならない。** `BorrowUnavailable` は
> **都度の借株需給**による locate 失敗であり、`BuyInBanned` は**期間の経過**で解除される禁止状態である。
> 原因も解除条件も異なるため、写像すると監査ログ（FR-11）の理由が実態と食い違い、原因究明が壊れる。

[IADR-0131](IADR-0131_short-selling-controls-fail-closed.md) 決定3 は、対応する計画コードが無かった
時点の判断として強制買戻しの 30 日禁止を `BorrowUnavailable` へ写像していた。裁定によりこれが
**明示的に否定された**ため、実装を追随させる。その際に 3 つの論点が生じた。

1. **enum のどこへ足すか**。拒否理由は HTTP 経路で整数として往来する。
2. **旧写像を消したあと、再び畳まれないことをどう担保するか**（統制の迂回は「素通り」だけでなく
   「記録の粒度を落として区別できなくすること」でも起こる）。
3. **submodule のピンを進めただけで計画適合テストが赤くならなかった**。issue #374 は
   「更新すると赤くなる」と想定していたが、実測は緑であった。機構の限界を明らかにする必要がある。

## 検討した選択肢

### 論点 A: `BuyInBanned` を enum のどこへ置くか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A-1 | 意味の近い `BorrowCostExceeded` の隣（序数 17）へ**挿入**する | 読みやすいが、以降の 6 メンバの序数が 1 つずつずれる。**過去に記録された拒否の意味が変わる**（`17` が `ShortExposureExceeded` から `BuyInBanned` へ化ける） |
| **A-2** | **末尾（序数 22）へ追加**し、意味のまとまりは XML ドキュメントの相互参照で表す | 既存の記録・伝送値がすべて保存される。[IADR-0132](IADR-0132_product-type-tri-state-and-guard-scope.md) 決定1（商品種別の序数保存）と同じ規律 |

### 論点 B: 再統合（写像の復活）をどう塞ぐか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | 肯定形のみ（禁止期間中に `BuyInBanned` が立つこと）を検証する | `BorrowUnavailable` を**併記**する実装でも通ってしまう。畳み込みは塞げない |
| **B-2** | **両向きの否定形**を置く。(a) 借株が成立している状態で禁止期間だけが効くとき `BorrowUnavailable` が立たないこと、(b) locate 失敗だけのとき `BuyInBanned` が立たないこと | 片方だけでは不十分——(a) だけなら locate 失敗を `BuyInBanned` へ寄せる逆向きの畳み込みが残り、**日報・月報の「強制買戻しの発生回数」が起きていない事象で水増しされる** |

### 論点 C: 計画適合テストが submodule 更新で赤くならないこと

`PlanConformanceTests` は `PlanRiskDefaults`（計画値）と `ActualDefaults`（実装値）を突き合わせる。
**`PlanRiskDefaults` は計画書を人手で転記した C# の表**であり、テストは planning submodule の
ファイルを一切読まない（`backend/` 配下に `planning/` を読むコードは存在しない）。したがって
ピンを進めても表は変わらず、テストは緑のままである。

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C-1 | 計画書（Markdown）を実行時にパースして計画値を導出する | 計画書は散文であり、値の在処が節・表・本文に散っている。パーサの誤読が「計画がそう書いてある」として通る新しい失敗様式を作る。[IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md) が値を**正規化文字列**にした理由（単位・基準を含めて比べる）とも噛み合わない |
| C-2 | 現状のまま（限界を記録しない） | 「submodule を更新すれば機構が教えてくれる」という**誤った安心**が残る。#374 は実際にその前提で起票されている |
| **C-3** | **転記であることを明示し、submodule のピンを動かす作業では計画差分の突き合わせを人手（または AI）の手順として必須にする**。転記後は機構が実装との乖離を機械的に検知する | 限界の所在（計画→表の 1 ホップだけが人手）を正確に切り分けられる。表を直せば以降は機械が守る |

## 決定

### 決定 1: 強制買戻しの 30 日禁止は専用の拒否理由 `BuyInBanned` で記録する

`ShortSellEvaluator` の (8) は `RejectionReason.BuyInBanned` を列挙する。
**`BorrowUnavailable` へも `BannedSymbol` へも写像しない。** クラス分類は
`RejectionReasonClassification` の既定どおり**クラス A** であり、
「統制違反 0 件」（クラス C 限定）の件数には影響しない。

旧実装は `BorrowUnavailable` が既に列挙済みなら追加しない重複排除を行っていたが、
理由が分離されたため**判定は 1 行になる**。借株需給による拒否と禁止期間による拒否は
**同時に立ち得る**（両方が真なら両方が列挙される）——違反の全件列挙という同評価器の規律
（FR-11 監査）どおりであり、意図的である。

これにより [IADR-0131](IADR-0131_short-selling-controls-fail-closed.md) 決定3 の後段
（「強制買戻し由来の 30 日禁止は `BorrowUnavailable` へ写像し `BannedSymbol` には混ぜない」）は
**本 IADR が改める**。前段（`StopOrderRequired` の新設・クラス A）は計画が同名で追認したため
**そのまま有効**である。

### 決定 2: 拒否理由 enum の既存メンバの序数は不変とし、新設は常に末尾へ追加する（案 A-2）

`RejectionReason` は段階ゲートの HTTP 経路で `IReadOnlyList<int>` として往来する。
既存メンバの間へ挿入すると**過去の記録の意味が変わる**。
`RejectionReasonOrdinalStabilityTests` が全メンバの序数を表で固定し、
**表に無いメンバがあれば失敗する**（＝末尾へ足した本人が表を更新するまで緑にならない）。

意味のまとまり（空売り 9 種が連続していないこと）は XML ドキュメントの相互参照で補う。
**読みやすさより記録の不変性を優先する**——[IADR-0132](IADR-0132_product-type-tri-state-and-guard-scope.md) 決定1 が商品種別で採ったのと同じ規律である。

### 決定 3: 計画適合レジストリの計画側は**人手転記**である。submodule のピン更新だけでは赤くならない（案 C-3）

[IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md) の機構は
「**計画値の表と実装の乖離**」を機械的に守るが、「**計画書と計画値の表の乖離**」は守らない。
実装側（`ActualDefaults`）はリフレクションで機械抽出するのに対し、計画側（`PlanRiskDefaults`）は
散文の計画書からの転記だからである。この非対称は設計上のもので、隠さずに記録する。

**運用規律**: `planning` submodule のピンを動かす作業は、**ピン更新と同じ PR の中で**
計画差分（`git -C planning diff <旧ピン>..<新ピン>`）を読み、`PlanRiskDefaults` の対象範囲
（05_trading-assumptions §1/§4/§5/§6 ＋ ADR-0008 / ADR-0016 / ADR-0018）に触れる変更が
あれば表を追随させる。**転記さえ行えば、そこから先（実装との乖離）は機構が守る。**

あわせて `ActualDefaults` の抽出候補にも同じ穴があった。`RejectionReason.ShortSellReasons` は
**計画が名指しした候補名の配列**を通して抽出するため、実装に存在しても候補に無い名前は
抽出値に現れない。`StopOrderRequired` は #329 で実装済みでありながら候補に無く、
**実装が計画を先回りしている事実が計画適合検査からは見えていなかった**。候補は
「計画が名指しする集合」であって「実装が持つ集合」ではない、という読みは維持しつつ、
計画改訂時には候補配列も同時に追随させる。

### 決定 4: 写像の再統合は**両向きの否定形**で塞ぐ（案 B-2）

- `強制買戻しの禁止期間中の拒否は借株不可へ写像されない`（借株成立・料率も上限内の状態で
  禁止期間だけが効くケースを組み、`BorrowUnavailable` が**立たない**ことを見る）
- `借株できないだけの拒否は強制買戻し禁止へ写像されない`（逆向き。`BuyInBanned` が**立たない**）
- `強制買戻しの禁止と借株不可は別の拒否理由である`（enum レベル。一方を他方の別名にしない）

## 理由

- 計画が写像を禁じた根拠は**監査の原因究明**である（FR-11）。拒否の理由コードは事後に
  「なぜ発注が止まったか」を復元する唯一の手掛かりであり、原因も解除条件も異なる 2 事象を
  1 コードへ畳むと、**期間が過ぎれば通るのか、需給が緩めば通るのか**が判別できなくなる。
- 決定15 が日報・月報へ「強制買戻しの発生有無・発生回数」を求めている以上、区別に実益がある。
  ただし**拒否件数は発生回数ではない**（1 回の強制買戻しに対し、禁止期間中の拒否は何度でも起こり得る）。
  集計の正しい入力は強制買戻し**イベント**であり、その受信経路は未実装である（後述）。
- 序数の不変性を規律として固定するのは、`RejectionReason` が**外部へ出る値**だからである。
  内部でしか使わない enum なら並べ替えは自由だが、記録・伝送に乗る enum の並びは契約である。

## 結果

- 良い影響:
  - 強制買戻しによる拒否が監査ログ上で**独立した理由**として残り、借株需給の逼迫と区別できる。
  - 序数の固定により、拒否理由の追加が**過去の記録を壊さない**ことが機械的に保証される。
  - 計画適合レジストリの限界（計画→表の 1 ホップが人手）が明文化され、
    「submodule を更新すれば機構が教えてくれる」という誤解が解消される。
- 悪い影響 / トレードオフ:
  - `RejectionReason` の並びが**意味のまとまりと一致しなくなる**（空売り 9 種が連続しない）。
    読み手は XML ドキュメントの相互参照に頼ることになる。
  - 序数表（`RejectionReasonOrdinalStabilityTests`）は理由を足すたびに更新が要る。
    ただし更新漏れはテストが検出する（表の網羅性検査）ため、忘れて壊れることはない。
  - **計画差分の突き合わせは依然として人手**である。転記漏れは機構では検知できない。
- フォローアップ:
  - **強制買戻し（buy-in）イベントの検知・通知と禁止リストの永続化は未実装**（担当 issue 未起票）。
    `ShortSellOrderContext.BuyInBanUntil` の供給経路が無いため、現状 `BuyInBanned` は
    テスト以外では立たない。ADR-0016 決定14 は「SIMULATE では発生しないため実弾解禁前に
    受信経路の疎通確認を行う」としており、ブローカー側の実装（#342）に依存する。
  - **日報・月報の「強制買戻しの発生回数」**（決定15）は上記イベントを入力とすべきであり、
    `BuyInBanned` の拒否件数で代用しない。報告書側の担当。

## 関連

- 改める: [IADR-0131](IADR-0131_short-selling-controls-fail-closed.md) 決定3 の**後段のみ**
  （強制買戻しの写像先）。前段（`StopOrderRequired` の新設）は計画の追認により有効
- 同じ規律: [IADR-0132](IADR-0132_product-type-tri-state-and-guard-scope.md) 決定1（enum 序数の保存）
- 限界を記録する対象: [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)
- 計画への環流（いずれも裁定済み）: [拒否理由コードの不足](../../feedback/20260804_adr0016-stop-order-rejection-reason.md)・
  [空売り比率 50% の構造的含意](../../feedback/20260804_adr0016-short-ratio-denominator.md)・
  [取引ガードの適用範囲](../../feedback/20260804_fr19-guard-scope.md)
