---
title: IADR-0448 east-west gRPC 面の所有者の門は、トークンの azp が Discord ボットの機密クライアントであることを併せて求める（s2s の分岐と REST の面は変えない）
type: impl-adr
status: Accepted
related_ids: [NFR-06, FR-14, ADR-0047, IADR-0051, IADR-0062, IADR-0098, IADR-0284, IADR-0331, IADR-0427, IADR-0445, IADR-0446]
author: endazon (with Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md
---

# IADR-0448: east-west gRPC 面の所有者の門は、トークンの azp が Discord ボットの機密クライアントであることを併せて求める（#1067）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-09-27
- 決定者: Claude Code（実装）／ endazon（裁定は計画 ADR-0047 決定 3・planning#690）

## 起点・関連

- 計画: ADR-0047 決定 3・フォローアップ 1（planning#690 の裁定）。関連要求 FR-14（Discord の対話）・NFR-06（発注機能へのアクセスは利用者本人のみ）
- 関連する実装仕様書: [`.ai-context/specs/20260927_1067_grpc-owner-gate-bot-azp.md`](../specs/20260927_1067_grpc-owner-gate-bot-azp.md)
- 前提: [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md)（east-west gRPC の段。本件は段 5 の前提）、
  [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)・[IADR-0098](IADR-0098_owner-realm-client.md)（ボットの機密クライアント）、
  [IADR-0051](IADR-0051_service-to-service-auth.md)（`OwnerOrService`・s2s）
- 関連 issue: #1067（本件）、#753（east-west gRPC の親）

## コンテキストと課題

AST の gRPC 面は 6 つある（Audit・Configuration・CostControl・MarketMonitor・Report・Risk の読み取り）。いずれもクラス属性の
`[Authorize(Policy = OwnerOrService)]` を門にしており、ロール（`trading-owner` または `trading-service`）だけを見る。

ボットのトークン（owner マップの機密クライアント `ai-stock-trading-owner` が client_credentials で取る）は、人の利用者と**同じ**
`trading-owner` を持つ。計画 ADR-0047 はボットのトークンをサービスの身元と分類し（決定 1）、段 5 でメタデータに載せる（決定 2）と決めた。
このままだと、人の利用者のトークン（`azp` は BFF・ブラウザの公開クライアント）がメタデータに載っても gRPC 面の門は通してしまう
（ADR-0047 実測 5）。ADR-0047 決定 3 は、gRPC 面の所有者の門に `azp` がボットの機密クライアントであることを併せて求めると裁定した。

## 決定

### 決定 1 — gRPC 面の門は新しいポリシー `GrpcOwnerOrService`。所有者の分岐だけに `azp` の確認を足す

- `AddAiStockTradingAuth` が `GrpcOwnerOrService` を登録する: 認証済み ∧（`trading-service` ∨（`trading-owner` ∧ 呼び出し元のクライアントが許可集合に在る））。
- **s2s の分岐は変えない**（`azp` を見ない。サービスのロールを併せ持つトークンもサービスの分岐で通る）。
- 6 面のクラス属性を `OwnerOrService` から `GrpcOwnerOrService` へ替える。**REST の面は `OwnerOrService` のまま**（人の利用者は BFF が中継する REST で操作する。ADR-0047 決定 3 は gRPC 面だけ）。
- 判定は `GrpcOwnerClientGate`（shim の `Foundation/Auth`）が持つ。
  - `azp` クレームが**ちょうど 1 つ**・空でない・**生の値が序数一致**で許可集合に在るときだけ真。
  - 🔴 前後空白を落とさない・大小文字を畳まない・接頭辞で比べない（変種は別のクライアントである）。
  - 🔴 `azp` が無いとき、`preferred_username` の `service-account-<id>` から復元しない（基盤の `MachinePrincipal.ClientIdOf` の復元は採らない）。名前は人が選べる値であり、門の判定に使わない。

### 決定 2 — 許可集合は構成 `Auth:GrpcOwnerClients`（配列）。既定はボットの機密クライアント。1 つの値は起動時に例外

- 未構成（子要素が 0 個）なら既定 `["ai-stock-trading-owner"]`。構成すると**既定を置き換える**（足し合わせない）。構成の側は前後空白を落とし、空白だけの要素を捨てる。**空になれば誰の所有者トークンも通さない**（fail-closed。s2s は通る）。
- 🔴 **1 つの値（`Auth__GrpcOwnerClients=a,b`・空文字を含む）は `AddAiStockTradingAuth` の中で例外**にする。配列へ束縛されず既定へ静かに戻るのを防ぐ（基盤の `TrustedUserContextRelay.ThrowIfScalar` と同じ形）。全サービスがこの拡張を通るので、gRPC 面を持たないサービスでも誤った構成は起動で止まる。
- 許可集合は登録時に 1 回だけ解決する（構成の再読み込みには追随しない。ほかの `Auth:*` と同じ）。

### 決定 3 — 配備の値はボットと同じ秘密鍵から採る

- helm: 6 面の `extraEnv` に `Auth__GrpcOwnerClients__0` を `ast-secrets` の `discord-owner-auth-client-id`（`optional: true`）から足す。notification の `Notifications__Discord__OwnerAuth__ClientId` と同じ鍵＝単一情報源。values-local で `extraEnv` を丸ごと上書きしている 3 サービス（risk-management・market-monitor・report）にも足した（helm はリストを置き換える）。鍵が無ければ env 自体が無く、コード既定になる。
- compose: 6 面に `Auth__GrpcOwnerClients__0: ${DISCORD_OWNERAUTH_CLIENTID:-ai-stock-trading-owner}`（ボットと同じ変数・同じ既定）。
- realm（`infra/keycloak/realm-export.json`）の `ai-stock-trading-owner` は機密・`standardFlowEnabled:false`・`directAccessGrantsEnabled:false`・サービスアカウントのロールは `trading-owner` だけ。**人がこのクライアントの `azp` でトークンを取る経路は無い。**
- 配線試験（T-10-1726）が、既定と realm の owner クライアントの一致・そのクライアントの性質・helm（values と values-local）と compose の 6 面が notification と同じ出所から採ることを固定する。

### 決定 4 — 試験は判定・構成・6 面の本物の組み立て・配線の 4 層で置く

- T-10-1723: 本物の登録が作るポリシーを `IAuthorizationService` で評価する（否定の試験: azp が BFF・公開クライアント・大小文字・接頭辞・接尾辞・前後空白・空・s2s のクライアント、azp 無し、azp 2 つ、ロール無し、未認証。陽性対照: ボット、s2s。REST の `OwnerOrService` は azp 無しの所有者を通したまま）。
- T-10-1724: 構成（既定・置き換え・空・1 つの値の例外）。
- T-10-1725: 6 面それぞれを本物の `Program.cs` で呼ぶ（否定の試験と陽性対照、同じサービスの REST の面は azp 無しの所有者で 200）。
- T-10-1726: 配線（上の決定 3）。

## 理由

- **新しいポリシーにしたのは、REST の面を変えないためである。** `OwnerOrService` 自体を変えると、BFF が中継する REST（人の利用者の経路）が閉じる。
- **判定を `azp` の生の値の序数一致にしたのは、変種を別のクライアントとして扱うためである。** 前後空白の除去や大小文字の畳み込みは、許可していないクライアントを通す向きにしか働かない。
- **`azp` が 2 つ以上のトークンを拒むのは、どちらを採るかで結果が変わるからである。** Keycloak のトークンは `azp` を 1 つだけ持つ。
- **1 つの値を起動時に止めるのは、既定へ静かに戻ると「狭めたつもりで既定のまま」が起きるからである**（基盤の #1628 の監査の F2 と同じ事故の型）。

## 結果

- 良い影響: 人の利用者のトークンは gRPC 面を通らない（段 5 でボットを移す前に門が閉じた）。s2s の呼び出し元（取引判断・報告書・市場監視・情報収集・費用統制）の振る舞いは変わらない。
- 悪い影響 / トレードオフ:
  - 6 面の helm の `extraEnv` が 1 行ずつ増えた。audit・configuration は `ast-secrets` を初めて参照するので、`reloader.enabled=true` の構成では同 Secret の変更で再起動の対象に入る（既定は `false`）。
  - 本番の realm は基盤（MSP）のレルムの写しであり（realm-export.json の説明）、そちらの owner クライアントの性質は本リポの試験では固定していない。配備では `discord-owner-auth-client-id` がボットの使う client と同じ値であることが前提になる。
- 配備の注意: **人のトークンで gRPC 面を呼んでいる呼び出し元は無い**（BFF は gRPC のクライアントを持たない。gRPC 面の呼び出し元はすべて s2s。ADR-0047 実測 6）。helm の既定・values-local は `grpcPort` を宣言しておらず、gRPC 面はまだ稼働クラスタで開いていない。

## 関連

- Supersedes: なし
- Superseded by: なし
- 変える記述: [IADR-0331](IADR-0331_assumptions-grpc-transport-and-proto-contract-checks.md)・[IADR-0427](IADR-0427_risk-read-grpc-stage2.md)・[IADR-0445](IADR-0445_audit-read-grpc-stage3.md)・[IADR-0446](IADR-0446_report-monitor-cost-read-grpc-stage4.md) の「gRPC 面の認可は REST と同じ `OwnerOrService`」は、本 IADR 以後は `GrpcOwnerOrService` と読む（凍結記録のため本文は書き換えない）。
