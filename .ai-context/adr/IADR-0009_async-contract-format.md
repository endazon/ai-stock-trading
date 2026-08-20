---
title: IADR-0009 非同期イベント契約は Markdown 通信仕様で管理し、OpenAPI は同期 API 専用とする
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-05, FR-10, ADR-0001, ADR-0002]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0009: 非同期イベント契約は Markdown 通信仕様で管理し、OpenAPI は同期 API 専用とする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: endazon（利用者）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-04（取引判断）、FR-05（発注執行）、FR-10（リスク統制）、ADR-0001（platform 再利用・イベント連携）
- 関連する通信仕様書: [events-and-ports](../../docs/api/events-and-ports.md)
- 対象 Issue: #34
- 対象: `docs/api/`（通信仕様）、`docs/api/openapi.yaml`、`scripts/gen-openapi-skeleton.js`

## コンテキストと課題

`docs/api/openapi.yaml` は「通信仕様書のエンドポイント一覧表が見つからなかった」というコメント付きの空雛形
（`paths: {}`）のままだった（Issue #34）。CI（`openapi.yml`）は通信仕様書から雛形を生成する設計のため、通信
仕様書が無い限り空のまま更新され続ける。

一方、現時点で確定している契約は**非同期イベント**（`TradeDecisionMade` / `OrderApproved` / `OrderRejected` /
`OrderExecuted`）と**ポート**（`IBrokerAdapter` / `IMarketDataSource`）であり、同期 HTTP API は未実装
（kill switch 操作・設定変更はリスク管理ホスト #12・設定管理 #19 で発生）。非同期契約を OpenAPI で表現するのは
本来の用途外であり、記述形式（OpenAPI / AsyncAPI / Markdown）の方針を決める必要があった。

## 検討した選択肢

1. **非同期契約も無理に OpenAPI へ押し込む** — OpenAPI は同期 HTTP 用であり、イベント/トピックの表現に不向き。
   誤解を招く
2. **AsyncAPI を採用して非同期契約を形式化する** — 表現力は高いが、現段階（契約が少数・流動的）ではツール
   チェーン導入コストが見合わない
3. **非同期契約は Markdown 通信仕様（`docs/api/*.md`）で管理し、OpenAPI は同期 API 専用とする** — 現状の契約量に
   見合い、`gen-openapi-skeleton.js` の「エンドポイント一覧表」方式とも整合する

## 決定

選択肢 3 を採用する。

- **非同期イベント契約・ポート契約は Markdown 通信仕様**（`docs/api/events-and-ports.md`）で管理する。AsyncAPI は
  現段階で採用しない（契約が増え形式化の便益がコストを上回った時点で再検討）。
- **OpenAPI（`openapi.yaml`）は同期 HTTP API 専用**とする。同期 API が実装される時点で、通信仕様書に
  「エンドポイント一覧」表（メソッド/パス/概要）を追記すると `gen-openapi-skeleton.js` が雛形を生成する。
- 現段階では同期エンドポイントが無いため `paths` は空のままとし、生成器の空 `paths` コメントを「同期 API 未実装。
  非同期契約は `docs/api/events-and-ports.md` を参照」と説明的に更新する（「見つからなかった」という誤解を避ける）。

## 理由

- 契約が少数かつ流動的な現段階では、Markdown で十分に追跡でき、生成器の既存方式とも噛み合う。
- 同期/非同期を型（openapi は同期・Markdown は非同期）で分けることで、`openapi.yaml` が非同期契約の欠落と
  誤解されるのを防ぐ。

## 結果

- 良い影響: 非同期契約が文書化され（#34 受け入れ基準の「非同期契約の別文書が整備される」を充足）、`openapi.yaml`
  の空理由が明示された。同期 API 追加時の追記フローが確定した。
- 悪い影響・トレードオフ: 非同期契約が機械可読（AsyncAPI）でないため、契約テスト・コード生成は手動。契約増加時に
  AsyncAPI 移行を再検討する。
- フォローアップ: リスク管理ホスト（#12）・設定管理（#19）実装時に同期 API を通信仕様へ追記し openapi を生成する。
  イベントエンベロープの platform 準拠（#22）と連動して詳細化する。

## 関連

- Supersedes: なし
- Superseded by: なし
- 再検討結果（フォローアップ）: [IADR-0037](IADR-0037_async-contract-format-reevaluation.md)（契約 4→10 増加時点で AsyncAPI 採用可否を再評価。当面不採用を再確認し、再採用トリガを観測可能な条件へ具体化・Issue #51）
