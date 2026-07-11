---
title: IADR-0037 ドテン/部分決済は取引判断が符号付きポジションのゼロ跨ぎで Close+Open に分解する
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-05, FR-10, FR-19, ADR-0003, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
---

# IADR-0037: ドテン/部分決済は取引判断が符号付きポジションのゼロ跨ぎで Close+Open に分解する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-04（取引判断が注文意図を生成）、FR-05（発注執行）、FR-10（エントリー専用リスク統制）、FR-19（取引ガード）、ADR-0003（独立リスク管理）、ADR-0007（信用有効化・差金決済防止・相場操縦ガード）
- 対象 Issue: [#50](https://github.com/endazon/ai-stock-trading/issues/50)（`Refs #25` の [IADR-0004](IADR-0004_position-effect-entry-scoping.md) フォローアップ）
- 関連する実装仕様書: [20260711_order-decomposition](../specs/20260711_order-decomposition.md)
- 関連 IADR: [IADR-0004](IADR-0004_position-effect-entry-scoping.md)（建玉効果でエントリー判定）、[IADR-0033](IADR-0033_shared-inventory-fold.md)（符号付き在庫の畳み込み）、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)（net 1 建玉の射影）、[IADR-0015](IADR-0015_stop-loss-mechanical-close.md)（損切りは純 Close・分解対象外）、[IADR-0026](IADR-0026_audit-deterministic-correlation.md)（決定的 UUID 相関）、[IADR-0035](IADR-0035_stop-loss-authoritative.md)（net 建玉に最新エントリーの損切り価格）、[IADR-0017](IADR-0017_trade-decision-structure.md)（サイジング）

## コンテキストと課題

[IADR-0004](IADR-0004_position-effect-entry-scoping.md) で `OrderIntent` に建玉効果 `PositionEffect`（Open/Close）を導入し、エントリー専用のリスク統制
（kill switch・段階資金上限・1 注文/日次金額上限・保有数上限・同日再エントリー・差金決済防止・相場操縦ガード等）を建玉効果で判定するようにした。
しかし **ドテン**（同一銘柄で保有を決済しつつ同時に逆方向へ新規建て）や **部分決済** を、`Close`＋`Open` の注文へどう分解するかは未確定で、
IADR-0004 のフォローアップに「別途 IADR 化する」と明記されている。

分解方針が未定だと、信用有効化後（[ADR-0007](../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md)）のショート⇄ロング転換・部分手仕舞いで
`PositionEffect` の設定が曖昧になり、エントリー専用制約の適用可否がぶれる。特に反転を単一の相殺注文で表すと、**新規建て部分**（逆張りの新ポジション）に
エントリー統制（kill switch・資金上限・差金決済防止・相場操縦ガード）が正しく効かず、計画の受け入れ基準「kill switch 起動後、新規発注が一切行われない」を
すり抜ける構造的欠陥が残る（IADR-0004 と同じ穴の再発）。

なお現状のコード（`TradeDecisionService`）は現物のみ（`ProductType.Cash`）でロング保有前提のため、売買判断を常に `PositionEffect.Open` で組み立てている。
保有と突き合わせた Open/Close の切替や反転の分解ロジックは未実装であり、本 IADR で方針を確定し、信用有効化スライスで実装する。

## 検討した選択肢

### A. 単一の相殺（ネッティング）注文にする

反転・部分決済を「差引き後の 1 注文」として、単一の `PositionEffect` を付けて発注する（例: ロング 100 → ショート 50 を「Sell 150」1 本）。

- 却下。差引き 1 本では、決済部分と新規建て部分が混在し `PositionEffect` を一意に決められない。エントリー統制を新規建て部分だけに適用できず、
  Close 相当部分にまで掛かる（過剰ブロック）か、Open 相当部分をすり抜ける（過小適用）。IADR-0004 が売買方向から建玉効果を分離した目的（統制の正確な適用）を崩す。
  差金決済防止・相場操縦ガードも新規建て部分を独立に評価できない。

### B. 発注執行（ブローカ）層で分解する

取引判断は差引き注文を出し、発注執行アダプタが Close/Open に割る。

- 却下。リスク管理のスクリーニング（`RiskEvaluator`・エントリー統制）は**取引判断と発注執行の間**にあり、分解後の 2 脚を別々に検証する必要がある。
  執行層が分解しても、その手前のスクリーニングは分解前の差引き注文しか見られない。加えて執行層はサイジング・目標ポジション・保有文脈を持たず、分解の一次情報を欠く。
  IADR-0004 の「注文生成側が建玉効果を最も正確に知る」という前提にも反する。

### C. リスク管理コアが保有スナップショットから Open/Close を推論する

`RiskEvaluator`（判定コア）が保有と突き合わせて効果を推定し、必要なら分解する。

- 却下。これは IADR-0004 で明示的に却下した選択肢（保有からの推論）と同じで、反転・部分決済で曖昧になり、判定コアがポートフォリオ照合ロジックを抱える。
  判定コアは「受け取った建玉効果を読むだけの純関数」を保つべき（IADR-0004 の決定）。

