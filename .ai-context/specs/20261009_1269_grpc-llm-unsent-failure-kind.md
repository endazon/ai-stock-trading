---
title: gRPC の LLM 完了経路でも Sent=false の原因（failure_kind / upstream_status_code）を読む（#1269）
type: spec
status: accepted
related_ids: [FR-04, FR-11, UC-01, ADR-0010, IADR-0517, IADR-0332, IADR-0328]
author: claude (Claude Code)
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 判断根拠を必ず記録・FR-11 後から監査できる)
  - planning:projects/ai-stock-trading/07_adr/ADR-0010 (platform LLM ゲートウェイの越境ルーティング)
---

# gRPC の LLM 完了経路でも Sent=false の原因を読む（#1269）

## 起点

- [#1269](https://github.com/endazon/ai-stock-trading/issues/1269)（#1267 の後続。PR #1268 の独立監査の 🟡「`GrpcLlmCompletionTransport` は `routing_reason` だけを写している」。IADR-0517 の残余リスク）。
- 計画: FR-04（判断根拠を必ず記録）・FR-11（後から監査できる）・ADR-0010。計画の裁定は要らない（#1267 の決定を輸送の片側へ広げるだけ）。
- 基盤: MSP#1824（Closes MSP#1819）が MSP develop 51633872 でマージ済み。基盤の `completion.proto` に
  `CompleteResponse.failure_kind = 9` / `upstream_status_code = 10`、`CompletionStreamEvent.failure_kind = 10` / `upstream_status_code = 11` が加わった
  （proto3 なので `""` / `0` が「無い」）。

## 現況（origin/develop 2e8a07d3）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | REST は `failureKind` / `upstreamStatusCode` を `LlmGatewayUnsent.ParseKind` / `ParseStatusCode`（`JsonElement?`）で寛容に読む | `Shared.Contracts/Llm/RestLlmCompletionTransport.cs` |
| 2 | gRPC は `routing_reason` だけを写し、原因の種類・状態コードは常に null（＝記録は常に「種別不明」） | `Shared.Infrastructure/Composable/Llm/GrpcLlmCompletionTransport.cs` |
| 3 | AST の proto の写しに 2 フィールドが無い。写しの規則は「`csharp_namespace` 以外の wire 面は正本と 1 文字も違わない」「追随は人手」（IADR-0332 決定 1・ファイル冒頭の出所） | `Shared.Infrastructure/Protos/platform/llmgateway/v1/completion.proto` |
| 4 | proto の後方互換は `scripts/check-proto-contracts.js` が baseline（`scripts/proto-contract-baseline.json`）と比べる（追加は非破壊・`--update` で baseline を更新） | `scripts/` |

## 母集合（規則 9・10）

誤りの側の文字列で走査した: `git grep -nE '写しにまだ無い|写しに無い|確定後に写し|MSP#1819 の確定後|routing_reason\` だけ|原因の種類が無い'`（`.ai-context/specs` を除く）と、
`git grep -n 'completion.proto\|proto の写し'`、`docs/` の `routing_reason|failureKind|failure_kind|RoutingReason|completion.proto`。

| 箇所 | 扱い |
| --- | --- |
| `GrpcLlmCompletionTransport.cs`（「proto の写しにまだ無い…null のまま」） | **是正**（写像を足し、注記を書き換え） |
| `GrpcLlmCompletionTransportTests.cs` T-04-019 の注記（同上） | **是正**（注記だけ。試験の主張は「載せない応答は null」で不変） |
| `completion.proto`（写し） | **追随**（2 メッセージに 2 フィールドずつ。出所に追随日と正本のコミットを追記） |
| `scripts/proto-contract-baseline.json` | **更新**（`--update`。非破壊の追加 4 件） |
| IADR-0517 決定 1「gRPC は `routing_reason` だけを運ぶ」・残余リスク「proto の写しには原因の種類が無い」・索引行の残余 | **日付つき追記**（原文は残す） |
| IADR-0284 / 0328 / 0331 / 0332（写しの存在・人手追随の言及） | 対象外（誤りなし。人手追随の規則はそのまま） |
| `TradeDecisionService/Tests/.../LlmGatewayUnsentReasonTests.cs` T-04-004「原因の種類が無い（現行の基盤。MSP#1819 の前）」 | 対象外（REST の旧い基盤＝後方互換の試験として正しい） |
| `docs/` | 該当なし |

規則 10: 本件で書いた IADR-0517 の追記・試験の注記は「マージ済み・確定名」の事実に依る。導出値（フィールド番号）は正本の `git show origin/develop:…/completion.proto` から引き直し、
写しのコメントを除いた本文を正本と突き合わせた（差は `option csharp_namespace` の 1 行だけ）。

規則 11（窓）: 該当しない。

## 設計

1. **proto の写しを正本へ追随する**（IADR-0332 決定 1 の写し方のまま）: 4 フィールドの名前・番号・型を正本どおりに足す。コメントは AST の表記（他リポジトリの issue を修飾）で書く。
   SSE 相当の `CompletionStreamEvent` は本リポが消費していないが、「写しが正本の部分集合ではない」ことを保つため同時に写す（写しの冒頭の方針）。
2. **gRPC の写像は REST と同じ読み取りを通す**: `LlmGatewayUnsent.ParseKind(string?)`（既存）と、新しく足す `LlmGatewayUnsent.ParseStatusCode(int)`。
   - `ParseKind`: `""` → null、既知の 3 値（大小・`_`・`-` を無視）→ 種類、未知の値・数字だけ → null（REST の文字列と同じ関数）。
   - `ParseStatusCode(int)`: 100〜599 → そのまま、それ以外（proto3 の既定 `0` を含む）→ null。REST の `ParseStatusCode(JsonElement?)` の範囲判定もこの 1 か所へ寄せる（判定を 2 か所に持たない）。
   - 🔴 新しい読み取りを gRPC 側へ別に書かない（同じ申告が輸送によって別の記録になる事故を構造的に避ける）。
3. **実装 ADR は起こさない**: IADR-0517 決定 1 の写像を輸送のもう片側へ広げるだけで、新しい判断は無い。IADR-0517 へ日付つき追記で残余の解消を記録する。

## 受け入れ基準

- [x] 写しの proto のフィールド名・番号・型が正本（MSP develop 51633872）と一致する（`csharp_namespace` 以外）。
- [x] gRPC で `failure_kind` / `upstream_status_code` を受けると、REST と同じ `LlmCompletionPayload`・`LlmGatewayUnsentCause`・1 行の説明になる
  （egress_denied・upstream_error 429・upstream_error 状態なし・provider_missing・未知の種類・範囲外の状態コード・旧い基盤の Sent=false・Sent=true）。
- [x] `""` / `0` は null（後方互換: 2 フィールドを持たない旧い wire を読んでも null）。
- [x] `check-proto-contracts` が緑（baseline 更新済み）。
- [x] ビルド警告 0・`dotnet format --verify-no-changes`・影響プロジェクトの試験が緑。

## 試験（T-04-023〜024。T-04-001〜022 は #1267）

| ID | 内容 | ファイル |
| --- | --- | --- |
| T-04-023 | REST と gRPC が同じ入力から同じ記録（payload・原因・説明）を作る（8 形）／陰性対照: gRPC で種類があれば説明に載り、無ければ「種別不明」・上流を書かない | `Shared.Infrastructure.Tests/LlmTransportUnsentParityTests.cs` |
| T-04-024 | 写しのフィールド番号が正本と一致（9・10／10・11）／2 フィールドを持たない旧い wire（手組みのバイト列）は既定値として読め、出口では null | 同上 |

### 変異の実測（変えて赤を確かめ、戻した）

| 変異 | 赤になった試験 |
| --- | --- |
| M1 gRPC の `FailureKind` を null のまま（変更前の形） | T-04-023 の 6 件 |
| M2 gRPC の状態コードを `0 → null` だけで運ぶ（範囲判定を通さない） | T-04-023「範囲外の状態コード」 |

## 範囲外

- REST と gRPC でトークン数の欠落の扱いが違う（REST は欠落 → null、gRPC は 0）。基盤は常に int を返すため実害は無く、本件の範囲外（試験では揃えて与える）。
- `CompleteStream`（逐次生成）は本リポが呼んでいない（IADR-0323 残余リスク）。写しにフィールドは足したが写像は無い。

## 残余

- proto の写しは引き続き人手追随（正本が変わっても CI は気付かない。IADR-0332 残余リスク）。
