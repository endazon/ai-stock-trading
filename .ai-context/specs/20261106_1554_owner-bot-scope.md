---
title: ボットの資格情報で届く REST の所有者の操作を、ボットが使う経路の許可表とボット専用のロールで絞る（段 1 は記録のみ）
type: spec
status: accepted
related_ids: [NFR-06, FR-14, FR-10, UC-06, ADR-0047, IADR-0622, IADR-0615, IADR-0611, IADR-0448, IADR-0449, IADR-0011]
author: claude (Claude Code)
created: 2026-11-06
updated: 2026-11-06
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06・FR-14「設定値の変更は Discord からは参照のみ」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md (決定 1・3)
---

# 仕様書: ボットの REST の許可表とボット専用のロール（#1554）

## 起点

- #1554。IADR-0615 決定 7 の B（オーナー裁定 2026-11-05「B → C」）。設計の裁定（オーケストレーター 2026-11-06）:
  - Q1 許可表は 21 本（読み取りを含む）。母集合は機械的に引く。漏れは試験で赤にする。
  - Q2 基盤のレルムの変更は基盤へ起票する（MSP#2441）。AST の写しはこの PR で揃える。
  - Q3 段は `Auth:OwnerBot:Mode`（#1541 の `Auth:OwnerToken:Mode` と別）。
  - Q4 REST は退避先として残す。gRPC の門はロールの条件を広げるだけ。
- 前提: #1541（IADR-0611。REST の所有者の判定の aud / azp）・IADR-0448 / IADR-0449（gRPC の所有者の面の azp）。

## 決定（IADR-0622）

1. **許可表 `OwnerBotRoutes`**（共有の shim）: ボットの REST アダプタが呼ぶ（メソッド, 経路の雛形）の 21 組。提供側は該当の経路に `.AllowOwnerBot()`（エンドポイントのメタデータ）を付ける。
2. **判定**（`OwnerTokenBindingHandler` を広げる。OwnerOnly・OwnerOrService の所有者の分岐）:
   - ボットのクライアント（azp がボットのクライアントの集合＝`Auth:GrpcOwnerClients`、既定 `ai-stock-trading-owner`）の所有者のトークンが、印の無い経路へ来た → `Auth:OwnerBot:Mode` が `Report`（既定・段 1）なら Warning を残して続ける。`Enforce`（段 2）なら通さない（403）。
   - ロール `trading-bot` は、azp がボットのクライアントで、かつ印の付いた経路でだけ所有者と同じに扱う。それ以外（他の azp・印の無い経路）は**段に関係なく通さない**（誰も持っていないロールなので、配備中の通信を止めない）。
   - その後は従来どおり #1541 の aud / azp の判定（`Auth:OwnerToken:Mode`）。
   - 利用者（BFF）のトークン・サービスの分岐（`trading-service`）は変えない。
3. ポリシーのロールの条件: OwnerOnly は `trading-owner` か `trading-bot`、OwnerOrService は加えて `trading-service`（`trading-bot` の可否は 2 の判定器が決める）。
4. **gRPC の所有者の面**: ロールの条件を `trading-owner` または `trading-bot` に広げる（azp がボットのクライアントであることは従来どおり求める）。gRPC 面の中身はボットの操作だけなので、許可表は置かない。
5. chart: `global.ownerBotScope.mode`（Report / Enforce 以外は描画で止める）を auth のサービスへ `Auth__OwnerBot__Mode` で描く。
6. 単体実行向けのレルムの写し（`infra/keycloak/realm-export.json`）: ロール `trading-bot` を足し、`service-account-ai-stock-trading-owner` に付ける（`trading-owner` は残す）。基盤のレルムの同じ変更（段 ②）と、所有者のロールの外し（段 ⑤）は MSP#2441。

## 段

① 本 PR（提供側がロールを受け入れ、印の外を記録する）→ ② 基盤のレルムでボットに `trading-bot` を付ける → ③ 段 1 の記録（「ボットのクライアントが許可表の外の所有者の経路を呼んだ」）が出ないことを確かめる → ④ `global.ownerBotScope.mode=Enforce` → ⑤ 基盤のレルムでボットから `trading-owner` を外す。

## 母集合（規則 9）と引き方

