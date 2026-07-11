---
title: IADR-0035 損切り価格を OrderIntent に載せ #63 台帳へ永続化し、open-positions の近似を実値化する
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-04, FR-10, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0035: 損切り価格を OrderIntent に載せ #63 台帳へ永続化し、open-positions の近似を実値化する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-04（取引判断が損切り価格を決める）、FR-03（損切りライン検知）、FR-10（保有・損切り監視）、ADR-0003
- 対象 Issue: [#83](https://github.com/endazon/ai-stock-trading/issues/83)（`Refs #22`）
- 関連する実装仕様書: [20260711_stop-loss-authoritative](../specs/20260711_stop-loss-authoritative.md)
- 関連 IADR: [IADR-0018](IADR-0018_portfolio-ledger-projection.md)（契約最小化）、[IADR-0030](IADR-0030_position-store-sync-api.md)（3% 近似の過渡的措置）

## コンテキストと課題

市場監視の損切りライン検知に供給する保有ポジション（IADR-0030）は、損切り価格の権威データが契約に無かったため
**既定比率 3% の近似**で導出していた。損切り価格の一次情報は取引判断 LLM の `stopLossDistancePerShare`（ATR 連動）だが、
`OrderIntent`/`OrderApproved`/#63 台帳（`LedgerFill`）に含まれていない（IADR-0018 の契約最小化）。近似は ATR 連動の実際の
損切りラインと乖離し得るため、実値化する。

## 決定

- **`OrderIntent` に `decimal? StopLossPrice`（nullable・既定 null）を追加する**（IADR-0018 の契約最小化を当該項目のみ見直し）。
  - 後方互換: 既定 null で既存の生成/消費（発注執行・他消費者）は無改修。損切り価格は取引判断→リスク管理承認→#63 台帳へ運ばれる。
- **取引判断**が損切り価格を算出して載せる（ロング=`ReferencePrice − 距離`／ショート=`ReferencePrice + 距離`）。`OrderApproved.Intent` は
  同一 Intent を通すため損切り価格を保持する（機械執行の Close Intent は損切り価格 null＝該当なし）。
- **#63 台帳**が永続化する: `ApprovedOrderRow` に `StopLossPrice` 列（nullable numeric・EF マイグレーション追加）。`LedgerFill.StopLossPrice` に補完。
- **射影**: `ProjectOpenPositions` は net 建玉に**最新の同方向エントリー（新規/建て増し/反転）の損切り価格**を持たせる（一部決済では保持・全決済で消滅）。
  平均取得単価と同じく最新のリスク評価を反映する自然な規則。`OpenPosition.StopLossPrice`（nullable）で公開。
- **フォールバック維持**: `OpenPositionsService` は損切り価格が存在すれば実値、無ければ **3% 近似**（IADR-0030）にフォールバックする
  （本変更前に建った建玉・欠損時）。

## 理由

- 損切り価格の一次情報（取引判断の決定値）を、既に承認 Intent を保持する #63 台帳（IADR-0018）に最小変更で載せられる。新規イベント/相関は不要。
- nullable＋フォールバックにより後方互換・段階移行が可能（レガシー建玉は近似のまま安全に動く）。
- net 建玉の損切りは「最新エントリーの損切り」を採ることで、平均取得単価（最新の建て増しを反映）と会計思想が一致する。

## 結果

- 良い影響: 損切りライン検知が ATR 連動の実値で動き、3% 近似の乖離が解消される。損切りの機械執行がより正確な水準で発火する。
- 悪い影響・トレードオフ: `OrderIntent` 契約が 1 項目増える（IADR-0018 の最小化方針を当該項目のみ緩める）。EF マイグレーション 1 件。
  両建て別ロット（信用有効化後・#50）では net 1 建玉に単一損切りとなる制約は残る。
- フォローアップ: 実 DB マイグレーション適用・実 E2E（#82）。損切り価格の履歴/変更追跡。両建て別ロット会計（ADR-0007/#50）。

## 関連

- Supersedes: なし（IADR-0030 の 3% 近似はフォールバックとして残す）
- Superseded by: なし
- 関連: [IADR-0018](IADR-0018_portfolio-ledger-projection.md)（契約最小化を当該項目で見直し）、[IADR-0030](IADR-0030_position-store-sync-api.md)（近似→実値化）
