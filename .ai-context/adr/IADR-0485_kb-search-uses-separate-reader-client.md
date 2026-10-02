---
title: IADR-0485 KB の検索は書き手と別の読み手のクライアント（ai-stock-trading-kb-reader）で名乗り、資格情報は KnowledgeBase:SearchAuth から読む。書き手の KnowledgeBase:Auth へは倒れない。経路 B で取引判断の検索を有効にする
type: impl-adr
status: Accepted
related_ids: [FR-08, FR-04, FR-01, NFR-06, ADR-0020, ADR-0032, ADR-0038, IADR-0069, IADR-0072, IADR-0093, IADR-0109, IADR-0293, IADR-0323, IADR-0324, IADR-0341, IADR-0454]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md (ニュースの欠測を判断の文脈へ明示して渡す)
  - planning:projects/ai-stock-trading/07_adr/ADR-0032_mcp-non-exposure-is-enforced-by-attributes-not-the-allowlist.md (決定 2 (1): AST の文書は project=ai-stock-trading を持つ)
  - planning:projects/ai-stock-trading/07_adr/ADR-0038_linked-deploy-auth-realm-is-the-platform-realm.md (決定 2: 経路は同じレルムを指す)
---

# IADR-0485: KB の検索は読み手のクライアントで名乗る（#1078）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-02
- 決定者: 利用者裁定（MSP#1696 のコメント 2026-10-02・案 B）を Claude Code が AST 側の配線へ落とした。基盤側の主体とポリシーは MSP/IADR-0492。

## 起点・関連

