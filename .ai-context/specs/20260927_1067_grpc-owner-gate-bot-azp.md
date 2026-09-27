---
title: east-west gRPC 面の所有者の門で、トークンの azp が Discord ボットの機密クライアントであることを併せて求める（#1067）
type: spec
status: done
related_ids: [NFR-06, FR-14, ADR-0047, IADR-0051, IADR-0062, IADR-0098, IADR-0284, IADR-0448]
author: endazon (with Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md (planning#690)
---

# 仕様書: gRPC 面の所有者の門に azp の確認を足す（#1067）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR-0047 決定 3・フォローアップ 1（planning#690 の裁定・2026-09-27）。FR-14（Discord の対話）・NFR-06（発注機能へのアクセスは利用者本人のみ）
- 起票: #1067。親: #753（east-west gRPC 段 5 の前提）
- 実装判断: [IADR-0448](../adr/IADR-0448_grpc-owner-gate-requires-bot-azp.md)（新設）・[IADR-0284](../adr/IADR-0284_east-west-grpc-scope-and-order-ruling.md) の日付つき追記
- 計画 ADR のレンジ: 本リポの宣言は `ADR-0001..0046` のままで ADR-0047 を引けないため、同じ PR で `0047` へ引き直す
  （隣接クローン `origin/main` `3c7949f` の `gen-plan-ranges.js --check` → ADR [1, 47]・欠番なし。クローンは shallow のため `git archive` で展開した木で実行）

## 目的・背景

gRPC 面 6 つの門 `OwnerOrService` はロールだけを見る。ボットのトークンは人の利用者と同じ `trading-owner` を持つので、人の利用者の
トークンがメタデータに載っても門を通してしまう（ADR-0047 実測 5）。段 5 でボットを gRPC へ移す前に、所有者の分岐に `azp` の確認を足す。

## 対象範囲

- 変える: 6 面の gRPC service のクラス属性・shim の認可登録（新しいポリシーと判定）・helm（values・values-local）・compose・試験・IADR・テスト仕様書
- 変えない: s2s の分岐・REST の面（`OwnerOrService`）・proto・呼び出し元・helm の `grpcPort`（gRPC 面は既定で開かない）

## 母集合（規則 9: 誤りの側の文字列で全文書を走査してから挙げる）

走査（`origin/develop` `ce3e5cfc`）:

1. `git grep -n "Authorize(Policy" -- backend` → 6 件、いずれも gRPC service（Audit `AuditEventsReadGrpcService`・Configuration `AssumptionsGrpcService`・
   CostControl `CostStateReadGrpcService`・MarketMonitor `WatchlistReadGrpcService`・Report `DailyPolicyReadGrpcService`・Risk `RiskControlsReadGrpcService`）。**全件を変える。**
2. `git grep -n "MapGrpcService" -- backend` → 本番は上の 6 つの `Program.cs` だけ。残りは試験の偽の提供側（`*StubHost`・`GrpcAssumptionsClientIntegrationTests`）で門を持たない → **対象外**（偽物であり、本物の門を試すのは本物の組み立て）。
3. `git grep -n "OwnerOrService"` → REST のエンドポイント群（`RequireAuthorization(OwnerOrService)`）・`AddPolicy` の定義 1 件（shim の `AuthExtensions`）。REST は **対象外**（ADR-0047 決定 3 は gRPC 面だけ。BFF が中継する経路を閉じない）。ポリシー定義は新しいポリシーを隣に足し、既存は変えない。
4. `git grep -n "AddPolicy\|AddAuthorization"` → 本番のポリシー定義は shim の 1 か所だけ（BFF・Backtest は既定の登録のみ）。試験のファクトリにポリシーの再定義は無い。
5. `trading-owner` を持つトークンを作る client（`infra/keycloak/realm-export.json`）: `ai-stock-trading-dev`（公開・利用者のブラウザ）・`ai-stock-trading-owner`（機密・client_credentials 専用・ボット）。`ai-stock-trading-svc` は `trading-service` だけ。→ 既定の許可集合は `ai-stock-trading-owner`。
6. helm・compose でボットの client id を持つ箇所: `discord-owner-auth-client-id`（helm の notification `OwnerAuth__ClientId`・report / risk / market-monitor の `DelegatedActor__TrustedClientIds`）・`DISCORD_OWNERAUTH_CLIENTID`（compose・`.env.example`・`k8s-local-deploy.sh` の既定 `ai-stock-trading-owner`）。→ 6 面は同じ鍵・同じ変数から採る。values-local は risk-management・market-monitor・report の `extraEnv` を丸ごと上書きしているので、そこにも足す。
7. 文書で「gRPC 面の認可は REST と同じ `OwnerOrService`」と書く箇所: 6 面のコードのコメント（**是正**）・凍結の IADR-0331 / 0427 / 0445 / 0446 の本文（**書き換えない**。IADR-0448 §関連に「以後はこう読む」を残す）・索引の各行（IADR-0284 の行に追記）・helm README の `DISCORD_OWNERAUTH_CLIENTID` の行（**是正**。読むサービスが増える）・テスト仕様書（本件の節を足す）。

### この変更で新たに誤りになる自分の記述（規則 10）

- 6 面の試験の「利用者（trading-owner）も読める」陽性対照: azp 無しでは通らなくなる → ボットの azp を付け、#1067 の注記を足した。
- 6 面のクラスのコメント「REST と**同じ** `OwnerOrService`」→ 是正した。
- 計画 ADR のレンジの宣言 `0046` → `0047`（別紙へ履歴を追記）。

## 設計

- 判定（`GrpcOwnerClientGate.Allows`）: 認証済み ∧（`trading-service` ∨（`trading-owner` ∧ `azp` がちょうど 1 つ・空でない・生の値が許可集合に序数一致））。
- 構成（`Auth:GrpcOwnerClients` 配列）: 未構成なら既定 `ai-stock-trading-owner`。構成すると置き換え（構成の側は trim・空白要素を捨てる）。空なら誰の所有者トークンも通さない。1 つの値（空文字を含む）は `AddAiStockTradingAuth` で `InvalidOperationException`。
- ポリシー `GrpcOwnerOrService` を `AddAiStockTradingAuth` で登録し、6 面のクラス属性を替える。

## 受け入れ基準

- [x] 6 面すべてで、`trading-owner` を持つトークンの azp が BFF・公開クライアント・変種（大小文字・接頭辞・接尾辞）・s2s のクライアント、または azp 無しなら PERMISSION_DENIED
- [x] ボットのトークン（`trading-owner` ＋ azp＝`ai-stock-trading-owner`）は通る。s2s は azp を問わず通る
- [x] 同じサービスの REST の面は、azp 無しの `trading-owner` で従来どおり 200
- [x] 許可集合は構成で置き換えられ、既定はボットの機密クライアント。1 つの値の構成は起動時に例外
- [x] 既定が realm の owner 機密クライアント（client_credentials 専用・`trading-owner` だけ）と一致し、helm（values・values-local）と compose の 6 面がボットと同じ出所から採ることを配線試験で固定
- [x] 変異 2 件以上（azp の確認を外す・大小文字を無視する ほか）で赤
- [x] build・test・format・scripts の検査器が通る

## テスト方針（テスト ID は T-10-1723〜T-10-1726。develop の最大 T-10-1722 の次）

| ID | 置き場 | 観点 |
| --- | --- | --- |
| T-10-1723 | PlatformShim.Tests `GrpcOwnerClientGateTests` | 本物の登録のポリシーを `IAuthorizationService` で評価（否定・陽性・REST の門は不変） |
| T-10-1724 | 〃 | 構成（既定・置き換え・空・1 つの値の例外） |
| T-10-1725 | 6 面の gRPC 試験（本物の `Program.cs`） | 否定の試験（9 通りの azp）・陽性対照（ボット・s2s）・同じサービスの REST |
| T-10-1726 | PlatformShim.Tests `GrpcOwnerGateWiringTests` | 既定＝realm・helm・compose の一致 |

## 検証の結果（2026-09-27）

- build 警告の増加なし・`dotnet test` 全プロジェクト緑（IntegrationTests の失敗 11 件はすべて `DockerUnavailableException`）・`dotnet format --verify-no-changes` exit 0・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 477 件 pass・trace-blocks / proto-contracts / reading-budget / knowledge-graph / test-traceability / cross-repo-refs / plan-id-qualification / doc-links / adr-index-sync / commit-messages が exit 0
- 変異 10 件すべて赤（一覧は `docs/tests/FR-10_risk-controls-tests.md` の本件の節）

## 計画書との差異

なし。ADR-0047 フォローアップ 3（IADR-0284 の段 5 の行と段 6 の範囲の改訂）は段 5 の着手時に行う（本 PR は決定 3 の門だけ）。

## 未決事項・残余リスク

- 本番の realm は基盤（MSP）レルムの写しであり、そちらの owner クライアントの性質（client_credentials 専用）は本リポの試験で固定していない。
- 許可集合は登録時に 1 回だけ読む（構成の再読み込みには追随しない）。
