---
title: 計画書ダイジェストは計画確定値の出典を網羅し、網羅そのものをテストで主張する
status: Accepted
related_ids: [NFR, FR-10, FR-19, FR-20, UC-06, ADR-0021, IADR-0127, IADR-0166, IADR-0172, IADR-0179]
author: endazon (with Claude Code)
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md
---

# IADR-0179: ダイジェスト表の網羅を機械的に主張する

## 背景

[IADR-0166](IADR-0166_plan-source-digest.md) は「計画書 → `PlanRiskDefaults`」という**人手の 1 ホップ**を見るためにダイジェスト表を導入した。[IADR-0172](IADR-0172_plan-risk-defaults-value-level-conformance.md) は「表 ⇄ 実装」を**値のレベル**まで下ろした。

**しかしどちらも、表そのものが計画確定値の出典を網羅しているかは見ていなかった。**

## 🔴 実測して分かったこと（2026-08-08・[#459](https://github.com/endazon/ai-stock-trading/issues/459)）

pin 前進の作業仕様書に「同一だった 6 節はハッシュ一致を根拠に突き合わせを省いた」と書き下ろした際、**その論法の前提（全キーの出典が表に掛かっていること）を実測した**。成り立っていなかった。

`PlanRiskDefaults` の 45 行が挙げる出典を機械的に数えると、**3 文書が表に無かった**。

| キー | 表に無かった出典 | 表に掛かる出典 |
| --- | --- | --- |
| **`Stage.Values`** | **FR-20** | 🔴 **無し** |
| `Guard.PreventSameDayReentry` | ADR-0021 決定4-1 | §5 |
| `ShortSell.Limits` | UC-06 | ADR-0016 決定 |
| `BrokerProvider.Values` / `Stage.Stage1BrokerProvider` | FR-20 | §5 |

**しかもその 3 文書は、いずれも同じ pin 前進で変更されていた**（`02_requirements` +21 行 ＝ FR-10 の維持率と FR-21 の新設／`03_usecases` +5 行 ＝ UC-06 の等号／ADR-0021 +21 行 ＝ 決定4-5 の GFV 解除条件）。

**それでもダイジェスト表は 1 件も赤くならなかった。**

## 決定

### 決定1: 表に無かった 3 節を登録する

`02_requirements §機能要求`（FR-10 / FR-19 / FR-20 の表）・`UC-06`・`ADR-0021 決定` を追加する。

**節の粒度は既存と同じく「見出し単位」である。** `02_requirements` は FR ごとの見出しを持たない 1 つの表であるため `## 機能要求` 節ごと登録する。粗いが、**ファイル全体ハッシュ（変更履歴の追記で毎回赤くなる。IADR-0166 決定2 が棄却したもの）よりは細かい**。

### 決定2: 🔴 網羅そのものをテストで主張する

**3 節を足すだけでは、次に誰かが新しい出典の行を足したときに同じ穴が空く。** `ダイジェスト表は計画確定値の出典を網羅している` を新設し、`PlanRiskDefaults` の `Citation` に現れる計画 ID（`ADR-\d{4}` / `FR-\d+` / `UC-\d+` / `05_trading-assumptions §x`）が**すべて**表のいずれかへ対応することを主張する。

**対応表は「無い出典を覚えておく場所」ではなく、対応が取れることを機械的に主張する場所である。** 対応先の `Citation` が実在することも同時に検査する（対応表の側が腐るのを防ぐ）。

### 決定3: 免除は理由つきでのみ認める

**すべての ID が値の出典であるとは限らない。** 現在の唯一の免除は `ADR-0026` —— `ShortSell.BorrowRateCapAnnual` の注記が「単位が PoC 項目 9 待ちである」と述べているだけであり、**20% という値の出典は ADR-0016 決定3 である**。

**免除リストは `Reason` を必須の項目として持つ。** 理由の無い免除を許すと、**赤を消すための置き場**になる —— それは本テストが塞いだ穴を、より見えにくい形で作り直すことになる。

### 決定4: 本テスト自身を変異検査で確かめる

**網羅を主張するテストが「実は何も検査していない」なら、穴が 1 段深くなるだけである。** ADR-0021 の登録行を 1 行コメントアウトして実走した。

| 段階 | 結果 |
| --- | --- |
| 3 節を登録した状態 | **17 passed / 0 failed** |
| ADR-0021 の行を落とす | **本テストのみ赤** |
| 復旧 | **17 passed / 0 failed** |

## 結果

- **`PlanRiskDefaults` の全 45 行の出典が、ダイジェスト表のいずれかの節に掛かっている。**
- **登録漏れのまま新しい出典の行を足せば赤くなる。**
- 検査2 の論法（「同一だった節はハッシュ一致を根拠に突き合わせを省く」）が、**初めて前提つきで正当化された。**

## 残余リスク

1. **`02_requirements §機能要求` の粒度は粗い。** FR-10 の 1 文字が変わっても、FR-20 由来の `Stage.Values` を読み直す指示として赤くなる。**赤の意味が「この節のどこかが変わった」まで薄まる** —— ただし **IADR-0166 決定1 の位置づけは「気づく機会を作る」ことであり、読み直す範囲が広いことは検知漏れより軽い。**
2. **`Citation` の文字列に ID が書かれていない出典は拾えない。** 本テストが見るのは `PlanDefault.Citation` の**表記**であり、実際にどの文書から転記したかではない。**出典を書かずに値だけ足せば素通りする**（`PlanDefault` は `Citation` を必須にしているが、中身の妥当性までは検査していない）。
3. **免除リストが増え始めたら、それ自体が兆候である。** 現在 1 件。**「値の出典ではない ID」が注記に増えるほど、`Citation` が出典欄と注記欄を兼ねていることの無理が出る** —— 分離するかは、増えた時点で改めて判断する（今は 1 件のために型を割らない）。

## 起点・関連

- 起点 issue: [#459](https://github.com/endazon/ai-stock-trading/issues/459)（計画 pin の前進。**本件はその作業中に前提を実測して見つけた**）
- 作業仕様書: [20260808_459_planning-pin-advance.md](../specs/20260808_459_planning-pin-advance.md) 検査6
- 前提: [IADR-0166](IADR-0166_plan-source-digest.md)（ダイジェスト機構そのもの）／[IADR-0172](IADR-0172_plan-risk-defaults-value-level-conformance.md)（値レベルの照合）／[IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)（既知逸脱の登録簿）
- 同じ PR の別決定: [IADR-0178](IADR-0178_maintenance-margin-threshold-equality.md)（維持率の等号）
