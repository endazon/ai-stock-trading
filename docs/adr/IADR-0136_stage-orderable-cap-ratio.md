---
title: IADR-0136 段階の発注可能額を固定額から総資金比へ改め、SIMULATE プロファイルの段階上限の差し替えを廃止する
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-10, FR-12, FR-17, UC-06, ADR-0008, ADR-0016, IADR-0005, IADR-0041, IADR-0108, IADR-0127, IADR-0130]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# IADR-0136: 段階の発注可能額を固定額から総資金比へ改め、SIMULATE プロファイルの段階上限の差し替えを廃止する

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: 実装（Claude Code）／ 起点 issue [#333](https://github.com/endazon/ai-stock-trading/issues/333)（親 [#344](https://github.com/endazon/ai-stock-trading/issues/344)）
- 作業仕様書: [20260804_333_stage-gate](../specs/20260804_333_stage-gate.md)

## コンテキストと課題

計画 [05_trading-assumptions §5](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md)
「運用段階（Stage）」は Stage 2（最小実弾）の発注可能額を次のとおり定める。

> Stage 2（最小実弾）＝総資金の **30%（$900）**。口座には総資金 $3,000 を入れ、**発注可能額をシステム側の
> 統制で 30% に制限する**（口座への入金額は制限しない）

実装は `StageSettings.CapitalCap` を**基準通貨（円）建ての固定額 35,000** で保持していた。この値は旧資金
100,000 円を前提とした暫定既定（[IADR-0041](IADR-0041_stage-gate-transitions.md)）であり、
2026-07-31 の増資（$3,000＝約 491,100 円）後の計画値とは整合しない。計画適合レジストリ
（[IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)）に `Stage.Stage2OrderableCapRatio`
として登録されていた逸脱である。

同型の問題は金額系の統制上限（1 注文 / 1 日）で既に解決済みであり、
[IADR-0130 決定1](IADR-0130_equity-ratio-risk-limits.md) が理由を残している——**固定額で持つと増資のたびに
書き換えが要り、書き換え漏れが「資金だけ増えて上限が据え置き」を生む。**

## 検討した選択肢

1. **固定額のまま値だけ 147,330 円（＝$900 相当）へ直す** — 計画の数値には一致するが、増資のたびに同じ
   書き換えが再発する。計画が明示的に「割合で持てば資金の増額に応じて各上限値が比例的に調整される」と
   書いている以上、固定額での追随は表面的である。
2. **`CapitalCap`（固定額）と `CapitalCapRatio`（比率）を両方持つ** — 移行は楽だが、同じ事実が 2 箇所に
   表現され、どちらが正かが判定側に委ねられる（`ShortSellSettings.Enabled` の二重表現が
   [IADR-0132 決定2](IADR-0132_product-type-tri-state-and-guard-scope.md) で問題になったのと同型）。
3. **`CapitalCapRatio`（総資金比）だけを持ち、判定時に equity から解決する** — `RiskLimitSettings` と
   同じ規律に揃う。

## 決定

**選択肢 3 を採用する。**

### 決定 1: `StageSettings` は総資金比だけを持ち、解決はメソッドを通す

`StageSettings(Stage, Mode, CapitalCapRatio)` とし、`OrderableCapFor(decimal equity)` だけが比率から
金額を解決する。**呼び出し側で `equity × 比率` と書かない**——equity の定義（どの時点の値か）が呼び出し側ごとに
ぶれるためである（IADR-0130 決定1 と同じ理由）。

| Stage | 比率 | 根拠 |
| --- | --- | --- |
| Stage 0（検証） | `1.00` | 実弾なし。段階としての金額の絞りは無い |
| Stage 1（SIMULATE） | `1.00` | 同上 |
| Stage 2（最小実弾） | **`0.30`** | 計画 §5「総資金の 30%（$900）」 |
| Stage 3（段階増額） | `1.00` | 計画 §5「最大 100%」。増額は月報レビュー時に FR-17 設定で確定する |

判定に用いる equity は `PortfolioSnapshot.Capital`（＝前営業日終値時点の評価額・当日中は不変）であり、
**FR-10 の金額上限と同一の基準**である。基準がばらけると「厳しい方が効く」という計画の規則
（§5 注記）が比較として成り立たない。

### 決定 2: SIMULATE プロファイルの段階上限の差し替えを**廃止**する

[IADR-0108](IADR-0108_simulator-risk-profile.md) の SIMULATE プロファイルは、ペーパー段階の資金上限を
プロファイル値（¥170,000,000）へ引き上げる `ApplyToPaperStage` / `CreateStagePolicy` を持っていた。
**比率はスケール不変であるため、これは不要になった**——基準資金をプロファイル値へ注入すれば、
段階の発注可能額は比例して自動的に上がる。IADR-0130 決定6 が金額系の上限について同じ論法で
オーバーライドを削除したのと同一の帰結である。

副次的な効果として、IADR-0108 決定4 の不変条件「**検証用プロファイルが実弾段階の上限を緩められる経路を
作らない**」が**構造的に**成立する——差し替える対象そのものが無い。従来は「実弾段階だけ除外する」条件分岐が
正しく書かれていることに依存していた。

`SimulatorProfileRiskSettingsStore` は**素通しだけになるが型としては残置する**。プロファイルの適用点が
将来また必要になったときの単一の場所であり、消すと次に必要になったとき配線ごと復元する羽目になる。

### 決定 3: 表示・契約に載る値も比率へ改める

Discord の段階ゲート現況（`HttpStageGateController`）は「資金上限: N 円」から
「**発注可能額: 総資金の N%**」へ改める。DTO も `CapitalCapRatio` とする。**固定額のまま表示すると、
統制の実体（比率）と人が読む値（金額）が食い違い、増資後に古い金額が表示され続ける。**

### 決定 4: テストは「絶対額の意図」を保ったまま比率へ翻訳する

既存テストの多くは「この金額で止まる／止まらない」を検証している。ヘルパで
`ratio = 目標金額 ÷ equity` へ翻訳し、**検証の意図（金額の大小関係）を保つ**。あわせて、
equity を 3 水準（¥100,000 / ¥491,100 / ¥982,200）で振って **Stage 2 の上限が総資金に比例する**ことを
`[Theory]` で固定する——「資金だけ増えて上限が据え置き」という、そもそも比率化が防ごうとした事故を
テストで塞ぐ。

## 結果

- **良い影響**: 増資のたびの書き換えが不要になり、書き換え漏れによる「資金だけ増えて上限が据え置き」が
  構造的に起こらない。SIMULATE プロファイルの条件分岐が 1 本消え、実弾段階の上限を検証用フラグで
  動かせないことが構造的に保証される。計画適合レジストリから `Stage.Stage2OrderableCapRatio` が解消した。
- **悪い影響 / トレードオフ**:
  - `StageSettings` は**破壊的変更**である（`CapitalCap` → `CapitalCapRatio`）。永続化はされていない
    （段階設定は `StageGatePolicy` の既定から供給される）が、Discord の DTO は形が変わる。
    Notification と Risk は同じリポジトリで同時に配備されるため、段階的移行は要らないと判断した。
  - **Stage 0 / Stage 1 / Stage 3 の比率 `1.00` は「段階としての絞りが無い」という意味であり、
    無制限ではない**。実効的な上限は FR-10 の統制上限（1 注文 25% / 1 日 150% / 保有建玉数 3）が担う。
    旧実装の Stage 0/1 の上限は「初期投入資金」＝ equity と同額であり、比率 `1.00` は同値である。
- **残余リスク**: Stage 3 の「最大 100%」は計画が段階的増額を月報レビューに委ねているため、
  比率 `1.00` は**上限の上限**である。実際の増額運用（FR-17 設定での逐次引き上げ）は未実装であり、
  現状は Stage 3 へ昇格した時点で FR-10 の上限まで一気に開く。段階増額の運用は本 issue の範囲外である。

## 関連

- 計画: [05_trading-assumptions §5](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md)（運用段階・金額系上限 3 値の注記）／
  [06_daytrading-review §4](../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md)
- 実装 ADR: [IADR-0130](IADR-0130_equity-ratio-risk-limits.md)（金額系上限の equity 比化・同じ論法）／
  [IADR-0108](IADR-0108_simulator-risk-profile.md)（SIMULATE プロファイル・決定4 の不変条件）／
  [IADR-0005](IADR-0005_stage-capital-cap-definition.md)（段階資金上限は累計で判定する）／
  [IADR-0041](IADR-0041_stage-gate-transitions.md)（旧・暫定既定 35,000 の一次記録）／
  [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)
- 仕様書: [作業仕様書 20260804_333](../specs/20260804_333_stage-gate.md)／
  [FR-20 機能仕様書](../functional/FR-20_staged-gates.md)／[FR-20 テスト仕様書](../tests/FR-20_staged-gates-tests.md)
