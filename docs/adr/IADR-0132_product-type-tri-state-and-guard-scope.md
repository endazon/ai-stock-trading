---
title: IADR-0132 商品種別は 3 値を単一情報源とし、実効値で照合する。商品種別ガードと差金決済ガードは適用範囲を絞る
type: impl-adr
status: Accepted
related_ids: [FR-19, FR-10, FR-20, UC-06, ADR-0007, ADR-0009, ADR-0016, IADR-0004, IADR-0119, IADR-0131]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
---

# IADR-0132: 商品種別は 3 値を単一情報源とし、実効値で照合する。商品種別ガードと差金決済ガードは適用範囲を絞る

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: 実装（Claude Code）／ 起点 issue [#332](https://github.com/endazon/ai-stock-trading/issues/332)（親 [#344](https://github.com/endazon/ai-stock-trading/issues/344)）
- 作業仕様書: [20260804_332_trading-guards](../specs/20260804_332_trading-guards.md)

## コンテキストと課題

[ADR-0016 決定1](../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md) は
取引ガード（FR-19）の商品種別を「**現物 / 信用買い / 空売り**」の 3 値に分け、それぞれ独立に有効・無効を
設定できるようにすることを定めた（既定はいずれも「現物のみ有効」）。実装は `Cash, Margin` の 2 値のままであり、
計画適合検査（[IADR-0127](./IADR-0127_plan-conformance-known-deviation-registry.md)）へ既知逸脱
`ProductType.Values`（担当 #332）として登録されていた。

3 値化にあたり、次の 4 点が実装判断を要した。

1. **有効・無効の情報源**: [#329 第 2 段階](../specs/20260804_329_short-selling-controls.md)は
   `ProductType` が 2 値であったため、空売りの有効・無効を専用フラグ `ShortSellSettings.Enabled` で持った
   （[IADR-0131 決定1](./IADR-0131_short-selling-controls-fail-closed.md)）。3 値化により**同じ事実が
   2 箇所で表現され得る**状態になる。
2. **照合する値**: `OrderIntent.ProductType` は上流（AI 判断サービス）が組み立てる申告値である。
   申告をそのまま信じてよいか。
3. **適用範囲（建玉効果）**: 現行は商品種別ガードを**全注文**へ適用している。3 値化すると、既定
   （現物のみ有効）では**空売り建玉の買い戻し**（`Buy × Close × ShortSell`）が `ProductTypeDisabled` で
   拒否される。損失に上限が無い建玉を閉じられなくなる。
4. **差金決済ガードの適用範囲**: FR-19 は「差金決済防止のガードは**日本株の現物取引**における差金決済規制に
   対応するものであり、米国株は信用口座（margin account）で運用するため Good Faith Violation は発生しない」と
   明記している（2026-08-01 改訂・planning#81 の利用者裁定）。実装は市場・商品種別を問わず適用していた。

## 検討した選択肢

| # | 選択肢 | 評価 |
| --- | --- | --- |
| A-1 | `ProductType` を 3 値化し、`ShortSellSettings.Enabled` を**削除**して `Guard.EnabledProductTypes` を単一情報源にする | **採用**。計画（FR-19）が「商品種別の可否」を取引ガードの項目として定めている。設定の食い違いが構造的に起きない |
| A-2 | 両方を残し「両方が有効なときだけ空売り可」とする | 二重の AND は安全側だが、**画面で有効化したのに空売りできない**という説明不能な状態を作る。設定の単一情報源が失われる |
| A-3 | `ShortSellSettings.Enabled` を `Guard` から導出するプロパティにする | 型としては 1 情報源だが、`ShortSellSettings` が `Guard` を知る循環参照になる |
| B-1 | 申告値（`OrderIntent.ProductType`）でガードを照合する | 新規売り建てを `Cash` と申告すれば商品種別ガードを迂回できる。**AI の自己申告で解除できるガード**になる |
| B-2 | 売買方向 × 建玉効果から**実効商品種別**を導いて照合する | **採用**。IADR-0004 / IADR-0119 / IADR-0131 と同じ規律（判定は申告ではなく事実から導く） |
| C-1 | 商品種別ガードを全注文へ適用する（現行） | 無効な商品種別の建玉を手仕舞えない。ADR-0009 / FR-10 の不変条件に反する |
| C-2 | 商品種別ガードを**新規建て（Open）に限定**する | **採用**。統制は「新規建てを止める」ものであり、手仕舞い・損切りは止めない |

## 決定

### 決定1: `ProductType` を 3 値（`Cash` / `MarginLong` / `ShortSell`）とし、序数を保つ

計画適合検査の期待値（`PlanRiskDefaults`「ProductType.Values」＝ `Cash, MarginLong, ShortSell`）に一致させる。
**旧 `Margin = 1` の位置は `MarginLong = 1` が引き継ぐ**。設定は JSON へ数値 enum として永続化され
（`RiskSettingsSerialization`）、画面も数値で送受信する（[IADR-0086](./IADR-0086_frontend-guard-edit-ui.md) 決定4）。
序数を入れ替えると**既存行の「有効な商品種別」が別の意味に化ける**。既定は `{ Cash }` のままである。

### 決定2: 有効・無効の単一情報源は `Guard.EnabledProductTypes`（`ShortSellSettings.Enabled` を削除）

空売りの有効・無効は `Guard.EnabledProductTypes.Contains(ProductType.ShortSell)` で決まる。
`ShortSellSettings` は**統制値（`Limits`）だけ**を持つ。`ShortSellEvaluator.Evaluate` は有効・無効を
引数（`bool shortSellEnabled`）で受け取り、設定の所在を自分では決めない。

計画（FR-19）が有効・無効を「取引ガードの商品種別」として定めている以上、実装側に別の入口を作らない。
永続化された旧行（`{"enabled":false,"limits":{…}}`）は `enabled` が無視され `limits` だけが読まれる。
**旧行の空売り可否は `EnabledProductTypes` が決める＝既定は現物のみ**であり、縮退は安全側である。

### 決定3: ガードは**実効商品種別**で照合する

`ProductTypeResolver.Resolve(intent)`:

| 注文 | 実効商品種別 |
| --- | --- |
| `Side == Sell` かつ `PositionEffect == Open`（新規売り建て） | **`ShortSell`**（申告値に関わらず） |
| それ以外 | 申告どおり |

新規売り建ての識別は [IADR-0131 決定1](./IADR-0131_short-selling-controls-fail-closed.md) と同一の規則を用いる
（`ShortSellEvaluator.IsShortEntry`）。**空売りの識別規則が実装に 2 つあってはならない。**
それ以外の注文は申告どおりに扱う（現物と信用買いを事実から区別する手段は建玉・口座情報に依るため、
本 issue の範囲では読み替えない。過剰な推測をしない）。

### 決定4: 商品種別ガードは**新規建て（Open）にのみ**適用する

FR-10 の不変条件「kill switch・日次損失ロックアウト・一時停止はいずれも**手仕舞い（Close）と損切りは止めない**」
（[ADR-0009](../../planning/projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md)）と同じ扱いにする。
3 値化により、この経路は**理論上の話ではなくなった**——既定で空売りは無効であり、空売り建玉の買い戻しは
`ProductType.ShortSell` の注文になるため、全注文へ適用すると**損失に上限が無い建玉を閉じられない**。

市場ガード（`MarketDisabled`）・禁止銘柄ガード（`BannedSymbol`）は**現行どおり全注文へ適用する**。
禁止銘柄は「登録は利用者の明示的な意思であり、システムは登録されたものを確実に強制する」（ADR-0007 §理由）
という計画の立て付けがあるため、実装判断で緩めない（作業仕様書の未決事項へ記録し、計画側の裁定に委ねる）。

> **計画への環流（2026-08-04・決定内容は変えない）**: 本決定が埋めた「適用範囲の未記載」と、
> その結果として同じガード群に生じた非対称（商品種別＝ Open 限定 / 禁止銘柄・市場＝全注文）を
> [feedback/20260804_fr19-guard-scope.md](../../feedback/20260804_fr19-guard-scope.md)（論点 1・論点 2）で
> 計画へ環流した。
>
> **【✅ 裁定済み 2026-08-04・ADR-0007 追補（質問票 第 1 回 Q3・Q4・planning#179）】** 本決定の
> 適用範囲は**計画の裁定と完全に一致していた**——商品種別＝**新規建て（Open）のみ**／禁止銘柄・市場＝**全注文**。
> **実装の変更は不要**である（#380）。禁止銘柄の Close 適用（選択肢 A）の理由は**インサイダー取引は売付けも対象**
> であり、AI が利用者の関知しないタイミングで規制対象銘柄を自動売却する経路を残さないためである。
>
> **この非対称は意図である。** ADR-0007 追補は「ガードごとに適用範囲が異なるのは**各ガードの目的が異なる
> ためであり、揃えるべき不整合ではない**」と明示している。**揃える方向の変更を提案しないこと。**
> 保有建玉を手仕舞う必要が生じた場合の手順は
> [禁止銘柄の一時解除 Runbook](../operations/banned-symbol-unlock-runbook.md) を正とする。

### 決定5: 差金決済防止ガードは**日本株 × 現物 × 新規建て**に限定する

FR-19 本文・05_trading-assumptions §5「米国口座の種別・決済」に従う。米国株は信用口座で運用するため
Good Faith Violation が発生せず、決済制度由来の回転数制約が無い。回転数は**日次発注金額上限
（equity の 150%/日）と保有建玉数上限（3）**で管理する。信用（信用買い・空売り）を除くのは、
2013 年の府令改正により**同一保証金での同日無制限回転が可能**であり差金決済規制の対象外だからである
（06_daytrading-review §2.1・§5「差金決済防止」の「信用有効化時は信用側で回転」）。

**誤適用は「統制が効きすぎる」だけでは済まない**——米国株（主ターゲット）の当日回転が丸ごと止まり、
日中スイングの戦略そのものが成立しなくなる。退行防止テスト（米国株で作動しないこと・日本株現物で作動すること）を
必須とする。

> **計画への環流（2026-08-04・決定内容は変えない）**: 根拠として参照される
> `06_technical/06_daytrading-review.md` §2.2 に「日本の差金決済禁止は米国株現物にも適用される」という
> 2026-07 時点の記述が残っており、本決定の根拠（FR-19 の 2026-08-01 改訂・planning#81）と齟齬する。
> 更新または参照追記を [feedback/20260804_fr19-guard-scope.md](../../feedback/20260804_fr19-guard-scope.md)
> （論点 3）で求めた。

### 決定6: 禁止銘柄の照合は「市場は厳密一致・銘柄コードは表記差を吸収」する

`BannedSymbol.Matches(symbol, market)` を単一情報源とし、市場は厳密一致（別市場の同一コードを誤拒否しない。
Issue #26）、銘柄コードは**前後空白を無視した大文字小文字非依存の一致**とする。禁止銘柄は利用者の自由入力であり、
序数比較のままでは `aapl` / `AAPL ` のような**表記差だけで禁止を迂回できる**。禁止銘柄の拒否は**クラス C**
（「AI が禁止事項を犯そうとした件数」）であり、取りこぼしは段階昇格ゲートの証跡そのものを損なう。

同日再エントリーの照合（`SymbolsTradedToday`）は本規則の対象外とする。こちらは自システムの約定台帳から
組み立てられる値であり表記が揺れないためである（外部由来の表記が入る経路ができた時点で見直す）。

## 理由

- **設定の食い違いは統制の穴になる**。「画面では空売り無効だがバックエンドでは有効」という状態は、
  どちらが正かを実行時に判定できない。単一情報源であれば、そもそも食い違いが表現できない（決定2）。
- **ガードは AI の自己申告で解除できてはならない**。ADR-0007 は「リスク管理サービスが発注前に決定的コードで
  強制する」と定めており、判定の入力に上流の申告を用いると決定性は保たれても**強制力が失われる**（決定3）。
- **統制の目的は新規リスクの抑制であり、既存リスクの固定化ではない**。手仕舞いを止める統制は、
  損失に上限が無い建玉に対して最悪の結果を生む（決定4）。
- **計画が明示的に否定した適用範囲を実装が保持し続けてはならない**。米国株への差金決済ガードの適用は、
  2026-08-01 の FR-19 改訂で明確に否定された（決定5）。

## 結果

- 良い影響:
  - 商品種別が計画どおり 3 値になり、**段階解禁（Stage 1＝3 種／Stage 2＝現物のみ／Stage 3＝条件付き）を
    設定として表現できる**（強制の結線は #333）
  - 空売りの有効・無効の情報源が 1 つになった（IADR-0131 の申し送り事項が解消）
  - 米国株の当日回転が決済制度由来の理由で止まらなくなった（統制は金額・件数の上限が担う）
  - `KnownPlanDeviations` の #332 担当 1 件が解消した
- 悪い影響・トレードオフ:
  - `ProductType` の序数が永続化・画面と結合していることが明文化された（**値の並べ替え禁止**という制約が増えた）
  - 信用買い（`MarginLong`）は**有効・無効の制御だけ**が入った。信用金利・必要証拠金・建玉の区別は未実装である
    （実弾解禁は Stage 3 のため急がない）
  - 商品種別ガードを Open 限定にしたため、「無効な商品種別の建玉を持ってしまった場合」に**手仕舞いは通る**。
    これは意図した縮退である（新規建ては止まる）
  - 手仕舞い（Close）の申告そのものを詐称する経路（保有ゼロでの `Close` 申告）は本 IADR の対象外であり、
    上流の建玉効果の導出（[IADR-0119](./IADR-0119_decision-derived-close.md)「保有なし・不明の売りは見送る」）が担う
- フォローアップ:
  - #333: 段階別の商品種別強制（本 IADR の `EnabledProductTypes` と **AND** で効かせる）
  - #342: 借株照会の可否（成立しなければ空売りは恒久的に無効）
  - 未起票: 信用買いの建玉表現・信用金利・必要証拠金
  - ~~計画側の裁定待ち: 禁止銘柄・市場ガードの手仕舞い適用~~ → **✅ 2026-08-04 裁定済み（全注文適用で確定・実装は一致）**（#380）

## 関連

- 起点 issue: [#332](https://github.com/endazon/ai-stock-trading/issues/332)
- 計画 ADR: ADR-0016（決定1・8・13）・ADR-0007（取引ガードのソフト設定・禁止銘柄）・ADR-0009（手仕舞い不停止）
- 実装 ADR: [IADR-0131](./IADR-0131_short-selling-controls-fail-closed.md)（空売り統制）・
  [IADR-0004](./IADR-0004_position-effect-entry-scoping.md)（建玉効果でのエントリー判定）・
  [IADR-0119](./IADR-0119_decision-derived-close.md)（判断由来の建玉効果）・
  [IADR-0086](./IADR-0086_frontend-guard-edit-ui.md)（画面の数値 enum 表現）・
  [IADR-0127](./IADR-0127_plan-conformance-known-deviation-registry.md)（既知逸脱レジストリ）
- 計画への環流: [feedback/20260804_fr19-guard-scope.md](../../feedback/20260804_fr19-guard-scope.md)
  （論点 1: 決定4 の明示化依頼 ／ 論点 2: 禁止銘柄ガードの Close 適用の裁定 ／ 論点 3: §2.2 の更新）
- 仕様書: [作業仕様書 20260804_332](../specs/20260804_332_trading-guards.md)・
  [機能仕様書 FR-19](../functional/FR-19_trading-guard.md)・
  [テスト仕様書 FR-19](../tests/FR-19_trading-guards-tests.md)
