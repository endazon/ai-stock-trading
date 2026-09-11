---
title: 本番 values.yaml の LlmGateway 資格情報を llm-caller へ揃え、既定描画の検査で固定する
type: spec
status: done
related_ids: [FR-04, NFR-05, IADR-0323]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 本番 values.yaml の LlmGateway 資格情報（#764）

## 起点

- #734（PR #735）は `values-local.yaml` だけを `llm-auth-*` へ切り替え、IADR-0323 に追記した。本番 `values.yaml` の
  trade-decision / report（4 箇所）は `kb-auth-*` のまま（#746 の実装中に発見）。`LlmGateway__BaseUrl` が本番既定で空のため
  今日は無害だが、有効化した瞬間に基盤の ServiceCaller 門で 403 になる。

## 設計

| 対象 | 変更 |
| --- | --- |
| `values.yaml` trade-decision / report の `LlmGateway__Auth__ClientId/Secret` | `llm-auth-client-id` / `llm-auth-client-secret` |
| `helm.yml` 既定描画の fail-safe 検査 | `kb-auth-client-id` 参照が在れば赤・`llm-auth-client-id` 参照が無ければ赤（values-local 側の検査と対） |

KB（`KnowledgeBase__Auth__*`）は `kb-auth-*` のまま（陰性対照）。

## 走査した母集合（規則 2・9）

`kb-auth-client` で追跡下の全ファイルを走査: `values.yaml`（LLM 側 4 箇所を変更・KB 側は据え置き）、`values-local.yaml`
（#734 で済み）、`k8s-local-deploy.sh` 鍵表・README（#734 で済み）、IADR-0109 / IADR-0323（据え置き）。

## 受け入れ基準

- [x] 既定描画で `LlmGateway__Auth__ClientId` 2 箇所が `llm-auth-client-id`、`KnowledgeBase__Auth__ClientId` は `kb-auth-client-id` のまま
- [x] helm.yml の検査は values.yaml の変更を戻すと赤
