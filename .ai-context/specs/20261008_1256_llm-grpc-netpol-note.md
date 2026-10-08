---
title: values の LlmGateway__Grpc の注記へ、MSP の本番の NetworkPolicy が REST だけを開けることを足す
type: spec
status: accepted
related_ids: [NFR]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs: []
---

# 仕様書: values の LlmGateway__Grpc の注記へ、MSP の本番の NetworkPolicy が REST だけを開けることを足す（#1256）

## 起点となる計画書（トレーサビリティ）

- 起点: 無採番 NFR（運用の前提の記述。メタ作業）
- 関連: MSP#1811・MSP#1812（MSP の本番の NetworkPolicy に AST → LLM ゲートウェイの ingress を切替で足した）、MSP/IADR-0513
- 起票: #1256

## 目的・背景

`deploy/helm/ai-stock-trading/values.yaml` の report・trade-decision の `LlmGateway__Grpc` の注記は、gRPC へ切り替える前提として
基盤の h2c ポートの配備と Istio PeerAuthentication だけを挙げる。MSP 側の本番の NetworkPolicy は REST だけを開けるため、
この前提を知らずに gRPC へ切り替えると L4 で塞がれる。その事実を注記へ足す。

## MSP 側の実測（`/home/user/microservices-platform`、`origin/develop` `1d71b2b3`）

- `deploy/helm/microservices-platform/values.yaml` 159〜178 行: `networkPolicy.fromAst` は `namespace: ai-stock-trading` と
  3 用途 `kbReader`（target `retrieval`・clients `[trade-decision-service]`）・`kbWriter`（target `document`・clients
  `[information-collection-service, report-service]`）・`llmGateway`（target `llmgateway`・clients `[report-service, trade-decision-service]`）を持ち、
  いずれも `enabled: false`。`llmGateway` の注記は「AST は REST だけを使う（LlmGateway__Grpc は置いていない）ので gRPC 8081 は開けない」。
- `templates/networkpolicy.yaml` 146〜201 行: 3 用途とも `ports` は `services.<target>.port` の 1 本だけを描く
  （注記「ポートは services.<target>.port（REST）だけ。east-west gRPC（grpcPort）は開けない」）。`llmgateway` の `port` は 8080
  （values 691 行）、`grpcPort` は別に宣言されている（693 行〜）。
- `.ai-context/adr/IADR-0513_ast-kb-ingress-network-policy.md` 決定 2 と 2026-10-08 追記（#1811）: gRPC 8081 は開けない。
  AST が gRPC へ切り替えるときは、別のポートを足すのではなく、用途の形（REST だけ）を変える判断として改めて扱う。

## 母集合（規則 9: 誤りの側の文字列で走査した）

誤りの側＝「gRPC 切替の前提を Istio PeerAuthentication だけで述べる記述」と「AST → MSP の gRPC 経路」。

| 走査 | 当たり | 扱い |
| --- | --- | --- |
| `git grep -n "Grpc" deploy/helm/ai-stock-trading/values.yaml` | 619〜621 行（report の `LlmGateway__Grpc`）・778〜780 行（trade-decision の同） | **是正対象**（2 か所） |
| 同上 | `Audit__Grpc`・`CostControl__Grpc`・`MarketMonitor__Grpc`・`Reports__Grpc`・`RiskManagement__Grpc`・`Auth__GrpcOwnerClients__0`・`Grpc__Port` | **除外**: いずれも AST の名前空間の中の呼び出し（呼び先は AST のサービス）で、MSP の `networkPolicy.fromAst` の射程外 |
| `git grep -n -i "KnowledgeBase.*Grpc\|Grpc.*KnowledgeBase" -- backend deploy docs` | 0 件 | KB（検索・文書）の gRPC 経路は AST に存在しない（values は `KnowledgeBase__*__BaseUrl` の REST だけ）。受け入れ基準 2 の「あれば」に該当なし。KB が REST だけである事実は LLM の注記の中で併記した |
| `git grep -n "llmgateway.*8081\|8081.*llmgateway"`（`.ai-context/specs` 除く） | values の 2 か所・IADR-0332（凍結記録）・試験コード | 試験・IADR は対象外（前提の記述ではない／凍結記録） |
| `git grep -n -i "PeerAuthentication" -- docs deploy` を gRPC・LLM で絞る | values 40 行（`mesh` の注記。HTTP の到達の話で gRPC 切替の前提ではない）・values 621/780 行・`docs/blocked-tasks.md` 531 行（STRICT の再測定記録）・`docs/operations/grpc-h2c-measurement-runbook.md` 110 行（AST 内 8081 の測定手順） | values 2 か所のみ是正。他は gRPC→LLM の切替前提を述べていないため除外 |
| `git grep -n "LlmGateway__Grpc" -- docs deploy` | `deploy/helm/ai-stock-trading/README.md` 634 行（LLM 単価の投入。前提の記述ではない）・`docs/operations/grpc-h2c-measurement-runbook.md` 84 行（「LlmGateway__Grpc はこの表に入らない」） | **除外**: どちらも切替の前提を述べていない。`docs/` に LLM ゲートウェイの gRPC 切替の前提を述べる頁は無い |
| `git grep -n "fromAst\|allow-ast-llm"` | 0 件 | 本リポに既存の記述なし |

## 規則 10: この変更で新たに誤りになる自分の記述

- 追記は「MSP の本番の NetworkPolicy は REST 8080 だけを開ける」。MSP 側が gRPC を開ける判断をすれば誤りになるが、
  そのときは追記自体が「MSP 側で改めて判断してもらう」と書くので、判断の結果として本注記を直すのが筋（射程の内）。
- 既存の「前提: … Istio PeerAuthentication」は誤りではなく不足だったため、残して追記する（置き換えない）。
- 導出値: 是正対象 2 か所（report・trade-decision）。clients は MSP values の `[report-service, trade-decision-service]` と一致を確認した。

## 変更

1. `deploy/helm/ai-stock-trading/values.yaml` の report・trade-decision の `LlmGateway__Grpc` の注記（各 2 行）の直後に 4 行のコメントを足す。
   コメントだけで描画は変わらない（既定描画・`values-local.yaml` 描画とも変更前と SHA-256 一致を確認する）。

## やらないこと

- gRPC への切替そのもの。MSP 側の NetworkPolicy の変更。

## 受け入れ基準

1. Given values の `LlmGateway__Grpc` の注記 When 読む Then MSP の本番の NetworkPolicy（`networkPolicy.fromAst.llmGateway`）は REST だけを開け、
   gRPC へ切り替えるなら MSP 側で穴の形を改めて判断する（MSP/IADR-0513）と書かれている（report・trade-decision の 2 か所）。
2. Given 同じ注意を要する他の値 When 母集合を走査する Then KB の gRPC 経路は存在せず、他の `*__Grpc` は AST 内の呼び出しとして除外理由が本書にある。
3. Helm の描画（既定・`values-local.yaml`）が変更前とバイト等価である。