### D. 取引判断（生成側）が符号付きポジションのゼロ跨ぎで分解する（採用）

取引判断サービスが、当該銘柄の現在ネット建玉と目標（サイジング済み注文数量）から、遷移をゼロ点で分割して `Close`／`Open` の意図を組み立てる。

- 採用。IADR-0004 の「生成側が建玉効果を確定情報として持つ」に一致し、各 `OrderIntent` が一意な `PositionEffect` を帯びてスクリーニングへ流れる。
  会計側の畳み込み（[IADR-0033](IADR-0033_shared-inventory-fold.md) `SignedInventory.Apply`）が既に反転を「全決済＋余りを新規建て」で扱っており、その**生成側ミラー**として整合する。

## 決定

**選択肢 D** を採用する。分解は取引判断サービス（注文生成側）が行い、判定コア・執行層は分解しない。

### 分解則（符号付きポジションのゼロ跨ぎ分割）

当該銘柄の現在ネット建玉を符号付き `p`（+ ロング / − ショート / 0 フラット）、サイジング済みの注文を符号付き数量 `q`（+ 買い / − 売り）とする。
遷移 `p → p + q` を**ゼロ点で分割**して意図を組み立てる（[IADR-0033](IADR-0033_shared-inventory-fold.md) `SignedInventory.Apply` の反転処理と同一の境界規則）。

| ケース | 条件 | 生成する意図 |
| --- | --- | --- |
| 新規/建て増し | `p == 0`、または `sign(p) == sign(q)` | **単一 Open**：数量 `|q|`、side = `sign(q)`。`PositionEffect.Open` |
| 部分決済 | `p != 0`、`sign(p) != sign(q)`、`|q| < |p|` | **単一 Close**：数量 `|q|`、side = `p` の反対。`PositionEffect.Close` |
| 全決済 | `p != 0`、`sign(p) != sign(q)`、`|q| == |p|` | **単一 Close**：数量 `|p|`、side = `p` の反対。`PositionEffect.Close` |
| 反転（ドテン） | `p != 0`、`sign(p) != sign(q)`、`|q| > |p|` | **2 意図**：① Close 数量 `|p|`（既存建玉を全決済）／② Open 数量 `|q| − |p|`（余りを新規建て）。両脚とも side = `p` の反対だが建玉効果が異なる |

- **反転は決して単一の相殺注文にしない**。既存建玉を全決済する Close 脚と、余りを新規建てる Open 脚に必ず割る。
- 全決済・部分決済で side が既存建玉の反対になるのは自明（ロング決済 = Sell、ショート決済 = Buy）。反転の 2 脚は同一 side（例: ロング→ショートなら両脚 Sell）で、
  建玉効果のみが Close/Open で異なる。

### 数量の決め方

- **Close 脚（部分/全決済・反転の①）**: 数量は既存建玉 `|p|` を上限とする。部分決済の数量は判断が要求した縮小量を `|p|` でクランプする
  （`closeQty = min(要求縮小量, |p|)`）。保有超過の決済は起こさない（フェイルセーフ。Close で合成ショートを作らない）。
- **Open 脚（新規/建て増し・反転の②）**: 数量はサイジング（[IADR-0017](IADR-0017_trade-decision-structure.md) `PositionSizer`）で新ポジションについて算出する。反転では**決済で解放される資本**（当該建玉の拘束解除）を
  前提に、post-close の残枠でサイジングする。反転の 2 脚は独立にサイジングし、**差引きしない**（Close = 既存全量、Open = 新規サイジング量）。

### 2 意図の運搬（契約無改修）

- 反転で生じる 2 意図は、**別々の `TradeDecisionMade` イベント（それぞれ独立の `DecisionId`）** として発行する。契約（`TradeDecisionMade`・`OrderIntent`）は変更しない
  （[IADR-0018](IADR-0018_portfolio-ledger-projection.md) の最小契約を維持）。1 イベントに意図の配列を載せる案は採らない（スクリーニング・承認・執行・重複排除がいずれも意図単位のため）。
- 2 脚の `DecisionId` は、反転判断の基底 ID から**決定的に導出**する（[IADR-0026](IADR-0026_audit-deterministic-correlation.md) の v5 UUID・自然キー＝基底 ID＋脚識別子）。
  再送に対し冪等で、監査で 2 脚が同一の反転判断由来と辿れる。
- 発行順は **Close → Open**。台帳・資本状態が「決済→新規建て」の順で反映され、Open 脚の資本評価が post-close 状態を見られるようにする。
- 各意図は独立にスクリーニング（`RiskEvaluator`）・承認（`OrderApproved`）・執行・台帳記録される。**Close 脚**はエントリー統制で止めない（IADR-0004 のフェイルセーフ）。
  **Open 脚**は全エントリー統制（kill switch・資金上限・商品種別/信用ゲート・差金決済防止・相場操縦ガード）の対象になる。

