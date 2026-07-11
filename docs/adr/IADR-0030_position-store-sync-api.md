---
title: IADR-0030 保有ポジションはリスク管理が #63 台帳から射影・所有し、市場監視は同期 API で照会する
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-10, ADR-0001, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0030: 保有ポジションはリスク管理が #63 台帳から射影・所有し、市場監視は同期 API で照会する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-03（市場監視・損切りライン検知）、FR-10（保有・損切り監視は維持）、ADR-0001、ADR-0003
- 対象 Issue: [#22](https://github.com/endazon/ai-stock-trading/issues/22)（継続）
- 関連する実装仕様書: [20260711_position-store-wiring](../specs/20260711_position-store-wiring.md)
- 関連 IADR: [IADR-0029](IADR-0029_sizing-context-sync-api.md)（同期 API 方式・踏襲）、[IADR-0028](IADR-0028_daily-policy-sync-api.md)、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)（#63 台帳の射影）、[IADR-0014](IADR-0014_market-monitor-events-and-boundary.md)（損切り優先の巡回）

## コンテキストと課題

市場監視の `IPositionStore` はプレースホルダで空（保有なし＝損切り検知対象なし）を返し、#63 台帳（設定＋約定射影）と結線されていない。
損切りライン検知（`StopLossEvaluator`）に実保有を供給する必要がある。

**課題**: #63 台帳（`PortfolioProjection`）は約定列から銘柄別ネット建玉（銘柄・市場・方向・数量・**平均取得単価**）を射影できるが、
**損切り価格を保持しない**。損切り価格の一次情報は取引判断 LLM の `stopLossDistancePerShare`（ATR 連動）だが、確定済み契約
（`OrderIntent`/`OrderApproved`/`OrderExecuted`）にも台帳（`LedgerFill`）にも含まれない（IADR-0018 が契約最小化のため銘柄/方向のみ補完）。

## 決定

- **保有ポジションはリスク管理サービスが導出・所有する**（サイジング文脈 IADR-0029 と同じ集約方針）。
  - `PortfolioProjection.ProjectOpenPositions(fills)` を追加（既存 `Apply`＝符号付き在庫・平均取得単価法を再利用する純関数）。
    銘柄別ネット建玉 `OpenPosition`（Symbol・Market・Side・Quantity・AverageEntryPrice）を返す（数量 0＝全決済は除外）。
  - `OpenPositionsService` が `IPortfolioLedgerStore` ＋ `IRiskSettingsStore` から `OpenPositionView`（＝`HeldPosition` と同形）を組み立てる。
- **損切り価格は既定比率で近似導出する（過渡的措置）**: `TradingDefaults.DefaultStopLossRatio`（0.03・前提条件 §5 の「損切り幅 3%」注記）を平均取得単価へ適用。
  - ロング（Buy）: `StopLossPrice = EntryPrice × (1 − ratio)`／ショート（Sell）: `EntryPrice × (1 + ratio)`。
  - 理由: 損切り価格の権威データが現契約に無いため。近似でも実保有に対して損切り検知を機能させることを優先し、実値化は後続で置換する（可逆）。
- **市場監視は同期 API で照会する**（IADR-0028/0029 と同方式）: `GET /risk-controls/open-positions`。`IPositionStore` を非同期化し、
  `HttpPositionStore` が写像する。Database per Service 維持・アダプタで可逆。
- **フェイルセーフ**: リスク管理不達・未取得・非 2xx・タイムアウト・不正応答は**空列**（＝損切り検知対象なし）。既存 `PlaceholderPositionStore`（空）と同一既定。
- **安全既定でゲート**: `RiskManagement:BaseUrl` 未設定/不正 URI は従来 `PlaceholderPositionStore`（空）。構成で有効化時のみ実照会（解決時に構成を読む・5s タイムアウト）。

## 理由

- 保有は約定（台帳）に依存し、それを所有するリスク管理が射影するのが集約として自然（IADR-0029 と同じ）。市場監視は照会のみ。
- 既存の純関数射影（`Apply`）を再利用し、新規契約を追加しない（IADR-0018 の方針を維持）。
- 損切り価格の近似は honest な過渡的措置として IADR に明記し、実値化を後続に切り出すことで、実保有の結線を先に前進できる。

## フェイルセーフの非対称性（明示）

daily-policy/sizing-context の安全既定は「取引しない」（保守側）だが、保有ポジションの空列は**損切り検知を抑止する**（保護が働かない側）。
損切り価格を知るには保有情報が不可欠で、依存先障害時に取り得る唯一の縮退である。緩和策: 短い監視間隔、プレースホルダ/失敗の警告ログ、
リスク管理側での独立した損切り執行（ADR-0003・`StopLossTriggered` 受信時の機械執行）。損切り価格の権威データ化までの過渡的リスクとして受容する。

**サイレント縮退の注意（優先度の明示）**: `GET /risk-controls/open-positions` は OwnerOnly（Keycloak `trading-owner`）のため、`RiskManagement:BaseUrl`
を設定して実運用を有効化しても service-to-service 認証が未実装だと**常に 401 → 空列**に倒れ、「接続されているように見えて損切り検知が
働いていない」サイレント縮退になり得る。フェイルセーフが保護抑止側であるこの非対称性ゆえ、**本エンドポイント向けの service-to-service 認証は
他の同期 API 連携（daily-policy/sizing-context）より優先**して実装する。それまでは `LogWarning`（照会失敗・プレースホルダ使用）を監視・
アラートへ連携し、縮退を検知可能にすることを運用の前提とする。

## 結果

- 良い影響: 実保有が損切りライン検知へ供給され、パイプラインが実データで動く土台がさらに進む。
- 悪い影響・トレードオフ: 損切り価格は既定比率の近似（ATR 連動の実値ではない）。実行時にリスク管理へ同期依存（不達時は検知抑止）。
- フォローアップ: **本エンドポイント向け service-to-service 認証（上記の非対称フェイルセーフゆえ最優先）**、~~損切り価格の権威データ化~~（**→ [IADR-0035](IADR-0035_stop-loss-authoritative.md) で実値化・3% 近似はフォールバックに #83**）、費用 poller の実データ化（#22 の他ステップ）、含み損益/DD の日次終値マーク（IADR-0008）、キャッシュ/リトライ。

## 関連

- Supersedes: なし（`PlaceholderPositionStore` を設定時に差し替え）
- Superseded by: なし
- 関連: [IADR-0029](IADR-0029_sizing-context-sync-api.md)（同期 API 方式・集約方針）、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)（射影）、[IADR-0014](IADR-0014_market-monitor-events-and-boundary.md)（損切り優先）
