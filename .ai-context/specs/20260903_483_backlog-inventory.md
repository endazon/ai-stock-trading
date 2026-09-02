---
title: バックログ定期監査（#483・#344・#24）の棚卸し文書更新
type: spec
status: draft
related_ids: []
author: Claude Code（棚卸しセッション）
created: 2026-09-03
updated: 2026-09-03
plan_refs: []
---

# 仕様書: バックログ定期監査（#483）・親トラッキング（#344）・インフラ（#24）の棚卸し文書更新

> 本仕様書は実装着手前に作成する。本作業は「NFR（運用・棚卸し）」であり、計画書の新規実装を伴わない
> ドキュメント整合作業である。トレーサビリティ規約の無採番 NFR の 2 類型のうち「メタ作業（規約整備・
> 文書統制）」に該当する（`.claude/rules/traceability.md` 参照）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR・運用ドキュメント整合作業）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（IADR-0185 の記録方針〔能力と規則を分けて記録する／最後に測った時点を残す〕を踏襲）
- 計画書リンク: なし（本作業は project-planning に依存しない。ADR-0029 決定2）

## 目的・背景

バックログ定期監査 issue #483（2026-08-31 実施）が指摘した `docs/blocked-tasks.md` の事実誤認・カバレッジ
漏れ・ラベル付け漏れと、2026-09-02〜03 にかけて実施された一連の実測・実装（#570 Discord 接続確認・#566
Finnhub レート制限・#397/#342 moomoo 読み取り専用 probe・#500 MCP 結合確認・#571 LLM ゲートウェイ用途
登録・#627 Istio mTLS 断・#626 OpenD PVC 喪失）の結果を、`docs/blocked-tasks.md`・親トラッキング #344・
インフラ #24 へ反映し、監査記録として #483 へ追記する。

## 対象範囲

- 対象:
  - `docs/blocked-tasks.md` の A-1/A-9 分離・A-3 事実誤認の訂正・A-5 代替検証記録・A 群への #571 追記・
    「最後に測った時点」欄の補完・新規 A 項目（Istio mTLS 断・LLM API キー未投入・OpenD SMS 再認証）の追加・
    B-2 受け皿 issue のリンク・冒頭「最終更新」の追記
  - #644（B-2 の受け皿。本作業内で起票済み）
  - #344 のチェックリストを実 state へ同期し、進捗コメントを追記
  - #24 へ ArgoCD／ExternalSecret／可観測性の実測コメントを追加し、`docs/infra/infra.md` Tier 境界節を
    現況へ追随させる
  - `scripts/measure-region-latency.sh`（Hetzner 契約後に実行するレイテンシ実測スクリプトの用意）と
    `scripts/README.md` への登録
  - #483 への実測反映コメントの投稿
- 対象外:
  - 実装コード（backend/frontend）の変更
  - Hetzner 契約後の実測そのもの（スクリプトの用意のみ）
  - `docs/blocked-tasks.md` 以外の未確定事項の新規解決（A-3 の「残る未確定事項」③④等、既存の未了項目の
    実機確認そのもの）

## 設計

### 1. `docs/blocked-tasks.md` の変更方針

- **A-1 分離**: A-7 が行った「環境構築で解消し得る前半」と「設計上恒久の後半」の分離と同型で、A-1 を
  A-1a（Hetzner への接続可否・技術検証・AI 実行可）と A-1b（Hetzner の ToS 適合・法的判断・人間の判断が要る）
  へ分ける。A-9 項目2 も同じ見出しの混在を持つため、A-1 への参照へ揃える。
- **A-3 事実誤認の訂正**: 「追跡: #382（open のまま）」を、#382 が 2026-08-07 に CLOSED 済みであった事実
  （`gh issue view 382` 実測）へ訂正する。既存の記述は削除せず取り消し線＋訂正として残す（本文書のポリシー・
  101 行目）。後継の追跡先を「本項（A-3）自身＋#342 の PoC 項目 7」と明記する。
