---
title: readinessProbe の timeoutSeconds（既定 1 秒）と NpgSql のヘルスチェックの打ち切り（未指定）の食い違いを揃える（#1137）
type: spec
status: accepted
related_ids: [NFR, IADR-0468, IADR-0011, IADR-0013, IADR-0058, IADR-0439]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
---

# 仕様書: readinessProbe の timeoutSeconds と DB 疎通チェックの打ち切り（#1137）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR・無採番。運用の可用性）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: 計画 ADR の決定は変えない
- 関連する実装ADR: **IADR-0468（本作業で新設）**／IADR-0011・IADR-0013（ヘルスチェックのエンドポイント）／IADR-0058（helm の CI ゲート）／IADR-0439（`helm upgrade` でしか入らない）
- 起票: [#1137](https://github.com/endazon/ai-stock-trading/issues/1137)（利用者の監査 2026-10-01・優先度 high）

## 目的・背景

起票の観測（origin/develop 23b73f35 で再確認した）:

- `deploy/helm/ai-stock-trading/templates/deployment.yaml` の readinessProbe は `periodSeconds: 10`・`failureThreshold: 30` だけで `timeoutSeconds` が無い（Kubernetes 既定 1 秒）。11 Worker 共通。
- `AddNpgSql(connStr, tags: ["ready"])` に `timeout` が無い。接続文字列にも `Timeout=` が無い（Npgsql 既定: 接続 15 秒・コマンド 30 秒）。
- 9/30 17:49:34 UTC に `Health check npgsql … Unhealthy completed after 1002ms … The operation was canceled`（1 回・ノードのメモリ 83%）。
  1002ms での取り消しは、kubelet が 1 秒で接続を切り `RequestAborted` がチェックへ伝わった形である。

## 母集合（規則 9・10: 誤りの側の文字列で走査する）

### テンプレート（`git grep -n readinessProbe -- deploy`）

| 当たり | 対象か | 扱い |
| --- | --- | --- |
| `deploy/helm/ai-stock-trading/templates/deployment.yaml`（11 Worker 共通。描画で .NET の Deployment は 11 本: audit / backtest / configuration / cost-control / information-collection / market-monitor / notification / order-execution / report / risk-management / trade-decision） | **対象** | `timeoutSeconds` を values から読む |
| `deploy/helm/ai-stock-trading/templates/opend.yaml`（OpenD・`tcpSocket`） | 対象外 | HTTP のヘルスチェックを走らせない TCP 疎通。DB に触らない |
| `deploy/opend/k8s/opend.yaml`（同上の素の manifest） | 対象外 | 同上 |
| `templates/cronjob.yaml` | 対象外 | probe を持たない（`git grep` で当たらない） |

### ヘルスチェックの登録（`git grep -n "AddNpgSql\|AddAiStockTradingHealthChecks\|MapAiStockTradingHealthChecks" -- backend`）

| サービス | `/health/ready` が走らせるチェック | 扱い |
| --- | --- | --- |
| audit / configuration / cost-control / market-monitor / order-execution / report / risk-management（7） | `npgsql`（`ready` タグ） | **`timeout:` を渡す** |
| backtest / information-collection / notification / trade-decision（4） | 無し（チェック 0 個で即 Healthy） | 変更なし（probe の `timeoutSeconds` は共通テンプレートで同じく効く） |
| `OpendAuthGateway` | ヘルスチェックを持たない（chart の 11 本にも入らない。OpenD の Pod のサイドカー） | 対象外 |
| `HealthCheckExtensions.MapAiStockTradingHealthChecks`（PlatformShim） | `/health/live` は `Predicate = _ => false`（チェック 0 個）／`/health/ready` は `ready` タグ | **liveness は DB に依存しない**ことを確認。変更なし（定数だけ足す） |

### 文書（`git grep -n -E "readinessProbe|failureThreshold|AddNpgSql|health/ready|ヘルスチェック" -- docs '*.md' ':!.ai-context' ':!CHANGELOG.md'`）

| 当たり | 偽になるか | 扱い |
| --- | --- | --- |
| `docs/tech/system-architecture.md:214`（全 Worker が `/health/ready` を公開） | ならない | 据え置く |
| `docs/api/east-west-grpc.md:86`（readiness は HTTP 側のまま） | ならない | 据え置く |
| `docs/how-to/local-run.md:57` | ならない | 据え置く |
| `docs/operations/operations.md`（#10 OpenD の readiness は TCP 疎通のみ） | ならない | 据え置く |
| `docs/tests/FR-15_backtest-tests.md` T-15-53（DB を持たない backtest は起動直後に ready） | ならない（backtest はチェック 0 個のまま） | 据え置く |
| `deploy/helm/ai-stock-trading/values.yaml` 冒頭の `services:` のコメント（`/health/live・/health/ready`） | ならない | 据え置く |

### 規則 10（この変更で新たに誤りになる自分の記述）

- helm.yml の新しい assert は「チェックの打ち切り 3 秒」を数値で持つ。定数を変えると古くなる → IADR-0468 の残余に書き、
  `ReadinessProbeTimeoutConsistencyTests` が定数（3 秒）を固定するので、定数を変えた PR は .NET 側で必ず赤になる（同じ変更で直す合図）。
- `values.yaml` のコメントと `HealthCheckExtensions` の doc コメントは互いの値（3 秒・5 秒）を書く。同上の試験が両方を固定する。
- 導出値: 「11 本」は描画からの実測（`awk` で `ASPNETCORE_URLS` を持つ Deployment を数えた）、「7 本」は `backend/Services/**/Program.cs` の走査の実測。

## 窓の扱い（規則 11）

時間差を扱う是正である（probe の打ち切り・チェックの打ち切り・DB の往復の 3 つの時間の大小）。
**前の端＝kubelet の probe の `timeoutSeconds`**、**後の端＝アプリのチェックの打ち切り**とみなし、3 通りの形を 3 つのプローブで比べた。

プローブ:

- **P1（増える側）**: DB の往復が 1〜3 秒に伸びた（遅いが答えられる。9/30 の事象）。期待: ready のまま。
- **P2（減る側）**: DB が固まった（往復が 15 秒以上・Npgsql の接続待ちが満了するまで）。期待: kubelet の打ち切り前に**明示の失敗（503）**が返り、チェックが取り消されない。
- **P3（間）**: 往復が 3〜5 秒。期待: 上限で切って失敗 1 回に数える（30 回連続で NotReady は従来どおり）。

| 形 | P1（1〜3 秒） | P2（固まる） | P3（3〜5 秒） |
| --- | --- | --- | --- |
| 前の端だけ（probe 5 秒・チェック未指定） | ○ ready | ✕ 5 秒で kubelet が切り、チェックは取り消し（答え無し） | ○ ready（甘い） |
| 後の端だけ（probe 既定 1 秒・チェック 3 秒） | ✕ 1 秒で kubelet が切る（**今回の事象**） | ✕ 1 秒で切る（答え無し） | ✕ |
| **両端（probe 5 秒 ＞ チェック 3 秒）＝採用** | ○ ready | ○ 3 秒で 503（答えが届く） | ○ 3 秒で失敗 1 回（意図どおり） |
| 両端だが逆（チェック ≥ probe） | ○ | ✕ kubelet が先に切る | ✕ |

両端の形は「実効の打ち切り＝min(probe, チェック)＝チェック」で、答えが必ず kubelet の窓の中に収まる。

［2026-10-01 追記 / #1137］独立監査の実測で、上の「必ず」は **Npgsql が取り消しに応じる局面に限る**と判明した（起動・認証のハンドシェイクでの無応答は接続 `Timeout` 15 秒まで止まらない）。回帰ではなく、残余として IADR-0468「結果」に記録した。
最後の行（逆の大小）は変異 M2・M3 が赤になることで塞ぐ。

## 設計（IADR-0468）

1. PlatformShim `HealthCheckExtensions` に定数 `NpgSqlReadinessTimeout = TimeSpan.FromSeconds(3)` を足す。
2. 7 サービスの `Program.cs` が `AddNpgSql(connStr, tags: ["ready"], timeout: HealthCheckExtensions.NpgSqlReadinessTimeout)` を呼ぶ。
3. `values.yaml` にトップレベル `probes.readiness`（`initialDelaySeconds: 10` / `periodSeconds: 10` / `timeoutSeconds: 5` / `failureThreshold: 30`）を足し、テンプレートはそれを読む。
   **描画差分は 11 Deployment に `timeoutSeconds: 5` が 1 行ずつ増えるだけ**（既定・values-local とも `diff` で実測）。
4. liveness・回数・間隔・接続文字列は変えない（挙動は打ち切りだけ変える）。

## 対象範囲

- 変更: `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Extensions/HealthCheckExtensions.cs`、7 サービスの `Program.cs`、
  `deploy/helm/ai-stock-trading/{values.yaml,templates/deployment.yaml}`、`.github/workflows/helm.yml`（assert 1 段）
- 試験: 7 サービスの `Tests/DbReadinessHealthCheckTimeoutTests.cs`（新規）、`PlatformShim.Tests/ReadinessProbeTimeoutConsistencyTests.cs`（新規）
- 記録: IADR-0468・索引行・本書
- 対象外: OpenD の probe・liveness・`failureThreshold` / `periodSeconds`・startupProbe の新設・接続文字列の `Timeout=`

## 受け入れ基準

- [x] 7 サービスの `npgsql` 登録が `ready` タグ・打ち切り 3 秒（本番の組み立てから読む）
- [x] `values.yaml` の `probes.readiness.timeoutSeconds`（5）がチェックの打ち切り（3 秒）より厳密に長い
- [x] 描画で 11 本すべての readinessProbe に `timeoutSeconds: 5` が出る（既定・values-local）。それ以外の描画は 1 バイトも変わらない
- [x] `AddNpgSql` を呼ぶ全 `Program.cs` が共通の打ち切りを渡す（走査で母集合を取る）
- [x] liveness は変えない

## テスト方針

- テスト ID は振らない。安全・統制の中核 FR（網羅裁定 #211）ではなく NFR の運用であり、テスト仕様書は任意（`docs/README.md`）。
  NFR・運用の試験を登録するテスト仕様書は無い（`docs/tests/` は FR-10 / 12 / 15 / 19 / 20 のみ）ため、リスク統制の帯（T-10-…）には登録しない。
  起点は試験のコメント（`NFR, IADR-0468, #1137`）に残す。
- (a) 組み立て: `DbReadinessHealthCheckTimeoutTests`（7 本）が `IOptions<HealthCheckServiceOptions>` の `Registrations` から `npgsql` を引く。
- (b) chart: helm.yml の `Lint and render chart` ジョブに assert を 1 段足す（既存の描画 assert と同じ awk の形）。
  .NET 側は `ReadinessProbeTimeoutConsistencyTests` が values.yaml とテンプレートを読んで大小を固定する。

## 変異（自己変異）の実測

| 変異 | 結果 |
| --- | --- |
| M1: configuration の `Program.cs` から `timeout:` を外す | **赤**: `ConfigurationService.Tests` の `DbReadinessHealthCheckTimeoutTests`（`Expected 3s, but found -1ms.`＝未指定は `InfiniteTimeSpan`）・`PlatformShim.Tests` の `AddNpgSqlを呼ぶ全サービスが共通の打ち切りを渡している` |
| M2: `values.yaml` の `timeoutSeconds` を 3（＝チェック）にする | **赤**: `readinessProbeのtimeoutSecondsはDB疎通チェックの打ち切りより厳密に長い`・helm assert（11 本とも `3` を列挙して exit 1） |
| M3: 定数を 5 秒（＝probe）にする | **赤**: `DB疎通チェックの打ち切りは3秒`・`…厳密に長い`・`AuditService.Tests` の `DbReadinessHealthCheckTimeoutTests` |
| M4: テンプレートから `timeoutSeconds:` の行を消す | **赤**: helm assert（11 本とも `none` を列挙して exit 1） |

いずれも戻して緑を確認した。

## 計画書との差異

なし（計画の FR / ADR の決定に触れない運用の値）。

## 採番

IADR-0468（並行レーンが 0469〜0471 を確保している）。テスト ID は振らない（上記）。

## 検証

- `dotnet build backend/backend.slnx -warnaserror`・影響する 8 試験プロジェクト・`dotnet format backend/backend.slnx --verify-no-changes`
- `helm lint --strict`・`helm template`（既定・values-local）と描画差分・helm.yml の新 assert の手元実行（helm v4.2.1）
- `node scripts/scripts.test.js` と文書系の検査器一式

## 残余リスク

- 3 秒・5 秒は 1 回の事象からの見積もり。負荷時の DB の往復の分布は測っていない。
- 稼働へは `helm upgrade` が要る（`kubectl rollout restart` では入らない。IADR-0439）。アプリだけ先に入っても悪化はしない（probe 1 秒が先に切るのは従来どおり）。
- liveness の既定 1 秒はチェック 0 個の応答だけを待つ。ノード負荷でそれすら超えるなら別件。