- **許可表の母集合**: ボットの REST アダプタ（`backend/Services/NotificationService/Infrastructure/ExternalServices/Http*Controller.cs`）の、`/` で始まる文字列リテラル（補間の `{…}` は経路の引数）を全部引いたもの。引き方は試験 `OwnerBotRouteCoverageTests` がそのまま行う（源泉を読み、許可表の経路と**集合で一致**を求める）。ボットの経路を足して許可表に足さなければ赤、許可表に残った古い経路も赤。
  - 引いた結果（21 本）: リスク管理 10・報告書 9・市場監視 2（下の表）。正は試験。
- **提供側の印の母集合**: 各提供側（リスク管理・報告書・市場監視）の本物の組み立て（`*WorkerWebApplicationFactory`）の `EndpointDataSource` から、印の付いた経路を引く。試験（各サービスの `OwnerBotRoutesMarkedTests`）が「印の付いた（メソッド, 雛形）＝許可表のそのサービスの分」を集合で求める。
- **所有者のポリシーの母集合**: `OwnerOnly`・`OwnerOrService` の作り（`AuthExtensions`）。判定器は 1 つ。
- **gRPC の門**: `GrpcOwnerClientGate.AllowsOwner`（`GrpcOwnerOnly`・`GrpcOwnerOrService` の所有者の分岐）。
- **除外**: BFF の経路（利用者のトークン。変えない）。ボットの REST アダプタのうち所有者のトークンを使わないものは無い（全アダプタの名前付き HttpClient が owner の機密クライアントのトークンを付ける）。

| メソッド | 経路 | 提供側 |
| --- | --- | --- |
| POST | /risk-controls/kill-switch/engage | リスク管理 |
| POST | /risk-controls/kill-switch/disengage | リスク管理 |
| POST | /risk-controls/pause | リスク管理 |
| POST | /risk-controls/resume | リスク管理 |
| GET | /risk-controls/status | リスク管理 |
| GET | /risk-controls/stage-gate | リスク管理 |
| POST | /risk-controls/stage-gate/transition | リスク管理 |
| POST | /risk-controls/stage-gate/withdrawal/evaluate | リスク管理 |
| POST | /risk-controls/good-faith-violations/clear | リスク管理 |
| POST | /risk-controls/position-drift/adopt | リスク管理 |
| GET | /reports/{periodKey}/review | 報告書 |
| GET | /reports/period-keys | 報告書 |
| GET | /reports | 報告書 |
| POST | /reports/{periodKey}/confirm | 報告書 |
| POST | /reports/{periodKey}/request-changes | 報告書 |
| POST | /reports/{periodKey}/regenerate | 報告書 |
| POST | /reports/policy-revisions | 報告書 |
| GET | /reports/policy-revisions/watchlist-proposal | 報告書 |
| POST | /reports/policy-revisions/{attemptId}/watchlist-apply-result | 報告書 |
| GET | /monitor/watchlist | 市場監視 |
| POST | /monitor/watchlist/proposal-apply | 市場監視 |

（メソッドと雛形の正は提供側の本物の経路。上の表は試験で突き合わせる。）

## 受け入れ基準

- [x] ボットのクライアントのトークンは、印の付いた経路では従来どおり通る。印の無い所有者限定の経路は、段 1 で記録して通し、段 2 で 403（`OwnerBotScopePolicyTests`・各提供側の `OwnerBotRoutesMarkedTests`）。
- [x] 利用者（BFF）のトークンとサービスの分岐は従来どおり（同上）。
- [x] `trading-bot` は、ボットのクライアントかつ印の付いた経路と gRPC の所有者の面でだけ通る。他の azp・印の無い経路では段に関係なく通らない（同上・`GrpcOwnerClientGateTests` の realm の写し）。
- [x] ボットのアダプタの経路・許可表・提供側の印が一致しないと試験が赤になる（`OwnerBotRouteCoverageTests`・`OwnerBotRoutesMarkedTests`。印を 1 つ外す・段 2 の判定を外す変異で赤を確かめた）。
- [x] `global.ownerBotScope.mode` は Report / Enforce 以外を描画で止め、既定は Report（helm.yml の step）。

## 範囲外

- 承認の面（IADR-0615 決定 7 の C）。gRPC 宣言時に REST をボットから閉じること。
- 基盤のレルムの変更（MSP#2441）。
