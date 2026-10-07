---
title: 統合テストは Docker 不達のとき依存ごとに訊いて理由つきで skip し、integration.yml は「全 skip で緑」を同じ PR で検知する（#1200）
type: spec
status: accepted
related_ids: [NFR, IADR-0497, IADR-0049, IADR-0050, IADR-0208, IADR-0277]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0090_integration-test-gate-asks-for-services.md (MSP/ADR-0090 決定 1・2・3。両実装リポ共通の正本)
  - planning:projects/ai-stock-trading/10_feedback/20260909_integration-test-gate-parity.md (planning#575 の完了記録。「実装側の残作業（AST）」1〜3)
---

# 統合テストの門と「全 skip で緑」の検知（#1200）

## 背景

- 第 4 回全体監査（2026-10-07）指摘 B-3。planning#575 の裁定（2026-09-09。正本は MSP/ADR-0090）から 28 日、AST 側が未着地。
- 裁定の要旨: ①門は依存ごとに「得られるか」を訊き、得られなければ skip（`E2EInfrastructure` の隣へ。外部供給の口は既にある）
  ②skip の理由に「どうすれば走るか」を書く ③CI で「全 skip が緑」にならない担保を**同じ PR で**置く（手段は実装の裁量）
  ④着地したら IADR に残す。

## 実測（origin/develop 9027f413）

- 本セッション（`/var/run/docker.sock` は在るがデーモンに繋がらない）で `dotnet test backend/Tests/AiStockTrading.IntegrationTests --filter Category=Integration`
  → **14 件すべて fail**（`DockerUnavailableException`）。
- 🔴 例外は `InitializeAsync` ではなく**コンストラクタ**（フィールド初期化子の `new PostgreSqlBuilder(...).Build()`）で出ている
  （スタック `..ctor() in TradeExecutionPipelineE2ETests.cs:line 38`）。門は**コンテナを組み立てる前**に通す必要がある。
- 🔴 ソケットファイルの有無は答えにならない（在るのに繋がらない）。MSP の `DockerRequired.IsAvailable()` の形（`File.Exists`）を写すと、
  この環境では「使える」と答えて fail のまま残る。
- Docker を要る試験 14 件（7 クラス）の依存:

| クラス | 件数 | 依存 |
| --- | ---: | --- |
| `ApprovedOrderTerminalConcurrencyE2ETests` | 3 | PostgreSQL |
| `LedgerStopLineWideningConcurrencyE2ETests` | 3 | PostgreSQL |
| `PositionDriftStateConcurrencyE2ETests` | 3 | PostgreSQL |
| `OrderExecutionPipelineE2ETests` | 1 | PostgreSQL・RabbitMQ |
| `TradeExecutionPipelineE2ETests` | 2 | PostgreSQL・RabbitMQ |
| `KeycloakOwnerOnlyEndpointE2ETests` | 1 | PostgreSQL・RabbitMQ・Keycloak |
| `ServiceTokenSyncQueryE2ETests` | 1 | PostgreSQL・RabbitMQ・Keycloak |

- 同アセンブリの他の試験（`E2EInfrastructureTests`・`E2EInfrastructureDisposeTests`・`MoomooAdapterFakeOpenDIntegrationTests`）は Docker を要らず、
  `Category=Integration` も無い（既定 CI で走る）。
- `integration.yml` は `dotnet test backend/backend.slnx` を**フィルタなし**で 1 回走らせるだけで、TRX も skip の検査も無い。
- 既存の TRX 読み取り（`scripts/summarize-test-failures.js`。`parseTrx` / `listTrxFiles` / `decodeXmlEntities` を export 済み）がある。

## 計画の確認

- 起点 ID: 無採番 NFR（テスト基盤）。MSP/ADR-0090 は「要求の不足」として AST 側の計画 ADR を立てないと裁定済み（AST の計画 ID は引かない）。
- 決定 1・2 は MSP/IADR-0414 の形を写す。決定 3 は手段を実装に委ねる（実走件数の下限・疎通検査・skip 件数の上限のいずれか）。
- MSP/ADR-0090 は案 B（CI では fail・ローカルでは skip）を退けた。🔴 よって MSP の `DockerRequired.IsAvailable()` にある
  「`CI=true` なら無条件に真」は**写さない**（同じコードが環境で違う結果を出す形そのもの）。CI での見逃しは決定 3 の担保で止める。

## 母集合（規則 9・10。誤りの側の文字列で走査）

`git grep -n -E "DockerUnavailable|Docker 必須|Docker が無い|Docker 不達|Docker デーモン|E2E_POSTGRES_CONNECTION|Docker を起動|Docker API が無い"`
と `git grep -n -E "integration\.yml|Category=Integration"`（`.ai-context/specs` を除く）:

| 箇所 | 扱い |
| --- | --- |
| 統合テスト 5 ファイルの「（Docker 必須）」 | 「依存を得られなければ skip（門は `RequiredServices`）」へ直す |
| 統合テスト 6 ファイルの「外部インフラ注入時（Docker API が無い環境…）」 | 門のコンストラクタへ移すので注記ごと書き直す |
| `scripts/e2e-local-infra.sh` 冒頭（containerd では "Failed to connect to Docker endpoint" となる） | 「門が skip し、理由に本スクリプトを挙げる」へ直す |
| `docs/tests/README.md` §実基盤結合の行（「`integration.yml`（夜間/手動）で実走」） | 既に古い（develop への push でも走る・IADR-0208）。依存を得られなければ skip・`integration.yml` は skip 0 件を検査、へ直す。trace ブロックへ IADR-0497・#1200 を足す |
| `.ai-context/adr/IADR-0049`（containerd では実走できない → 外部注入） | 日付つき追記（本文は残す） |
| `scripts/README.md` の本リポ固有の表 | 新しい検査器の行を足す |
| `docs/blocked-tasks.md` A-5（`e2e-local-infra.sh` の代替検証。Docker デーモン停止時の実測経路） | 別主題（DB マイグレーションの代替検証）。除外 |
| `docs/tests/FR-10_risk-controls-tests.md` T-10-590 | 試験の主張の記述であり門に触れない。除外 |
| `docs/operations/wolverine-queue-cleanup-runbook.md`・`FR-12`/`FR-20` の試験仕様の `Category=Integration` | 分類の参照のみ。除外 |
| `.github/workflows/integration.yml` の「fail-closed の門（0 件実行で落とす）も要らなくなった」 | フィルタ由来の 0 件の話であり、今回の門（skip 由来）とは別。両者を取り違えないよう注記を足す |

導出値（規則 10）: Docker を要る試験 14 件（3+3+3+1+2+1+1）。検査器の既定の対象アセンブリは `AiStockTrading.IntegrationTests` 1 つ。

## 設計

### 1. 門（`backend/Tests/AiStockTrading.IntegrationTests/RequiredServices.cs`。`E2EInfrastructure.cs` の隣）

- `RequiredServices.Postgres` / `RabbitMq` / `Keycloak`（`ExternallySuppliable`: 名前・環境変数・外部の値・値の例）。外部の値は
  `E2EInfrastructure` の既存プロパティを読む（口は新設しない）。
- `SkipUnlessObtainable(params ExternallySuppliable[] needs)`: 依存ごとに「外部供給 **または** コンテナ実行環境に届く」。
  1 つでも得られなければ `Assert.SkipWhen` で**真の Skipped**。理由は不足している依存名・環境変数・値の例と
  `scripts/e2e-local-infra.sh`（3 つをまとめて起こして env を出す）を並べる。
- 判定の純粋部 `SkipReason(needs, containerRuntimeReachable)` を分け、Docker 無しで既定 CI の単体試験から確かめる。
- コンテナ実行環境の判定（`ContainerRuntime.IsReachable()`）は **Testcontainers 自身の判定**
  （`TestcontainersSettings.OS.DockerEndpointAuthConfig` が null でないこと。`DOCKER_HOST` 等の設定も同ライブラリが見る）を 1 回だけ評価して持つ。
  例外は「届かない」とする。ソケットファイルの有無は見ない（実測のとおり答えにならない）。🔴 `CI=true` で真にしない。
- 外部供給がすべて揃っている依存だけの試験では、Docker への問い合わせ自体をしない。
- 試験クラスはコンストラクタの先頭で門を通し、その後にコンテナを組み立てる（フィールド初期化子の `Build()` をコンストラクタへ移す）。
  🔴 xUnit v3 はコンストラクタで投げた skip 例外を Skipped として扱う（本作業で実測して固定する）。
- 片側だけの外部注入（`E2E_POSTGRES_CONNECTION` だけ等）は従来どおり `UseExternal` が fail fast（誤設定であり、skip に倒さない）。
- Docker に届くのにコンテナが起動できない場合は従来どおり fail（握り潰さない）。

### 2. 担保（`scripts/check-integration-skips.js` ＋ `integration.yml`）

- 形: **対象アセンブリの skip 件数の上限 0 ＋ 実走件数の下限 1 ＋ 対象アセンブリの TRX が在ること**。
  - 上限 0: 門は Docker が無いときだけ skip するので、Docker のある CI で 1 件でも skip されたら「依存が揃っていない実行」である。
    件数の下限（例「14 件以上」）は試験の増減のたびに写し直す導出値になり腐る（規則 10）ので置かない。
  - 下限 1 と TRX の存在: アセンブリが走らなかった・ビルドから落ちた形を「skip 0 件」と読まない（0 件検査で緑にしない）。
  - 壊れた TRX は読み飛ばさず赤（不明を 0 と読まない。`summarize-test-failures.js` と同じ規則）。
- 発火時の出力: `::error::` で「対象アセンブリ・skip 件数・上限・実走件数」を 1 行、続けて skip された試験の名前と理由を列挙し、
  `$GITHUB_STEP_SUMMARY` にも表で書く。
- `integration.yml`: テストの step に `id: tests`・`--logger trx --results-directory`。検査は
  `if: ${{ !cancelled() && steps.tests.outcome != 'skipped' }}`（テストが赤でも評価する。force_failure のときだけ走らない）。
  検査器の自己試験も同じ条件で先に走らせる。
- 自己試験（`--self-test`）は合成 TRX で発火・非発火の両側を固定し、`scripts.repo.test.js` から毎回走らせ、配線も固定する。

## 受け入れ基準

1. Docker 不達で統合テストを走らせると、14 件は理由つきで Skipped、fail 0。
2. `integration.yml` に skip 0 件・実走 1 件以上・TRX 在りの検査が入る。
3. 検査が発火したとき、何件 skip されたか・上限・試験名と理由が CI ログで読める。
4. IADR-0497 に門の形と担保の形を残し、planning#575 の完了記録を引く。索引へ行を足す。

## 試験

- 単体（`RequiredServicesTests`。既定 CI）: 外部供給ありなら Docker 無しでも得られる／両方無ければ理由を返し名前・変数・例・スクリプトを含む／
  得られる依存は理由に出ない／すべて外部供給なら Docker に問い合わせない／Docker に届けば理由なし。
- 実走: 本環境で 14 件 Skipped・0 Failed。
- 検査器: 本環境の実 TRX で発火（exit 1・読める出力）／合成の「全件実走」TRX で通過（exit 0）。自己試験。

## 残余

- 担保が CI で実際に発火したかは、発火した時点で planning#575 / MSP MSP/ADR-0090 フォローアップ 4 に従い報告する（本 PR の時点では未発火）。
- 計画への完了の環流（planning 側 `10_feedback/` の追記）は計画リポの作業であり本 PR の外。
