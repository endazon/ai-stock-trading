---
title: 最小期待利益に解が無い領域は、全経路で「解無し（null）」に揃える
status: Accepted
related_ids: [FR-17, FR-10, UC-04, ADR-0016, IADR-0173, IADR-0177]
author: endazon (with Claude Code)
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0177: 解が無い領域の振る舞いを 3 経路で揃える

## 背景

計画の裁定（**[project-planning#289](https://github.com/endazon/project-planning/issues/289)**・**2026-08-08 確定**）が、最小期待利益のしきい値について次を定めた。

> **解が無い領域では見送る。** 分母は `1 − 倍率 × 税率` であり、**`倍率 × 税率 ≥ 1` になるとしきい値に解が無い**。この領域に入った場合は**採算不能とみなし、当該取引を見送る**（fail-closed）。**負のしきい値を返して全通過させることは、いかなる経路でも行わない。**
>
> **［実装状況 2026-08-08］向き（通過させない）は 3 経路とも既に一致しているが、振る舞いは揃っていない。** …… **費用算出は例外を送出しており、文字どおりの「見送り」ではない**。**揃え方は実装設計の範囲とする。**

**「揃え方は実装設計の範囲とする」** —— 本 IADR がその設計判断を記録する。

> ⚠️ **この裁定は現在の submodule pin（`a4616a8`）に入っていない。** 裁定を含む計画側コミットは **`2791e4f`** であり、pin はそれより前である。**したがって上記 `plan_refs` を辿っても裁定文は読めない。** [IADR-0175](IADR-0175_security-spec-absence-notation.md) の規律（未確認・未反映を空欄にせず書く）に従い明記する。pin 更新は [#459](https://github.com/endazon/ai-stock-trading/issues/459) の担当であり、**本作業はそれと独立に成立する**（コードの穴は pin と無関係に存在する）。

### 着手前の実測（2026-08-08）

| # | 経路 | 変更前 | 裁定と揃うか |
| --- | --- | --- | --- |
| 1 | `MinimumExpectedProfit.Threshold`（共有契約・式の単一情報源） | `null` | ✅ |
| 2 | `ProfitabilityGate.Evaluate`（採算ゲート） | `Indeterminate` | ✅ |
| 3 | `CostCalculator.MinimumViableProfit`（費用算出） | **`InvalidOperationException`** | 🔴 |

## 決定

### 決定1: 費用算出は `decimal?` を返し、解無しは `null` とする（例外を送出しない）

`CostCalculator.MinimumViableProfit` の戻り値を `decimal` から **`decimal?`** へ変え、解が無ければ `null` を返す。

**なぜ例外を残さないか。** 例外も「通過させない」点では安全側であり、「例外でも fail-closed だからよい」と済ませたくなる。**しかし壊れ方が違う。** 見送りは*その取引を見送って処理は続く*のに対し、例外は*その呼び出しを含む処理ごと落ちる*。費用算出は設定サービスの Domain にあり、**公開面（API・画面）から呼ばれれば構成異常が 500 として現れる**。裁定が「文字どおりの見送りではない」と書いているのはこの差である。

**なぜ `decimal?` か（`TryGet` 形・番兵値ではなく）。**

| 案 | 採否 | 理由 |
| --- | --- | --- |
| **`decimal?`** | **採用** | **共有契約 `Threshold` が既に `decimal?` である。** 同じ「解無し」を同じ型で表せば、経路をまたいで読む人が対応を取り違えない |
| `bool TryGet(out decimal)` | 不採用 | 戻り値を無視しても**コンパイルが通る**。`null` は算術に混ざらないが、`out` の値は初期値のまま使えてしまう |
| 番兵値（`decimal.MaxValue` 等） | **不採用** | 裁定の要請は「負のしきい値で全通過させない」ことである。**番兵値は数値として扱えてしまうこと自体が再発経路**になる（比較・加減算に紛れ込む） |

### 決定2: 呼び出し側で `null` を既定値・0 で埋めない

`null` は**「未設定」ではなく「見送り」**である。既定値で埋める・0 とみなすのは**どちらも「全通過」と同じ結果**になる。型の XML ドキュメントにこの意味を明記する。

**実測で production の呼び出し元は 0 件だった**（テスト 2 か所のみ）。**公開面が生える前に直すのが最も安い。**

`ActualDefaults`（[IADR-0166](IADR-0166_plan-source-digest.md) の計画書ダイジェスト）は `null` で**明示的に失敗させる**。ここが最も危ない —— `null` と `decimal` を `>` で比べると**常に false** になり、「基準に税が入っていない」というダイジェストを**静かに**生んで、検知の仕組みそのものを無力化する。

### 決定3: 🔴 非正の倍率も解無しへ倒す（実装中に見つけた欠陥への対処）

**本作業で追加したテストが、裁定の要請に反する実在の欠陥を見つけた。**

費用も税率も正規化されるため、**負のしきい値を生み得るのは倍率だけ**である。しかも**負の倍率では分母 `1 − m × r` が 1 より大きくなり、解無し判定（分母 `<= 0`）をすり抜ける** —— 例えば `m = −2` / `r = 0.20315` では分母 `1.406 > 0` となり、**負のしきい値がそのまま返る＝全通過**であった。

採算ゲートは呼び出し**前**に `m <= 0` を弾いていたため露見せず、**費用算出だけが素通ししていた**。

**対処は共有契約の側に置く**（`multiple <= 0m` なら `null`）。**同じ式を守る規則は式の側に置く** —— 呼び出し側ごとの防御は、**片方だけ抜ける**。実際にそうなっていた。

### 決定4: 単一情報源に**直接の**テストを与える

**`MinimumExpectedProfit` には直接のテストが無かった**（呼び出し側 2 経路のテスト越しの間接検査のみ）。**「3 経路が同じ意味を返す」ことを求めるなら、基準となる 1 経路が最も強く固定されていなければならない。**

`MinimumExpectedProfitTests` を新設し、境界（直前・ちょうど・直後）・**負のしきい値が全域で返らないこと**・正規化・退化を固定する。**決定3 の欠陥はこのテストが見つけた** —— 間接検査だけでは、呼び出し側が偶然同じ結論へ落ちている場合と区別できない。

## 結果

| # | 経路 | 変更後 |
| --- | --- | --- |
| 1 | `MinimumExpectedProfit.Threshold` | `null`（**非正の倍率も含む**） |
| 2 | `ProfitabilityGate.Evaluate` | `Indeterminate` |
| 3 | `CostCalculator.MinimumViableProfit` | **`null`** |

**3 経路が同じ意味を返し、どの経路も例外を送出せず、どの入力でも負のしきい値を返さない。**

### 対照実験（緑 → 赤 → 緑）

`CostCalculatorTests` の `倍率と税率の積が1以上なら解が無く例外になる` が**例外であること自体を固定していた**ため、そのまま対照に使えた。

| 段階 | 結果 |
| --- | --- |
| 実装前 | **10 passed / 0 failed**（緑） |
| 実装後・テスト書き換え前 | **9 passed / 1 failed** —— 狙ったテストだけが赤 |
| テスト書き換え後 | **18 passed / 0 failed**（緑） |

⚠️ **期待を変更したため、その事実をテストのコメントに残した**（「最初からこうだったのではない」と読めるように）。

## 残余リスク

1. **入口の検証は本作業に含めない。** 倍率・税率を**設定時に**弾く統制は別である（**出口で見送るだけでは、構成異常そのものが運用者に見えない**）。可観測性（警告ログ・設定画面での表示）は未着手。
2. **pin が進むまで、本 IADR の `plan_refs` から裁定文は読めない**（[#459](https://github.com/endazon/ai-stock-trading/issues/459)）。
3. **決定3 の欠陥が「いつから在ったか」は追っていない。** 式が共有契約へ移る前（[IADR-0173](IADR-0173_minimum-expected-profit-tax-inclusive.md)）からの可能性がある。**現在の穴を塞いだだけであり、過去に負のしきい値で通った取引があったかは調べていない**（ペーパートレード段階であり実損は生じ得ないが、**調べていないことは事実として残す**）。

## 起点・関連

- 起点 issue: [#461](https://github.com/endazon/ai-stock-trading/issues/461)
- 作業仕様書: [20260808_461_minimum-viable-profit-fail-closed.md](../specs/20260808_461_minimum-viable-profit-fail-closed.md)
- 起点の裁定: **[project-planning#289](https://github.com/endazon/project-planning/issues/289)**（planning `2791e4f`）。**その裁定の起点は本リポの [#358](https://github.com/endazon/ai-stock-trading/issues/358)**（[IADR-0173](IADR-0173_minimum-expected-profit-tax-inclusive.md) から `/plan-feedback` で環流）
- 前提: [IADR-0173](IADR-0173_minimum-expected-profit-tax-inclusive.md)（式の単一情報源・基準は往復費用＋税）
- pin 更新: [#459](https://github.com/endazon/ai-stock-trading/issues/459)
