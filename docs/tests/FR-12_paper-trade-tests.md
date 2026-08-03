---
title: ペーパートレード（内蔵 paper）テスト仕様書
type: test-spec
status: draft
related_ids: [FR-12, FR-13, FR-20, UC-01, UC-02, UC-06, IADR-0127]
author: endazon (with Claude Code)
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/05_screens/01_screens.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
related_specs:
  - ./README.md
  - ../functional/FR-12_paper-trade.md
  - ./FR-20_staged-gates-tests.md
  - ../specs/20260803_343_regression-test-foundation.md
---

# テスト仕様書: ペーパートレード（内蔵 `paper`）— FR-12

> 全面再実装（[#344](https://github.com/endazon/ai-stock-trading/issues/344)）に合わせた再作成。
> 計画大改定（project-planning#144）で FR-12 の位置づけが変わった点を反映する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-12（ペーパートレード）。関連: FR-13（設定変更）・FR-20（段階と発注先の 2 軸）
- ユースケース（UC）: UC-01 / UC-02（取引サイクル）・UC-06（設定変更）
- 受け入れ基準の所在: `02_requirements/01_requirements.md` FR-12 / FR-20、`05_screens/01_screens.md`（表示規約）
- 実装 ADR: [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)（計画確定値の適合検査）

## 本改定で変わった前提（テストの意味が変わる箇所）

| 論点 | 改定前 | **改定後（計画確定）** |
| --- | --- | --- |
| 内蔵 `paper` の位置づけ | Stage 1 の検証手段 | **デバッグ・開発用途**。Stage 1 は moomoo `SIMULATE` |
| 発注先の表現 | `TradeMode` = `{Paper, Live}` の 2 値 | **`BrokerProvider` の 3 値**（moomoo `REAL` / `SIMULATE` / 内蔵 `paper`） |
| Stage 1 の実績集計 | 区別なし | **`SIMULATE` の約定のみ算入。内蔵 `paper` は除外日数として別計上** |
| 画面表示 | 規定なし | **全画面に警告バナー常時表示**（2 文言必須）＋統制カードに `paper` ラベル |

**この差は「Stage 1 の合格証跡が擬似約定で積み上がる」という統制の穴に直結する**ため、
以下のテストのうち T-05〜T-07 は否定形（迂回不能）として必須とする。

## テスト対象・範囲

- 対象: 内蔵 `paper` の擬似約定、発注先（`BrokerProvider`）の選択・切替、Stage 1 集計からの除外、画面の警告表示規約。
- 対象外: moomoo `SIMULATE` への実発注（#342 の PoC 完了後・`Category=Integration`）、実弾切替の警告モーダルの
  画面実装（#340。本書は**表示規約の検証観点**のみ定める）。

## テスト観点

正常系（擬似約定が成立し記録される）／異常系（未対応の注文種別）／境界値（切替時点の前後の約定の帰属）／
**否定形（内蔵 `paper` の実績が Stage 1 に混入しない・外部へ発注しない）**。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | 発注先＝内蔵 `paper` | 成行の新規建てを発注する | 擬似約定が成立し、注文状態・約定が記録される | FR-12 | 自動 |
| T-02 | 発注先＝内蔵 `paper` | 逆指値付きの新規建てを発注する | 逆指値も擬似的に受理され、建玉と同時に成立する（FR-10 の「逆指値が未受理なら建玉を持たない」と整合） | FR-12, FR-10 | 自動 |
| T-03 | 発注先＝内蔵 `paper` | 統制違反となる発注を行う | **統制は実発注時と同じく適用される**（`paper` だから緩む、が無いこと） | FR-12, FR-10 | 自動 |
| T-04 | 発注先＝内蔵 `paper` | 発注を行う | **外部（moomoo / OpenD）への送信が 1 度も発生しない** | FR-12 | 自動（否定形） |
| T-05 | Stage 1・内蔵 `paper` で約定を積む | Stage 1 進捗を集計する | **取引件数に算入されない** | FR-20 | 自動（否定形） |
| T-06 | Stage 1・内蔵 `paper` で営業日をまたぐ | Stage 1 進捗を集計する | **経過営業日数に算入されず、除外日数として別に数えられる** | FR-20 | 自動（否定形） |
| T-07 | Stage 1・`SIMULATE` と `paper` が混在する期間 | Stage 1 進捗を集計する | `SIMULATE` の約定・日数のみ算入され、進捗表示に除外日数が併記される | FR-20 | 自動 |
| T-08 | 発注先＝内蔵 `paper` | 任意の画面を表示する | 上部に警告バナーが常時表示され、**「デバッグモードです。外部へ発注していません」**と**「この期間は Stage 1 の実績に算入されません」**の 2 文言を含む | FR-12, SC-01〜03 | 自動 |
| T-09 | 発注先＝内蔵 `paper` | 統制状態のカード（勝率・取引件数・稼働率）を表示する | 各カードに `paper` である旨のラベルが付く | FR-12 | 自動 |
| T-10 | 発注先を `paper` → `SIMULATE` へ切替 | 切替の前後で約定を作る | 切替時刻を境に約定の帰属が分かれ、変更履歴に日時・変更前後・理由が残る | FR-13, FR-20 | 自動（境界値） |

## テストデータ

- 擬似約定は決定的であること（乱数を用いる場合はシードを固定する）。再現しない擬似約定は退行検知に使えない。
- Stage 1 集計のテストは営業日カレンダーを注入可能にし、実時刻に依存させない。

## 現状（2026-08-03 時点）と担当

本書が定めるケースのうち、**T-04 以外は実装が未到達**である。`BrokerProvider` の 3 値化（#334）と
Stage 1 集計規則（#333）が入るまで書けない。計画確定値との乖離は
`AiStockTrading.PlanConformance.Tests` の既知逸脱として登録済み（`BrokerProvider.Values` / `Stage.Values` /
`Stage.Stage1BrokerProvider`）であり、担当 issue が解消した時点で登録簿の更新が強制される。

| ケース | 担当 issue |
| --- | --- |
| T-01〜T-04 | #334（発注先の 3 値化と内蔵 `paper` の分離） |
| T-05〜T-07 | #333 / #334（Stage 1 集計からの `paper` 除外） |
| T-08 / T-09 | #340（画面の警告バナー・ラベル） |
| T-10 | #334 / #339（変更履歴・監査ログ） |

## 変更履歴

| 日付 | 内容 |
| --- | --- |
| 2026-08-03 | 全面再実装（#344）に合わせて新規作成（#343） |
