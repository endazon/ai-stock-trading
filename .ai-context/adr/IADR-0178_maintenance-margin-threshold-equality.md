---
title: 維持率の判定は新規建ての拒否・自動縮小の発動とも「維持率 ≦ 適用閾値」で成立させる
status: Accepted
related_ids: [FR-10, UC-06, ADR-0016, IADR-0133, IADR-0160, IADR-0178]
author: endazon (with Claude Code)
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
---

# IADR-0178: 維持率の等号を 2 つの統制で揃える

## 背景

計画の裁定（**ADR-0016 決定7 の 2026-08-07 確定**・利用者裁定 質問票 第 14 回 Q6・裁定依頼 [planning#240](https://github.com/endazon/project-planning/issues/240)）が定めた。

> **`≦` へ揃える。新規建ての拒否は「維持率 ≦ 適用閾値」で拒否する**（従前の記述は「割り込む」＝ `<` と読めた）。自動縮小（UC-06）は従前どおり **`≦` で発動**する。揃えないと、**維持率がちょうど閾値のときに縮小が決済を出している最中の口座へ新規空売りが承認される**。**非対称を意図として残す案は採らない** —— 幅は等号の 1 ケースだが、統制が自ら作った状態の上で別の統制が反対向きに働くことになり、説明がつかないためである。

**この裁定は本リポの [IADR-0160](IADR-0160_maintenance-margin-applied-threshold-account-wide.md) が環流したものである。** 同 IADR は残余リスクとして次を書き残していた。

> **閾値ちょうどの維持率では、縮小が発動しながら新規建てが通る。** …… **本 issue は閾値の値の食い違いを直すものであり、この等号の非対称は範囲外**として変更しなかった（どちらも計画由来であり、片方を動かすと既存の境界テストが定めた解釈を実装判断で覆すことになる）。環流して裁定を仰ぐ。

## 着手前の実測（2026-08-08）

| 統制 | 位置 | 等号 |
| --- | --- | --- |
| 自動縮小の発動 | `MaintenanceMarginReducer.Plan`（[IADR-0133](IADR-0133_maintenance-margin-auto-reduce.md) 決定3） | `ratio > threshold` で見送り ＝ **`≦` で発動** |
| 新規建ての拒否 | `ShortSellEvaluator`（規則 (4)） | 🔴 **`ratio < threshold`** |

## 決定

### 決定1: 拒否側を `<=` へ揃える

`ShortSellEvaluator` の比較を `ratio < threshold` → **`ratio <= threshold`** とする。**変更は比較演算子 1 つである。**

### 決定2: 🔴 本 PR（pin 前進）の範囲に含める

**[#459](https://github.com/endazon/ai-stock-trading/issues/459)（計画 pin の前進）は「棚卸しして個別 issue へ切り出す」ことを範囲とし、実装は「やらないこと」に挙げている。** それでも本件を含めた理由は次のとおりである。

- **本件は新規 ADR 3 本の実装ではない。** issue が除外したのは ADR-0026 / 0027 / 0028 の実装であり、本件は**既存 ADR-0016 の裁定への追随**である。
- **[IADR-0160](IADR-0160_maintenance-margin-applied-threshold-account-wide.md) が「範囲外」と書いた理由が消滅している。** 当時の理由は「どちらも計画由来であり、片方を動かすと既存の境界テストが定めた解釈を実装判断で覆すことになる」であった。**裁定が下りた以上、動かすのは実装判断ではない。**
- **向きは統制が厳しくなる側である。** 誤って倒しても新規建てが 1 ケース多く止まるだけであり、緩む向きではない。
- **別 issue へ送ると「裁定は済んだが実装は `<` のまま」という状態が残る。** それは pin を進める作業そのものが作り出した乖離であり、**作った側で閉じないと、次に読む人はこの非対称が裁定前のものか裁定後のものか区別できない。**

### 決定3: 拒否と縮小の**一致そのもの**をテストで固定する

境界値テストを片方ずつ持つだけでは足りない。**`閾値ちょうどでは拒否と自動縮小がともに成立する`（T-10-268）を新設し、適用閾値の直下・ちょうど・直上の 3 点で「拒否」「縮小」「両者の一致」の 3 つを主張する。**

**これが本件から取るべき教訓である。** 非対称は 2026-08-07 の裁定より前から実在していたが、**両者を並べて比べるテストが無かったために「残余リスクの文章」としてしか存在しなかった** —— 文章は CI を赤くしない。片方ずつの境界値テストは、**どちらか一方の等号を動かしても両方とも緑のまま**である。

### 決定4: 期待を反転させたテストには、その事実をコメントで残す

反転したのは 2 か所である。

| テスト | 従前 | 変更後 |
| --- | --- | --- |
| `建玉が一件のときの適用閾値は従前の単一建玉の式と一致する`（T-10-248） | 閾値ちょうどは**通す** | 閾値ちょうどは**拒否**（＋直上で通すことを追加） |
| `維持率は適用閾値の境界で切り替わる`（`delta: 0`） | `expectedRejected: false` | **`true`** |

**「最初からこうだったのではない」と読めるようにする**（[IADR-0177](IADR-0177_no-solution-fail-closed-uniformity.md) 決定と同じ規律）。期待の変更を黙って行うと、**後から見た人は「テストがそう書いてあるのだから、そういう仕様だったのだろう」と読む** —— 変更の是非を検討する機会そのものが消える。

## 結果

| 統制 | 変更後 |
| --- | --- |
| 自動縮小の発動 | **`≦`**（不変） |
| 新規建ての拒否 | **`≦`** |

**維持率がちょうど適用閾値のとき、新規空売りは拒否され、同時に自動縮小が発動する。** 拒否理由は `MaintenanceMarginBreach`（ADR-0016 決定10）である。

### 対照実験（緑 → 赤 → 緑）

| 段階 | 結果 |
| --- | --- |
| 実装前（`MaintenanceMargin` 系） | **44 passed / 0 failed**（緑） |
| 実装後・テスト書き換え前 | **40 passed / 4 failed** —— `建玉が一件のときの…` の Theory 4 ケースだけが赤 |
| 同・Domain 全体 | さらに `維持率は適用閾値の境界で切り替わる(delta: 0)` **1 件**が赤（狙いどおり 2 か所のみ） |
| テスト書き換え後 | **676 passed / 0 failed**（緑） |

**赤くなったのが 2 か所だけであったことが、等号がこの 2 か所にしか書かれていないことの実測でもある。**

## 残余リスク

1. **本統制は依然として発火しない。** 維持率の束（`MaintenanceMarginSnapshot`）の供給経路が無く、実際の口座では常に fail-closed 側へ落ちる（[#331](https://github.com/endazon/ai-stock-trading/issues/331) / [#342](https://github.com/endazon/ai-stock-trading/issues/342) 待ち）。**等号を揃えたことの効果は、供給が入るまで観測できない。**
2. **信用買い（マージンロング）側の拒否経路は本 IADR の対象外である。** `ShortSellEvaluator` は空売りの評価器であり、信用買いの新規建てに同じ閾値判定が掛かる経路は現時点で存在しない。**適用閾値の算式（`MaintenanceMarginPolicy`）は商品種別を見て条文を選ぶが、その閾値で新規建てを止める側は空売りにしかない。** 供給が入る時点で改めて見る必要がある。
3. **`≦` を「安全側だからもっと広げる」方向へは動かしていない。** 裁定が定めたのは等号の一致であって余裕幅の追加ではない。直上（`閾値 + 0.01`）で通ることをテストで固定し、**将来 `≤ 閾値 + α` へ滑ることを塞いだ。**

## 起点・関連

- 起点 issue: [#459](https://github.com/endazon/ai-stock-trading/issues/459)（計画 pin の前進。**本件はその棚卸しで見つかった**）
- 作業仕様書: [20260808_459_planning-pin-advance.md](../specs/20260808_459_planning-pin-advance.md) 検査5
- 起点の裁定: **ADR-0016 決定7 の 2026-08-07 確定**（質問票 第 14 回 Q6・裁定依頼 [planning#240](https://github.com/endazon/project-planning/issues/240)）。**その裁定の起点は本リポの [#420](https://github.com/endazon/ai-stock-trading/issues/420)**（[IADR-0160](IADR-0160_maintenance-margin-applied-threshold-account-wide.md) 残余リスクから環流）
- 前提: [IADR-0133](IADR-0133_maintenance-margin-auto-reduce.md)（自動縮小・`≦` で発動）／[IADR-0160](IADR-0160_maintenance-margin-applied-threshold-account-wide.md)（適用閾値の口座単位化）
- テスト: `MaintenanceMarginAppliedThresholdTests`（T-10-248・**T-10-268**）／`ShortSellingControlsTests`（`維持率は適用閾値の境界で切り替わる`）