- **A-5 代替検証の記録**: `scripts/e2e-local-infra.sh` 経由でローカル Docker が使えない場合の代替検証手段
  （Docker デーモン停止時の実測経路）を追記する。
- **#571 の A 群への追記**: MSP 側の変更を AI がローカルクローンで直接編集できる（ブロッカーとしての性質が
  解消）ことと、MSP PR #1158（OPEN）待ちであることを記録する。
- **「最後に測った時点」欄の補完**: A-1・A-5・A-9 項目3 に同欄を追加する。
- **新規 A 項目**: Istio mTLS 断（MSP#1159・AST#627 が受け皿として既存）、LLM API キー未投入（`llm-provider-
  credentials` の `anthropic-api-key` が空。専用 issue は無いため「追跡: 未起票」として記録）、OpenD の
  PVC 喪失に伴う SMS 再認証（#626 が受け皿として既存・`resource-policy: keep` の是正案を含む）。
- **B-2**: 受け皿 issue #644（本作業内で起票）へのリンクを追加する。
- **冒頭「最終更新」**: 既存の書式（前回更新の一覧を保持し先頭に追加）に倣い、本日分を 1 行追記する。

### 2. #344 の同期

`gh issue view <n> --json state` で本文チェックリスト 19 項目の実 state を再実測し、本文のチェック状態を
実態へ揃える（`gh issue edit 344 --body`）。W12 以降（本日分）の追記コメントを投稿し、残る子 issue と #204
監査が新設した 8 件（#632/#633/#634/#636/#637/#640/#642/#643）へのリンクを含める。

### 3. #24 のコメント・`docs/infra/infra.md` の追随

`kubectl` の読み取り専用コマンドで ArgoCD Application 同期状態・ExternalSecret／Vault 同期・可観測性
Pod 状況を実測し、結果を #24 へコメントする。`docs/infra/infra.md` の Tier 境界節（Tier 3 の扱い）は
実測結果を反映して現況へ追随させる（大幅な書き直しはしない。表の状況欄・根拠欄の更新に限る）。

### 4. `scripts/measure-region-latency.sh`

moomoo OpenD の接続先ホストと主要情報源（finnhub.io / api.stlouisfed.org / sec.gov /
api.edinet-fsa.go.jp）への TCP/TLS RTT を N 回測り中央値を出すシェルスクリプトを新設する。依存ゼロ
（`scripts/README.md` の既存スクリプト群の作法に揃える）。実行は Hetzner 契約後（本作業では実行しない）。
`scripts/README.md` の「本リポジトリ固有」表へ登録する。

## 受け入れ基準

- [ ] `docs/blocked-tasks.md` が課題本文 (a)〜(h) を反映している
- [ ] #644（B-2 受け皿）が起票され B-2 からリンクされている
- [ ] #344 のチェックリストが実 state と一致し、進捗コメントが投稿されている
- [ ] #24 へ実測コメントが投稿され、`docs/infra/infra.md` が現況に追随している
- [ ] `scripts/measure-region-latency.sh` が用意され `scripts/README.md` に登録されている
- [ ] #483 へ本日の実測反映コメントが投稿されている
- [ ] `node scripts/check-trace-blocks.js` / `check-cross-repo-refs.js` / `check-doc-links.js` が通る
- [ ] PR が作成されている（マージはしない）

## テスト方針

本作業はドキュメント・issue 更新であり、xUnit テストの対象コードは変更しない。検証は上記の機械検査
（trace-blocks・cross-repo-refs・doc-links）と目視レビュー（表の整合・リンク切れ）で行う。

## 計画書との差異

- 差異: なし（本作業は計画書に基づく実装ではなく、実装リポジトリの運用ドキュメントの棚卸しである）

## 未決事項

- LLM API キー（`anthropic-api-key`）未投入について専用 issue を起票するかどうかは、本作業の指示範囲
  （新規 A 項目として記録すること）を超えるため、「追跡: 未起票」として記録するに留め、起票の要否は
  棚卸しの運用者（人間）の判断に委ねる。