- 起票: [#1078](https://github.com/endazon/ai-stock-trading/issues/1078)（実環境の取引判断に収集情報が RAG から 1 件も届かない）
- 基盤側の対: MSP#1696（読み手の ABAC 主体）・MSP/IADR-0492（読み手のクライアント `ai-stock-trading-kb-reader`・ポリシー 1 本）
- 関連する計画書 ID: FR-08（KB の保存と RAG）・FR-04（判断の材料）・FR-01（情報収集）
- 計画 ADR: ADR-0020（欠測の明示）・AST/ADR-0032 決定 2 (1)・ADR-0038 決定 2
- 関連する実装 ADR: [IADR-0093](IADR-0093_kb-writer-cross-realm-s2s.md)（KB の s2s は MSP レルムの専用クライアント。本 IADR は**検索の部分**を読み手へ移す）・
  IADR-0454（検索は本文の Scope `project=ai-stock-trading` を送る。#1083）・IADR-0323（LLM の s2s を別主体へ分けた前例）・
  IADR-0324（s2s の Authority は `global.authAuthority` から導出）・IADR-0109（`ast-secrets` を再作成しない）
- 作業仕様書: [`.ai-context/specs/20261002_1078_kb-reader-client.md`](../specs/20261002_1078_kb-reader-client.md)
- 基点コミット: `origin/develop` `10a32a8e`

## コンテキストと課題

#1078 の原因 1・2 のうち、AST 側に残っていたのは次の 2 つである（Scope の送出は #1083、本文の取り込みは #565 / IADR-0274 で済んでいる）。

1. **検索の宛先が空**（`KnowledgeBase__Search__BaseUrl`。経路 B の `values-local.yaml`）。空なら検索は NoOp で 0 件。
2. **検索が書き手の資格情報で名乗る**（`KnowledgeBase:Auth` = `ai-stock-trading-kb-writer`）。書き手のトークンは `profile` を持たず
   `preferred_username` が載らないうえ、基盤の ABAC に書き手が AST の文書を読むポリシーが無い（書き手は自分の写しだけを読める）。
   裁定（MSP#1696・案 B）は、読み手を書き手と分け、最小権限（読み手は書けない）とした。

## 決定

### 決定 1 — 検索は `KnowledgeBase:SearchAuth`（読み手）、保存・台帳は `KnowledgeBase:Auth`（書き手）。互いへ倒れない

- `KnowledgeBaseAuthExtensions` に節名を受ける形（`AddAiStockTradingKnowledgeBaseAuth(config, sectionName)` / `ReadOptions(config, sectionName)`）を足し、
  `AddAiStockTradingKnowledgeBase` の検索の名前付きクライアント（`kb-search`）だけを `KnowledgeBase:SearchAuth` で構成する。
  既存の公開の形（引数 2 つ）は `KnowledgeBase:Auth` のまま（保存・台帳は変わらない）。
- 🔴 **フォールバックしない。** `SearchAuth` が揃わなければ、`Auth`（書き手）が揃っていても検索にはトークンを付けない（401 → 空結果。
  `HttpKnowledgeBaseSearch` の fail-safe）。倒れると、書ける資格情報で検索を名乗る（裁定の最小権限に反する）うえ、書き手には AST の文書を
  読むポリシーが無いので結果も 0 件のままで、誤りが表に出ない。逆向き（読み手の資格情報で保存する）も起こらない。
- 試験: `KnowledgeBaseAuthTests`（検索は読み手で名乗る／読み手が無ければ書き手が揃っていてもトークンを付けない／読み手の資格情報は保存に使わない／
  節は独立に読む）。

### 決定 2 — 取引判断の検索の資格情報は `ast-secrets` の `kb-reader-auth-client-id` / `kb-reader-auth-client-secret`

- 取引判断の Deployment から書き手の `kb-auth-client-*` を外し、`KnowledgeBase__SearchAuth__ClientId` / `__ClientSecret` を読み手の鍵から読む
  （既定 `values.yaml`・経路 B `values-local.yaml` の両方）。取引判断は KB へ書かない。
- Authority は `global.authAuthority` から導出する（`templates/deployment.yaml`。`KnowledgeBase__Search__BaseUrl` が非空のときだけ。IADR-0324 と同じ形）。
- 鍵は Vault の `ai-stock-trading/app-secrets`（ESO が `dataFrom.extract` で吸い上げる。dev の種は MSP の `bootstrap.sh`）か、
  ESO を使わない経路では `scripts/k8s-local-deploy.sh` が作る（ID は dev 既定 `ai-stock-trading-kb-reader`・秘密は空既定 ＝ env `KB_READER_AUTH_CLIENTSECRET` で与える）。
  **秘密の値は AST のリポジトリに置かない。**
- 描画の検査（`.github/workflows/helm.yml`）: 取引判断が `kb-auth-client-*` を参照したら赤・読み手の鍵が無ければ赤（既定と経路 B の両面）。
  s2s の Authority の非空件数（経路 B）は 4 → 5。

### 決定 3 — 経路 B で取引判断の検索を有効にする（宛先 `http://retrieval-service.microservices-platform:8080`）。本番の既定は空のまま

- 経路 B（`values-local.yaml`）の `KnowledgeBase__Search__BaseUrl` を MSP の RetrievalService へ向ける。
- 本番の既定（`values.yaml`）は空のまま —— 本番は MSP の既定拒否の NetworkPolicy が AST 名前空間からの ingress を塞いでいる（裁定のとおり別件）。

## 統制と現在の実現手段

| 統制 | 現在の実現手段 | 配備までの暫定手段 |
| --- | --- | --- |
| 検索は読み手で名乗り、書き手の資格情報を使わない | **ある**: 節の分離（試験）・取引判断の env（描画の検査） | — |
| 読み手は AST の文書だけを読み、書けない | 基盤側（MSP/IADR-0492）。経路 B は realm の宣言と dev seed | 本番はポリシーの投入まで検索は 0 件（広がらない） |
| 実データで参考情報が 1 件以上載る | 🔴 **未実測**（PoC で確かめる） | — |

## 結果

- 良い影響: 経路 B で取引判断の検索が AST の文書（収集記事・確定報告書）を引ける。読み手の資格情報が漏れても KB へは書けない。
- 悪い影響 / トレードオフ: 情報収集の Deployment は検索の宛先（空）を持つが読み手の資格情報を持たない。宛先を入れるなら読み手の env を足す必要がある
  （`values.yaml` の注記に書いた。無ければ 401 → 空結果で、広がる向きには倒れない）。

## 残余

1. 実データでの受け入れ（参考情報 1 件以上がプロンプトに載る）は PoC で確かめる（基盤の realm の再適用・seed の投入・AST の再起動の後）。
2. 本番の NetworkPolicy（MSP 側。別件）。
3. #1078 の残り —— 欠測の明示（`collection-status`）を RAG を経由せず直接渡すかの検討は本 IADR の外（#1078 の「やること」3 つ目）。
