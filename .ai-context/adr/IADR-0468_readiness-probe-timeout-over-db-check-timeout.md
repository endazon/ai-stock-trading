---
title: IADR-0468 readinessProbe の timeoutSeconds（5 秒）を明示し、DB 疎通チェックの打ち切り（3 秒）をそれより厳密に短く固定する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0011, IADR-0013, IADR-0058, IADR-0439, IADR-0049]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
---

# IADR-0468: readinessProbe の timeoutSeconds と DB 疎通チェックの打ち切りの大小を揃える（#1137）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-01
- 決定者: Claude Code（実装）。起票は利用者の監査（2026-10-01）

## 起点・関連

- 関連する計画書 ID: NFR（無採番。運用の可用性。計画の FR / ADR の決定は変えない）
- 起票: [#1137](https://github.com/endazon/ai-stock-trading/issues/1137)
- 作業仕様書: [`.ai-context/specs/20261001_1137_readiness-timeout.md`](../specs/20261001_1137_readiness-timeout.md)（母集合・規則 11 の表・変異の実測）
- 前提:
  - [IADR-0011](IADR-0011_foundation-min-port.md) / [IADR-0013](IADR-0013_platform-foundation-testsupport-shim.md): `/health/live`（チェック 0 個）と `/health/ready`（`ready` タグのチェックだけ）は PlatformShim の `MapAiStockTradingHealthChecks` が張る。
  - [IADR-0058](IADR-0058_helm-chart-ci-gate.md): chart の描画は helm.yml（`Lint and render chart`）で検査する。
  - [IADR-0439](IADR-0439_helm-release-drift-read-only-check.md): values・テンプレートの変更は `helm upgrade` でしか稼働中のリリースへ入らない。

## 背景（origin/develop 23b73f35 で確認）

- 11 Worker は共通テンプレート `deploy/helm/ai-stock-trading/templates/deployment.yaml` の readinessProbe（`/health/ready`・`initialDelaySeconds: 10`・`periodSeconds: 10`・`failureThreshold: 30`）を使い、**`timeoutSeconds` を持たなかった**（Kubernetes の既定 1 秒）。
- DB を持つ 7 サービス（audit / configuration / cost-control / market-monitor / order-execution / report / risk-management）は `AddNpgSql(connStr, tags: ["ready"])` で **`timeout` を渡していなかった**。登録の `Timeout` は `Timeout.InfiniteTimeSpan`（実測: 変異で `-1ms`）で、打ち切りは Npgsql の既定（接続 15 秒・コマンド 30 秒）に委ねられていた。
- 稼働 PoC で 2026-09-30 17:49:34 UTC に `Health check npgsql … Unhealthy completed after 1002ms … The operation was canceled` が 1 回出た（ノードのメモリ 83%）。**1002ms で取り消された**のは、kubelet が 1 秒で接続を切り、`RequestAborted` がチェックへ伝わったためである。DB は遅かっただけで、答えが出る前に kubelet が打ち切った。
- liveness（`/health/live`）は `Predicate = _ => false` でチェックを 1 つも走らせない。**DB に依存しない**ため本件の対象外である。

## 決定

1. **DB 疎通チェックの打ち切りを 3 秒に固定する。** 定数 `HealthCheckExtensions.NpgSqlReadinessTimeout = TimeSpan.FromSeconds(3)`（PlatformShim）を置き、7 サービスの `AddNpgSql(..., timeout: HealthCheckExtensions.NpgSqlReadinessTimeout)` が渡す。超えたら `HealthCheckService` が Unhealthy（"A timeout occurred while running check."）を返す。
2. **readinessProbe の `timeoutSeconds` を 5 秒で明示する。** 値は `values.yaml` の `probes.readiness`（`initialDelaySeconds: 10` / `periodSeconds: 10` / `timeoutSeconds: 5` / `failureThreshold: 30`）を単一情報源とし、テンプレートはそれを読む。**描画差分は 11 Deployment に `timeoutSeconds: 5` が 1 行ずつ増えるだけ**（既定・values-local とも実測）。
3. **大小の不変条件: probe の `timeoutSeconds` ＞ チェックの打ち切り（厳密に）。** チェックが先に打ち切って 503 を返すので、**チェックが取り消しに応じる局面では** kubelet の打ち切りより前に答えが届く（取り消されない）。応じない局面は「結果」の残余に書く（独立監査の実測）。逆か等しいと、kubelet が先に切って「結果の無い失敗」に戻る。
4. **回数と間隔は変えない。** `failureThreshold: 30` × `periodSeconds: 10`（約 300 秒）は起動時の migration を待つための値で、本件の射程外。この値の下では、遅い往復が 1 回あっても失敗 1 回に数えるだけで NotReady にはならない（連続 30 回で NotReady）。
5. **liveness は変えない**（チェック 0 個で DB に依存しない。`timeoutSeconds` は既定 1 秒のまま）。
6. **固定する検査**:
   - 7 サービスのテスト `DbReadinessHealthCheckTimeoutTests`: 本番の組み立て（`Program.cs`）から `HealthCheckServiceOptions.Registrations` を読み、`npgsql` の登録が `ready` タグ・打ち切り 3 秒であること。
   - `PlatformShim.Tests` の `ReadinessProbeTimeoutConsistencyTests`: 定数が 3 秒・`values.yaml` の `probes.readiness.timeoutSeconds` が定数より厳密に長く 5・回数と間隔が従来の値・テンプレートが values を読む・**`backend/Services` の全 `Program.cs` を走査し `AddNpgSql` を呼ぶ 7 本すべてが共通の打ち切りを渡す**（新しいサービスが打ち切り無しで足されたら落ちる）。
   - helm.yml の `Assert readinessProbe timeoutSeconds exceeds the DB check timeout (#1137)`: 既定と values-local の描画で、.NET サービスの Deployment が 11 本・全部に `timeoutSeconds` があり 3 を超えること。

### 数値の根拠

| 値 | 根拠 |
| --- | --- |
| チェック 3 秒 | 実測の失敗は 1 秒を超えた往復 1 回（1 夜に 1 回）。平常の `SELECT 1` は数 ms で、3 秒は平常の 1000 倍近い余裕。これを超える往復は「遅い」ではなく「答えられない」と扱ってよい。上限を置くことで、DB が固まったときにリクエストが 15 秒（Npgsql の接続既定）残り続けない |
| probe 5 秒 | チェック 3 秒＋応答の書き出し・スレッドプールの遅れ・ノード負荷時のスケジューリングの余裕 2 秒。起票の提案値と同じ |
| 間隔 10 秒・閾値 30 | 据え置き（決定 4）。間隔 10 秒 ＞ probe 5 秒なので、probe の要求が重ならない |

## 採らなかった案

- **probe だけを 5 秒にする（チェックは未指定のまま）**: 遅い往復は通るが、DB が固まると kubelet が 5 秒で切ってチェックは取り消される（答えが無い）。Npgsql の 15 秒の接続待ちが要求ごとに走りかけて捨てられる（規則 11 の表の「後の端だけ」）。
- **チェックだけを 3 秒にする（probe は既定 1 秒）**: 今回の事象そのもの。1〜3 秒の往復は kubelet が先に切る（規則 11 の表の「前の端だけ」）。
- **接続文字列に `Timeout=` / `Command Timeout=` を足す**: 業務の DB アクセス（EF Core）の打ち切りまで変わる。射程外。
- **probe の値を values.yaml に置かずテンプレートへ直書き**: 大小の不変条件を検査するとき、値の在りかが 2 つになる。values を単一情報源にした。
- **failureThreshold を下げて NotReady を早める／startupProbe を足す**: 挙動の変更で射程外（決定 4）。

## 結果

- 1〜3 秒の遅い往復は ready のまま通る。3 秒を超えると 503 が 3 秒強で返り、失敗 1 回に数える（30 回連続で NotReady は従来どおり）。
- 🔴 **稼働中のリリースへ入れるには `helm upgrade` が要る**（`kubectl rollout restart` では values・テンプレートの変更は入らない。IADR-0439）。アプリ側の打ち切り（3 秒）はイメージの入れ替えで入る。**片方だけ入った状態**（アプリ 3 秒・probe 1 秒）は、従来どおり 1 秒で kubelet が切るだけで、悪化はしない。
- 残余:
  - 3 秒・5 秒は 1 回の事象からの見積もりで、負荷時の DB の往復の分布は測っていない（計器の `ast_*` にヘルスチェックの所要時間は無い）。
  - liveness の `timeoutSeconds`（既定 1 秒）はチェック 0 個の応答だけを待つ。ノード負荷でそれすら 1 秒を超えるなら別件（本件では観測されていない）。
  - **打ち切り 3 秒が効かない局面がある**（独立監査の実測。AspNetCore.HealthChecks.NpgSql 9.0.0 / Npgsql 10.0.3）。`HealthCheckRegistration.Timeout` はトークンを取り消すだけなので、Npgsql がトークンを見ない局面ではチェックは止まらない。
    - 取り消しに応じる局面（遅いクエリ・SYN が返らない宛先）: 約 3.0 秒で Unhealthy。#1137 の実例（要求の中断による取り消し）はこちら。
    - 応じない局面（TCP は受理したまま起動・認証のハンドシェイクで無応答 / 初回 Open の型読み込みで停止）: Npgsql の接続 `Timeout`（既定 15 秒）・内部コマンドの打ち切り（30 秒）まで走る。kubelet は 5 秒で切るので、この局面に限り「結果の無い失敗」が残る。**回帰ではない**（是正前も同じ）。readiness は失敗 1 回に数えるので NotReady への遷移は従来どおり。
    - 塞ぐなら、ヘルスチェック専用の接続文字列に `Timeout=3`（必要なら `Command Timeout=3`）を付ける（実測で無応答の局面も約 3 秒で終わる。`AddNpgSql` は自前のデータソースを作るので EF Core には波及しない）。上の「採らなかった案」の理由（業務の DB アクセスまで変わる）は、業務の接続文字列へ足す形にだけ当てはまる。本件では 7 サービスの接続文字列の組み立てを変えることになるので射程外とし、ハンドシェイクでの停止が実際に観測されたら別件で起票する。
  - 走査テストは `Program.cs` の `.AddNpgSql(` という文字列だけを見る。拡張メソッド経由の登録や、`AddNpgSql` 以外の `ready` チェックを打ち切り無しで足しても落ちない（独立監査の変異 M8 が生存）。
  - helm.yml の assert はチェックの打ち切り 3 秒を数値で持つ。定数を変えるときは同じ変更で直す（`ReadinessProbeTimeoutConsistencyTests` が定数と values の大小を固定するので、定数だけを 5 秒以上にすれば .NET 側が赤になる）。
