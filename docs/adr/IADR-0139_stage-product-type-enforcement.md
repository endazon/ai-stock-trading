---
title: IADR-0139 段階別の商品種別強制を新規建てのみへ課し、Stage 3 の空売り実弾解禁をフェイルクローズの 2 条件 AND にする
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-19, FR-10, FR-11, UC-06, ADR-0008, ADR-0009, ADR-0016, IADR-0004, IADR-0107, IADR-0130, IADR-0131, IADR-0132, IADR-0134, IADR-0137]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
---

# IADR-0139: 段階別の商品種別強制を新規建てのみへ課し、Stage 3 の空売り実弾解禁をフェイルクローズの 2 条件 AND にする

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: 実装（Claude Code）／ 起点 issue [#333](https://github.com/endazon/ai-stock-trading/issues/333)（親 [#344](https://github.com/endazon/ai-stock-trading/issues/344)）
- 作業仕様書: [20260804_333_stage-gate](../specs/20260804_333_stage-gate.md)

## コンテキストと課題

[ADR-0016 決定8](../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md) は
段階別の商品種別を表で確定した。

| Stage | 現物 | 信用買い | 空売り |
| --- | --- | --- | --- |
| Stage 0（検証） | 実弾なし | 実弾なし | 実弾なし |
| Stage 1（SIMULATE 3 か月） | ✅ | ✅ | ✅ **検証する** |
| Stage 2（最小実弾） | ✅ | ❌ | ❌ |
| Stage 3（段階増額） | ✅ | ✅ | ✅ **実弾解禁** |

Stage 3 の空売り実弾解禁にはさらに 2 条件が課される。

> **Stage 3 での空売り実弾解禁には、1 銘柄あたりの空売り上限が $500 以上であることを条件とする**（決定8。
> 決定 2(a) より上限は自己資金の 10% であるから、この条件は **自己資金 $5,000 以上**と等価である）
>
> **空売りを含む戦略で Stage 0 の 7 条件を再度満たすことを、Stage 3 の空売り実弾解禁の前提条件とする**（決定14）

**実装にはこの規則が存在しなかった。** [#332](https://github.com/endazon/ai-stock-trading/issues/332)（IADR-0132）は
商品種別を 3 値化したが、扱ったのは**利用者設定**（`Guard.EnabledProductTypes`）だけであり、
段階ゲートが課す強制は範囲外だった（[FR-20 テスト仕様書](../tests/FR-20_staged-gates-tests.md)も
「既定値と段階別強制を混同しないこと」と注意している）。

実装判断を要したのは次の 4 点である。

1. **適用範囲（新規建てか全注文か）。**
2. **設定（`Guard.EnabledProductTypes`）との関係。** 段階が許せば設定を上書きするのか、両方要るのか。
3. **equity $5,000 をどの通貨で判定するか。** 計画（ADR-0016 決定6）は USD 建てを正とするが、
   実装の判定パイプラインは基準通貨（円）建てである。
4. **決定14 の「空売りを含む戦略で Stage 0 の 7 条件を再度満たす」verdict をどう受けるか。**
   `BacktestEvaluated` には「空売りを含む戦略か」の属性が無い。

## 決定

### 決定 1: 適用は**新規建てのみ**。手仕舞い・損切りは止めない

`RiskEvaluator` の `isEntry` ブロックから `StageProductPolicy.Evaluate` を呼ぶ。

project-planning#179 の裁定により、**段階別の商品種別強制の適用範囲は「新規建てのみ」**である
（#332 が実装した商品種別ガードと同じ範囲・[IADR-0132 決定4](IADR-0132_product-type-tri-state-and-guard-scope.md)）。
**段階を上げる前に建てた信用買い・空売りの建玉を閉じられなくなると、損失に上限が無い建玉を抱えたまま
決済できなくなる。** FR-10 の不変条件「手仕舞い（Close）と損切りは止めない」（ADR-0009）に真っ向から反する。

具体的には、Stage 3 → Stage 2 の差し戻しが起きた瞬間に、保有中の空売り建玉の買い戻し
（`Buy × Close × ShortSell`）が拒否される——**差し戻しという安全側の操作が、最も危険な建玉を
凍結させる**という倒錯が生じる。否定形テストで塞いだ。

### 決定 2: 設定と段階制約は**両方**を満たす必要がある（常に厳しい方が効く）

段階制約は `Guard.EnabledProductTypes` を上書きしない。**設定で有効にしても段階が許さなければ通らず、
段階が許しても設定で無効なら通らない。** FR-10「同一の注文に複数の上限が掛かる場合は常に厳しい方が効く」と
同じ構造であり、`RiskEvaluator` が違反を全件列挙する（＝ AND）性質にそのまま乗る。

拒否理由も分ける。`ProductTypeDisabled`（設定による無効化）と `StageProductTypeProhibited`（段階制約）は
**原因も解除方法も違う**——前者は設定変更で解け、後者は段階が上がるまで解けない。畳むと監査ログ（FR-11）の
理由が実態と食い違う（ADR-0016 決定10 の 2026-08-04 追記が `BuyInBanned` について示したのと同じ規律）。

### 決定 3: 照合は**実効商品種別**で行う

`ProductTypeResolver.Resolve(intent)` の結果（新規売り建ては常に `ShortSell`）で判定する。
申告値（`OrderIntent.ProductType`）を信じると、**新規売り建てを `Cash` と申告して段階制約を迂回できる**。
IADR-0132 決定3 と同じ規律であり、否定形テストで固定した。

### 決定 4: Stage 3 の空売り実弾解禁は **2 条件の AND** で、供給が無ければ**開かない**

```
equity ≥ $5,000  かつ  空売りを含む戦略で Stage 0 の 7 条件を再充足
```

- **等値変換後の equity 側で判定する。** 計画は「1 銘柄あたり上限 $500 以上」を「自己資金 $5,000 以上と
  等価」と自ら変換している。$500 側で判定すると、1 銘柄あたり上限（`ShortSellingLimits.PerSymbolCapFor`）と
  同じ制約を 2 箇所で表現することになる。等値であることは
  `StageProductPolicyTests.解禁下限は1銘柄あたり空売り上限500ドルと等価である` が固定する。
- **解禁下限は `ShortSellingLimits` に置かない。** 同型は計画適合レジストリ（`ShortSell.Limits`）が
  メンバ集合ごと固定している値の集合であり、**利用者が設定する統制値**である。解禁条件は計画が定めた
  段階ゲートの条件であって統制値ではないため、`StageProductPolicy` の定数として持つ。
- **`StageReleaseContext` が `null`（供給元が無い）なら空売りは通さない**（フェイルクローズ）。
  `ShortSellOrderContext` が `null` のとき借株不可として扱うのと同じ規律（IADR-0131 決定4）。
  「確認できないまま通す」を許すと、統制が実質的に存在しないのと同じになる。

### 決定 5: equity の判定は基準通貨（円）建てで行い、計画記載の参照レートで 1 点換算する

計画（ADR-0016 決定6）は判定の基準を**自己資金の米ドル建て評価額**と定めるが、実装の統制判定
パイプラインは基準通貨（円）建てである（IADR-0107 / IADR-0130 決定3）。初期投入資金と同じく
**計画が明記した参照レート**（`TradingDefaults.ReferenceUsdToJpyRate` ＝ §5「1 USD ≈ 163.7 円」）で
1 点換算する。実装が独自のレートを決めているのではない。

**実勢レートでの equity 評価は #333 の範囲外である。** 為替レート源そのものが計画追随待ち
（[#381](https://github.com/endazon/ai-stock-trading/issues/381)・ADR-0022）であり、
そちらが決着してから判定通貨を移すのが順序として正しい。IADR-0130 決定3 が
「equity と注文金額を同一通貨で評価する限り、比率による判定は通貨に依存しない」と示したとおり、
比率の判定は移行しても値の書き換えが要らない。**本件は比率ではなく絶対額の閾値**であるため、
移行時には換算点の見直しが要る——その旨をここに残す。

### 決定 6: 新しい拒否理由 2 種はクラス B とし、序数は末尾へ足す

`StageProductTypeProhibited`（23）・`StageShortSellReleaseUnmet`（24）を `RejectionReason` の**末尾**へ追加する
（IADR-0134 決定2）。**分類はクラス B**（段階制約による拒否）である——段階が許さない商品種別の発注要求は
「段階ゲートが設計どおり止めた」記録であり、**AI が禁止事項を犯そうとした件数（クラス C）ではない**。
クラス C へ混ぜると段階昇格ゲート（「統制違反 0 件」）が機能しなくなる
（ADR-0016 決定10 が `$5` 未満の除外について明示的に禁じた誤りと同型）。

## 結果

- **良い影響**: ADR-0016 決定8 の表が発注前の決定的コードとして実効する。設定と段階制約が独立して効くため、
  「設定で空売りを有効にしたら Stage 2 でも空売れる」という抜け道が塞がった。
  空売りの実弾解禁が fail-closed であり、条件を確認できないまま開くことはない。
- **悪い影響 / トレードオフ**:
  - **`RiskEvaluator.Evaluate` の引数が 1 つ増えた**（`stageRelease`）。既定 `null` はフェイルクローズであり、
    渡し忘れは「空売りが開かない」方向へ倒れる（安全側）。
  - equity 閾値の通貨換算が 1 点固定である（決定 5）。実勢レートでの評価へ移す際は換算点の見直しが要る。
- **残余リスク（実装したが発動しない）**:
  - **決定14 の verdict（空売りを含む戦略での Stage 0 再充足）の供給元が存在しない。**
    `BacktestEvaluated` に「空売りを含む戦略か」の属性が無く、`StageReleaseContext` を組み立てる経路も無い。
    `OrderScreeningService` は本引数を渡さないため、**Stage 3 の空売り実弾は常に
    `StageShortSellReleaseUnmet` で閉じている。** これは安全側であるが、
    **「解禁条件を満たしたのに開かない」状態でもある**——解禁時には供給元の実装が必須である。
    差し込み口（`StageReleaseContext`）を残したのはそのためである。
  - そもそも Stage 2/3 は実弾段階であり、実弾は別途 triple-latch（IADR-0060 / IADR-0111）で
    到達不能に閂が掛かっている。段階別の商品種別強制が実運用で効くのは SIMULATE 上の検証時である。

## 関連

- 計画: [ADR-0016 決定8・決定14](../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md)／
  [05_trading-assumptions §5](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md)「取引可能な商品種別」「空売りの実弾解禁条件」／
  [06_daytrading-review §4](../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md)
- 実装 ADR: [IADR-0132](IADR-0132_product-type-tri-state-and-guard-scope.md)（商品種別 3 値・実効値照合・ガードの適用範囲）／
  [IADR-0131](IADR-0131_short-selling-controls-fail-closed.md) 決定4（フェイルクローズ）／
  [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定2（序数は末尾へ）／
  [IADR-0130](IADR-0130_equity-ratio-risk-limits.md) 決定3（判定の権威値は USD・パイプラインは基準通貨）／
  [IADR-0137](IADR-0137_stage1-trading-day-counting.md)
- 仕様書: [作業仕様書 20260804_333](../specs/20260804_333_stage-gate.md)／
  [FR-19 機能仕様書](../functional/FR-19_trading-guard.md)／[FR-20 テスト仕様書](../tests/FR-20_staged-gates-tests.md)
- **Superseded by（決定5 のみ）**: [IADR-0152](IADR-0152_usd-base-currency-migration.md) 決定3
  （2026-08-05・[#364](https://github.com/endazon/ai-stock-trading/issues/364)）。判定の基準通貨が USD になったため、
  本 IADR 決定5 の「equity 閾値は基準通貨〔円〕建てで判定し参照レートで 1 点換算する」という**近似は不要になった**。
  `ShortSellLiveReleaseEquityInBase` は削除し、`ShortSellLiveReleaseEquityUsd`（$5,000）を equity と直接比較する。
  同 IADR が残余リスクとして挙げていた「実勢レートでの評価へ移す際は換算点の見直しが要る」は解消した
  （計画 ADR-0016 決定6「自己資金の米ドル建て評価額」と実装が厳密に一致する）。決定1〜4・決定6 は不変である。
