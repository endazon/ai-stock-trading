---
title: IADR-0029 サイジング文脈はリスク管理が導出・所有し、取引判断は同期 API で照会する
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-10, ADR-0001, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0029: サイジング文脈はリスク管理が導出・所有し、取引判断は同期 API で照会する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-04（サイジング）、FR-10（段階/日次残枠・連敗/DD）、ADR-0001、ADR-0003
- 対象 Issue: [#22](https://github.com/endazon/ai-stock-trading/issues/22)（継続）
- 関連する実装仕様書: [20260710_sizing-context-wiring](../specs/20260710_sizing-context-wiring.md)
- 関連 IADR: [IADR-0028](IADR-0028_daily-policy-sync-api.md)（同期 API 方式・踏襲）、[IADR-0017](IADR-0017_trade-decision-structure.md)（サイジング文脈）、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)（#63 台帳の PortfolioState）、[IADR-0005](IADR-0005_stage-capital-cap-definition.md)（段階資金上限＝取得額累計）

## コンテキストと課題

取引判断はサイジング文脈（資金・段階/日次残枠・連敗/DD・モード・上限）を入力に数量を確定する（IADR-0003/0017）。現状
`ISizingContextProvider` はプレースホルダで既定値を返し、リスク管理（設定＋#63 台帳の PortfolioState を持つ）と結線されていない。
サイジング文脈をどのサービスが導出・所有し、取引判断がどう得るかを決める必要がある（#22 第一歩 IADR-0028 と同方針で）。

## 決定

- **サイジング文脈はリスク管理サービスが導出・所有する**。リスク管理は設定（`RiskManagementSettings`）と PortfolioState（#63 台帳
  の実データ・`LedgerPortfolioStateProvider`）から `SizingContextView` を組み立てる（`PortfolioSnapshotBuilder` を再利用）。
  - StageCapitalRemaining = max(0, `Stage.CapitalCap` − `InvestedCapital`)（IADR-0005）。
  - DailyOrderRemaining = max(0, `Limits.MaxDailyOrderAmount` − `DailyOrderedAmount`)。
  - Capital・ConsecutiveLosses・DrawdownRatio は PortfolioState 由来、Mode・Limits は設定由来。負値は 0 にクランプ。
- **取引判断は同期 API で照会する**（IADR-0028 と同方式）: `GET /risk-controls/sizing-context`。`ISizingContextProvider` を非同期化し、
  `HttpSizingContextProvider` が写像する。方式の理由（アーキ概要の同期 API 契約管理・Database per Service 維持・アダプタで可逆）は
  IADR-0028 に同じ。
- **フェイルセーフ**: リスク管理不達・未取得・非 2xx・タイムアウト・不正応答は**残枠 0 の安全既定**（availableCapital 0 → 数量 0 →
  見送り）＝取引しない。依存先障害時に安全側へ倒れる（過大なサイジングを避ける）。
- **安全既定でゲート**: `RiskManagement:BaseUrl` 未設定/不正 URI は従来のプレースホルダ（既定値）を用い、構成で有効化したときのみ実照会する。
- **service-to-service 認証**（エンドポイントは OwnerOnly）は platform 統合の後続で結線する（本スライスはアダプタ・写像・フェイルセーフを実装）。

## 理由

- サイジング文脈は設定＋保有/約定に依存し、それらを所有するリスク管理が導出するのが集約として自然（取引判断は照会のみ）。
- IADR-0028 と同じ同期 API 方式で一貫性・Database per Service・可逆性を保てる。フェイルセーフを残枠 0（取引しない）にすることで、
  依存先障害時も過大発注を構造的に防ぐ（daily-policy の「未取得なら取引しない」と同じ安全側）。

## 結果

- 良い影響: 取引判断が実データ（段階/日次残枠・連敗/DD）でサイジングでき、パイプラインが実データで動く土台が進む。
- 悪い影響・トレードオフ: 実行時にリスク管理へ同期依存する（不達時は取引しない安全側）。service-to-service 認証・キャッシュ/リトライ・
  含み損益/DD の日次終値マーク（IADR-0008 後続）は後続。
- フォローアップ: service-to-service 認証、市場監視 `IPositionStore`・費用 poller の実データ化（#22 の他ステップ）、含み損益マーク、キャッシュ。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0028](IADR-0028_daily-policy-sync-api.md)（同期 API 方式）、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)（PortfolioState）
