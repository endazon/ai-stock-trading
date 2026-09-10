---
title: LLM ゲートウェイ呼び出しの資格情報を kb-writer から ai-stock-trading-llm-caller へ切り替える
type: spec
status: done
related_ids: [FR-04, FR-16, NFR-05, IADR-0323]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md
---

# 仕様書: LLM 呼び出しの資格情報を LLM 専用 client へ（#734）

## 起点

- issue #734。基盤の LlmGateway が `ServiceCaller`（`platform-service`）を要求するようになり（MSP#1365）、AST が使う
  `ai-stock-trading-kb-writer`（`platform-operator`）では 403 になる。基盤は AST 用に別主体 `ai-stock-trading-llm-caller` を
  realm へ足した（MSP#1368）。

## 実測（2026-09-10）

| 呼び出し | 結果 |
| --- | --- |
| kb-writer のトークンで `POST /complete` | 200 —— 稼働イメージが MSP#1365 より古く門が無いだけ |
| llm-caller のトークンで `POST /complete` | 200（`claude-opus-5`） |

**いまは偶然動いており、基盤の再デプロイで壊れる。**

## 設計

| 対象 | 変更 |
| --- | --- |
| `values-local.yaml` trade-decision / report の `LlmGateway__Auth__ClientId/Secret` | `ast-secrets` の `llm-auth-client-id` / `llm-auth-client-secret` を参照 |
| `scripts/k8s-local-deploy.sh` の `ast-secrets` 鍵表 | `llm-auth-client-id`（dev 既定 `ai-stock-trading-llm-caller`・`LLM_AUTH_CLIENTID`）/ `llm-auth-client-secret`（`LLM_AUTH_CLIENTSECRET`） |
| KB（③）の `kb-auth-*` | **不変**（KB 書き込みは kb-writer のまま。別主体にする理由は MSP#1368） |
| IADR-0323 | 決定 4 の負債解消を日付つき追記 |

## 走査した母集合（規則 2・9）

`kb-auth-client|KB_AUTH_CLIENT` で追跡下の全ファイル（`.claude/` `.ai-context/specs/` `CHANGELOG` 除く）を走査:
`values-local.yaml` 8 箇所（うち LLM 側 4 箇所を変更・KB 側 4 箇所は据え置き）、`k8s-local-deploy.sh` 表、
`k8s-local-deploy.test.sh` env 列、helm README 表、IADR-0109（保持の設計・据え置き）、IADR-0323（追記）。

## 受け入れ基準

- [x] `helm template -f values-local.yaml` で trade-decision / report の `LlmGateway__Auth__ClientId` が `llm-auth-client-id` を参照
- [x] 陰性対照: `KnowledgeBase__Auth__ClientId` は `kb-auth-client-id` のまま
- [x] `k8s-local-deploy.test.sh` に dev 既定の固定（T-734-01）を足し全緑
- [ ] 稼働: `ast-secrets` に `llm-auth-*` を入れ trade-decision / report を入れ直す（取引サイクルの観測後）
