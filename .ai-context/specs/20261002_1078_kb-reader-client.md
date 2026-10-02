---
title: 作業仕様書 — 取引判断の KB 検索を読み手のクライアントで名乗り、経路 B で有効にする（#1078 / MSP#1696）
type: spec
status: done
related_ids: [FR-08, FR-04, FR-01, NFR-06, ADR-0020, ADR-0032, ADR-0038, IADR-0069, IADR-0093, IADR-0109, IADR-0323, IADR-0324, IADR-0454, IADR-0485]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0032_mcp-non-exposure-is-enforced-by-attributes-not-the-allowlist.md (決定 2 (1))
  - planning:projects/ai-stock-trading/07_adr/ADR-0038_linked-deploy-auth-realm-is-the-platform-realm.md (決定 2)
issue: "#1078"
---

# 作業仕様書 — 取引判断の KB 検索を読み手で名乗る（#1078）

> 着手前に作成する。裁定は MSP#1696 のコメント（オーナー 2026-10-02・案 B）。基盤側の主体とポリシーは MSP/IADR-0492（MSP#1696）。
> 判断の記録は [IADR-0485](../adr/IADR-0485_kb-search-uses-separate-reader-client.md)。

## 射程

- **やる**: 検索の資格情報の節を書き手と分ける（`KnowledgeBase:SearchAuth`）。取引判断の Deployment の資格情報を読み手の鍵へ替える。
  経路 B で検索の宛先を MSP の RetrievalService へ向ける。`ast-secrets` の鍵（`kb-reader-auth-client-*`）を `k8s-local-deploy.sh` に足す。描画の検査。運用文書。
- **やらない**: 本番の既定で検索を有効にすること（MSP の NetworkPolicy が塞いでいる。別件）。欠測の明示を RAG の外で渡す件（#1078 の 3 つ目）。
  基盤側（realm・ABAC）は MSP#1696 で行う。秘密の値は置かない。

## 受け入れ基準（MSP#1696 の裁定）と写像

| 基準 | 写像 |
| --- | --- |
| 読み手を書き手と分ける（最小権限・読み手は書けない） | `KnowledgeBaseAuthTests`: 検索は読み手で名乗る／読み手が未設定なら書き手が揃っていても検索にトークンを付けない／読み手の資格情報は保存に使わない／節は独立に読む。描画の検査（`helm.yml`）: 取引判断に `kb-auth-client-*` が在れば赤・`kb-reader-auth-client-*` が無ければ赤（既定・経路 B） |
| userId は `preferred_username`（`profile`） | 基盤側（MSP の realm 宣言）。AST 側は読み手のクライアント ID を送るだけ |
| `/authz/scope` が AST の文書だけの範囲 | 基盤側の試験（MSP `AstKbReaderPolicySeedTests` / `AstKbReaderSearchScopeTests`）。AST 側は Scope `project=ai-stock-trading` を送る（#1083 / IADR-0454。変更なし） |
| 実データで参考情報が 1 件以上 | 🔴 PoC で確かめる（本 PR では試験しない。issue は閉じず Refs） |
| 経路 B で検索が配線される | 描画の検査（`helm.yml` #776/#781 のステップ）: 経路 B の s2s Authority の非空件数 4 → 5（`KnowledgeBase__SearchAuth__Authority` が導出される） |

## 母集合（規則 9・10・11）

### 規則 9（誤りの側の文字列で走査）

| 走査した文字列 | 範囲 | 当たり | 追随 |
| --- | --- | --- | --- |
| `KnowledgeBase__Auth` / `KnowledgeBase:Auth` | 追跡下の全ファイル（`.ai-context/specs/`・CHANGELOG を除く） | values.yaml 3 面（情報収集・報告・取引判断）・values-local.yaml 3 面・template 1・helm.yml 3・コード 3・試験 1・README 1 | 取引判断の 2 面を `SearchAuth` へ。情報収集の注記「書き込み/検索の s2s は kb-writer」を直した。報告・情報収集の保存は不変 |
| `kb-auth-client` / `KB_AUTH_CLIENT` | 同上 | values 6・deploy スクリプト 2・その試験 1・README 1・runbook 1 | 取引判断の 2 面だけ `kb-reader-auth-client-*` へ。鍵の一覧（deploy スクリプト・試験・README・runbook）へ読み手を足した |
| `llm-auth-client`（別主体へ分けた前例） | 同上 | helm.yml の検査 2・deploy・runbook・README | 同じ形の検査を読み手にも置いた（helm.yml の fail-safe ステップ） |
| `Search__BaseUrl` | 同上 | values.yaml 2・values-local 2・template 1・README | 経路 B の取引判断だけ宛先を入れた（情報収集は空のまま） |
| `RAG` ＋ `kb-writer` | 同上 | values.yaml の取引判断の注記 1 | 読み手へ書き換えた |

### 規則 10（この変更で新たに誤りになる自分の記述・導出値）

- `scripts/k8s-local-deploy.sh` の「非空の既定を持つ 5 キー」——計算し直すと llm-caller を足した時点で 6、読み手を足して **7**。直した（日付つきの注記）。
- `helm.yml` の s2s Authority の期待件数（経路 B）——**4 → 5**（3 面）。エラーメッセージの env 名の列挙にも `SearchAuth` を足した。
- `values.yaml` の情報収集の注記「KB 書き込み/検索の s2s は … kb-writer」——検索は読み手になったので誤り。直した。

### 規則 11（窓）

時間差を扱う是正ではない。**該当なし。**

## 変異の確認

| 変異 | 期待 |
| --- | --- |
| 検索の名前付きクライアントを `KnowledgeBase:Auth` で構成する（フォールバック） | `KnowledgeBaseAuthTests` の 2 件が赤 |
| 取引判断の `KnowledgeBase__SearchAuth__ClientId` を `kb-auth-client-id` へ戻す | `helm.yml`「Assert fail-safe defaults」が赤 |
| template の `KnowledgeBase__SearchAuth__Authority` の導出を消す | `helm.yml`「Assert every rendered realm …」が赤（4 件・期待 5 件） |

## タスク

- [x] `KnowledgeBaseAuthExtensions` / `KnowledgeBaseExtensions`（節の分離）と試験
- [x] values.yaml / values-local.yaml / template
- [x] helm.yml の検査
- [x] `k8s-local-deploy.sh` と試験
- [x] README（chart）・`docs/operations/vault-secrets-runbook.md`
- [x] IADR-0485
