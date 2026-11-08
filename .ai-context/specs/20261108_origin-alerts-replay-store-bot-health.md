---
title: 作業仕様書 — 発行元の確かめの記録の警報・再送の記録の永続化・Discord ボットの接続状態の計器と警報
type: spec
status: in-progress
related_ids: [NFR, FR-09, FR-10, FR-14, IADR-0645, IADR-0627, IADR-0633, IADR-0637, IADR-0638, IADR-0062, IADR-0374]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs: []
---

# 作業仕様書 — 発行元の確かめの警報・再送の記録の永続化・ボットの接続状態（NFR）

> 着手 2026-11-08（develop `20249ea15`）。判断の記録は IADR-0645。クラウド稼働前の硬化（利用者の裁定 2026-11-07「medium も low も全部直してから稼働する」）。

## 範囲

1. **記録だけの受け手・段 1 の受け手の確かめの失敗を警報にする。** `ast_messaging_origin_failures_total{outcome="admitted"}` を引く警報を足す
   （既存の `AstMessageOriginRejected` は `rejected` だけを引く）。
2. **旧の共有クライアント（移行の段）の通過を計器にして警報にする。** これまではログの Warning だけだった。
3. **発行元の確かめの再送の記録（処理し終えたメッセージ ID）を、DB を持つ受け手では DB に残す**（再起動で消えない。窓は従来どおり 24 時間）。
   置き場の無い受け手（DB を持たない受け手・置き場を選ばない構成）は、起動より前に発行されたメッセージを厳しめに扱う（下の AC3-3）。
4. **Discord ボットの接続状態を計器にして警報にする。** 設定の欠けで接続しないとき・切断が続くとき。段 2 では設定の欠けで readiness を落とす。

範囲外: 表の外の型（発行元の印が付かない型）の確かめ（IADR-0638 決定 2 のまま）。警報の配送（基盤の Prometheus・Alertmanager）。

## 母集合（traceability.repo.md 規則 9・10）

- 計器の追加は `BusinessMetricNames` が単一の情報源で、`scripts/check-observability-assets.js` が警報・ダッシュボードとの双方向の一致を検査する
  （足した計器は警報から必ず引く）。系列の表は `deploy/observability/README.md`。
- 再送の記録を使う受け手は `MessageOriginCatalog.VerifiedBy` の空でないサービス: risk-management・market-monitor・order-execution・cost-control・audit
  （DB あり）、trade-decision・notification（DB なし）。chart で DB を持つのは `services.<名前>.db` のあるもの（`values.yaml`）。
- 旧の共有クライアントを通す箇所は `ServiceCallerHandler`（`Auth:ServiceToken:LegacyClients` の一致）だけ（`grep -rn LegacyClients backend`）。
  宛先の確かめ（`AudienceAccepted`）は通過の判定であって通過そのものではないので、計上は認可の側の 1 箇所で行う。
- ボットの Gateway の選択は `DiscordBotGatewayFactory` だけ。接続は `DiscordNetBotGateway`。

## 受け入れ基準

| ID | 基準 | 試験 |
| --- | --- | --- |
| AC1-1 | 警報 `AstMessageOriginUnverifiedAdmitted` が `outcome="admitted"` を引き、最初の 1 件で鳴る形（`AstMessageOriginRejected` と同じ形）である | `check-observability-assets.js`・`scripts.repo.test.js` |
| AC2-1 | 旧の共有クライアントのトークンを通した回ごとに `ast.auth.legacy_service_client_uses`（タグ `endpoint`）が 1 増える。自分のクライアントのトークンでは増えない（陰性対照） | `ServiceCallerPolicyTests` |
| AC2-2 | 警報 `AstLegacyServiceClientUsed` が最初の 1 件で鳴る形である | 同 AC1-1 |
| AC3-1 | DB の置き場では、処理し終えた ID を再起動（検証器の作り直し）の後も再送として拒む（段 2）。置き場が無ければ通ってしまう（陰性対照） | `MessageOriginReplayStoreTests`・Integration `MessageOriginReplayStoreE2ETests`（実 PostgreSQL） |
| AC3-2 | 置き場の読みに失敗したら、例外で処理を止める（拒否として捨てない。Wolverine の再試行・エラーキューへ）。書きに失敗したら計器 `ast.messaging.origin_replay_store_failures` に残し、プロセスの中の記録で続ける | `MessageOriginReplayStoreTests` |
| AC3-3 | 置き場が無い受け手は、起動より前に、猶予（既定 5 分）を超えて発行されたメッセージを結果 `unrecorded` とする（段 2 は処理しない・段 1 は記録して通す）。起動の後に発行されたもの・猶予の中のものは従来どおり | `MessageOriginReplayStoreTests` |
| AC3-4 | 構成 `MessageOrigin:ReplayStore=Database` で接続文字列が無ければ起動で止まる。値が `Memory`・`Database` 以外なら起動で止まる。置き場は DI の Singleton（ホストが破棄） | `MessageOriginWiringTests` |
| AC3-6 | 表は起動時の移行の経路（`MigrateAsync` の直後の `EnsureMessageOriginReplayStoreAsync`）で作る。作る権限が無ければ明示の例外（SQLSTATE）で起動を止める。作っていない置き場の読みは例外。全サービスの Program.cs が移行の後に呼ぶ | Integration `MessageOriginReplayStoreE2ETests`（権限の無い利用者）・`MessageOriginWiringTests`・Architecture `MessageOriginReplayStoreStartupTests` |
| AC3-5 | chart は `global.messageOrigin.replayStore` を、DB を持つ受け手にだけ `MessageOrigin__ReplayStore` として描く。DB の無い受け手には描かない。`values-cloud` は `Database` | `helm template`（`helm.yml` の assert） |
| AC4-1 | ボットが無効・設定の欠け・切断・接続のどれかを `ast.notification.discord_bot_status`（タグ `state`。今の状態だけ 1）に出す | `DiscordBotGatewayFactoryTests` |
| AC4-2 | 構成 `Notifications:Discord:Bot:MissingSettingsMode=Enforce` で、有効なのに設定が欠けていれば readiness が Unhealthy。`Report`（既定）・無効では Healthy（陰性対照） | `DiscordBotReadinessTests` |
| AC4-3 | 警報 `AstDiscordBotUnavailable` が設定の欠け・切断を 10 分で鳴らす | 同 AC1-1 |

## 段（稼働中の通信を止めうる変更）

- 再送の記録の DB: コードの既定は `Memory`。chart の `values-cloud` だけ `Database`。戻すときは `global.messageOrigin.replayStore: Memory`。
- ボットの readiness: コードの既定は `Report`（計器と警報だけ）。`values-cloud` も `Report` のまま。計器が `connected` を示すことを確かめてから
  利用者が `Enforce` へ上げる（手順書 `docs/operations/cloud-single-node-migration-runbook.md`）。

## 検証

- `dotnet build backend/backend.slnx`・関係する試験・`dotnet format --verify-no-changes`。
- `helm lint`・`helm template`（values / values-cloud）。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`node scripts/check-observability-assets.js`・文書の検査。