### 建玉効果の常時明示（不変条件）

生成する全 `OrderIntent` に `PositionEffect` を明示的に設定する（null・曖昧を作らない）。判定コアは Open/Close を推論しない（IADR-0004 の決定を維持）。
この不変条件は呼び出し側責務として結合テストで担保する（[IADR-0003](IADR-0003_position-sizing-responsibility.md) のサイジング結線と同様）。現状の取引判断は常に効果を明示設定しており
（現物ロング新規 = `Open`、損切り機械執行 = `Close`）、本不変条件は既に満たされている。分解ロジック導入時もこれを崩さない。

### 適用範囲・非適用

- **損切り機械執行**（[IADR-0015](IADR-0015_stop-loss-mechanical-close.md)）は本分解の**対象外**。損切りは常に純 Close であり、スクリーニングを迂回して直接組み立てる。反転は LLM 由来の取引判断に限る。
- **現物のみ（現段階）**: `p ≥ 0`（ショート無し）。反転が要求されても Open 脚は Sell×Open（ショート新規建て）となり、リスク管理が現物ゲート（信用未有効・`ProductType`）で拒否する。
  Close 脚（ロング全決済）は成立するため、**現物では反転が「ロングを全決済し、ショート新規建ては下流で拒否」に自然縮退する**（特別扱い不要）。信用有効化で Open 脚も成立する。

## 理由

- 生成側での分解は IADR-0004 の思想（建玉効果は生成側の確定情報）に一致し、各意図が一意な建玉効果を帯びてエントリー統制を正確に適用できる。
- ゼロ跨ぎ分割は会計側の畳み込み（`SignedInventory.Apply`）と同一境界則で、生成側と会計側の一貫性を構造的に保つ。
- 2 意図を別イベント＋決定的 ID にすることで、契約を増やさず、各脚を独立にスクリーニング・監査でき、再送に冪等。
- 現物での自然縮退により、信用未有効の現段階でも安全側（決済は通り、ショート新規は拒否）に倒れ、特別なガードを足さずに済む。

## 結果

- 良い影響: 反転・部分決済でもエントリー統制が新規建て部分にだけ正しく効く。各脚が独立に監査・スクリーニング可能。会計則と整合。契約無改修。現物で安全縮退。
- 悪い影響・トレードオフ:
  - 反転は 2 注文にまたがり**原子的でない**。Close は約定したが Open が保留/拒否となる窓では建玉フラット（安全側）で留まる（[IADR-0015](IADR-0015_stop-loss-mechanical-close.md) と同様に既知の窓として受容）。
  - Open 脚のサイジングは post-close 資本を前提とするが、`RiskEvaluator` は非同期窓で古い台帳を見て保守的に Open を拒否し得る（安全側の縮退）。
  - 分解には当該銘柄の現在ネット建玉が生成時入力として必要（サイジング/保有文脈 [IADR-0029](IADR-0029_sizing-context-sync-api.md)/[IADR-0030](IADR-0030_position-store-sync-api.md) から供給）。
  - 会計は net 1 建玉（[IADR-0018](IADR-0018_portfolio-ledger-projection.md)・現物ネッティング）のまま。反転は同一ネット線上の逐次 Close→Open で表現し、両建て別ロット会計は信用（ADR-0007/#50）後
    （[IADR-0035](IADR-0035_stop-loss-authoritative.md) の「net 1 建玉に単一損切り」制約と同じ）。2 脚が建玉効果で既に分離済みのため、将来の別ロット会計とも両立する。
- フォローアップ（信用有効化スライスで実装）:
  - 取引判断への分解ロジック実装（ゼロ跨ぎ分割の純関数化＝`SignedInventory` 隣接が候補・[IADR-0033](IADR-0033_shared-inventory-fold.md) パターン）。現在ネット建玉のサイジング文脈への供給。
  - 「発注意図に建玉効果が必ず設定される」不変条件＋反転分割の結合テスト。反転 2 脚の決定的 `DecisionId` 相関。差金決済防止・相場操縦ガードの Open 脚適用確認。
  - 原子的反転／注文内ネッティングは**追わない**（明示）。両建て別ロット会計（ADR-0007/#50）。

## 関連

- Supersedes: なし（[IADR-0004](IADR-0004_position-effect-entry-scoping.md) のフォローアップ「分解方針の別途 IADR 化」を本 IADR で解消）
- Superseded by: なし
- 関連: [IADR-0004](IADR-0004_position-effect-entry-scoping.md)、[IADR-0033](IADR-0033_shared-inventory-fold.md)、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)、[IADR-0015](IADR-0015_stop-loss-mechanical-close.md)、[IADR-0026](IADR-0026_audit-deterministic-correlation.md)、[IADR-0035](IADR-0035_stop-loss-authoritative.md)、[IADR-0017](IADR-0017_trade-decision-structure.md)
