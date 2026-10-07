---
title: IADR-0497 統合テストの門は依存ごとに「得られるか」を訊いて理由つきで skip し、integration.yml は skip 件数の上限 0 で「全 skip で緑」を止める
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0049, IADR-0050, IADR-0208, IADR-0277]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0090_integration-test-gate-asks-for-services.md (決定 1・2・3。両実装リポ共通の正本)
  - planning:projects/ai-stock-trading/10_feedback/20260909_integration-test-gate-parity.md (planning#575 の完了記録。「実装側の残作業（AST）」1〜3)
related_specs:
  - ../specs/20261007_1200_integration-skip-gate.md
---

# IADR-0497: 統合テストの門と「全 skip で緑」の担保（#1200）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-07
- 決定者: Claude Code（実装）。起点 [#1200](https://github.com/endazon/ai-stock-trading/issues/1200)（第 4 回全体監査 B-3）

## 起点・関連

- 関連する計画書 ID: 無採番 NFR（テスト基盤）。🔴 **AST 側の計画 ADR は無い** —— 正本は MSP/ADR-0090 1 本（planning#575 の利用者裁定 2026-09-09。
  同じ決定を 2 つの ADR に分けない）。AST はその完了記録 `projects/ai-stock-trading/10_feedback/20260909_integration-test-gate-parity.md` から引く。
- 写した実装: MSP/IADR-0414（`RequiredServices.SkipUnlessObtainable`）。
- 作業仕様書: [`20261007_1200_integration-skip-gate`](../specs/20261007_1200_integration-skip-gate.md)（実測・母集合・試験）
- 改める実装判断: [IADR-0049](./IADR-0049_integration-e2e-foundation.md)（Testcontainers 基盤。Docker 不達時の挙動だけを fail → skip へ。追記済み）

## コンテキストと課題

- Docker に届かない環境で `AiStockTrading.IntegrationTests` を走らせると、Docker を要る **14 件すべてが `DockerUnavailableException` で fail** した
  （本作業の実測。監査の実測と一致）。MSP は同じ環境で skip・fail 0。
- 外部供給の口（`E2EInfrastructure` の `E2E_POSTGRES_CONNECTION` / `E2E_RABBITMQ_CONNECTION` / `E2E_KEYCLOAK_BASEURL`）は既に在った。欠けていたのは門だけ。
- 🔴 例外は `InitializeAsync` ではなく**コンストラクタ**（フィールド初期化子の `Build()`）で出ていた。門はコンテナを組み立てる前に通す必要がある。
- 🔴 `/var/run/docker.sock` は**在るのにデーモンに繋がらない**環境だった。ソケットファイルの有無で判定する MSP の `DockerRequired.IsAvailable()` を写すと
  「使える」と答え、fail のまま残る。
- MSP/ADR-0090 決定 3: skip へ倒すと、fail が偶然担っていた「CI で依存が揃わない実行を止める」守りが消える。**同じ PR で担保を置く**（手段は実装の裁量）。

## 検討した選択肢

| 論点 | 案 | 評価 |
| --- | --- | --- |
| Docker に届くかの判定 | (a) ソケットファイルの有無（MSP の形） | 却下。本環境で誤答（在るが繋がらない） |
| | (b) **Testcontainers 自身の判定**（`TestcontainersSettings.OS.DockerEndpointAuthConfig` が null でない） | **採用**。`Build()` が投げる条件と同じ問い。`DOCKER_HOST`・設定ファイルも同ライブラリが見る |
| | (c) 自前で Docker API へ ping | 却下。Testcontainers と答えが割れ得る |
| CI での近道 | MSP の「`CI=true` なら Docker あり」 | **写さない**。MSP/ADR-0090 が退けた案 B（同じコードが環境で違う結果を出す）そのもの |
| 門の位置 | `InitializeAsync` の先頭 | 却下。例外はその前（コンストラクタ）で出る |
| | **コンストラクタの先頭**（コンテナの `Build()` をフィールド初期化子からコンストラクタへ移す） | **採用**。xUnit v3 はコンストラクタで投げた skip を Skipped として扱う（実測） |
| 担保の形 | 実走件数の下限（例 14 件以上） | 単独では却下。試験の増減で写し直す導出値になり腐る（規則 10） |
| | 依存の事前疎通検査 | 却下。門と同じ問いを別の場所で訊き直すだけで、試験が実際に走ったかは見ない |
| | **skip 件数の上限 0 ＋ 実走の下限 1 ＋ TRX の存在** | **採用**。門は依存を得られないときだけ skip するので、Docker のある CI で 1 件でも skip されたら依存が揃っていない実行である |

## 決定

### 決定 1: 門（MSP/ADR-0090 決定 1・2）

- `backend/Tests/AiStockTrading.IntegrationTests/RequiredServices.cs`（`E2EInfrastructure.cs` の隣）に
  `RequiredServices.Postgres` / `RabbitMq` / `Keycloak` と `SkipUnlessObtainable(params …)` を置く。外部の値は `E2EInfrastructure` の既存プロパティを読む（口は新設しない）。
- 判定は依存ごとに「外部供給 **または** コンテナ実行環境に届く」。1 つでも得られなければ `Assert.SkipWhen` で真の Skipped。
  外部供給だけで揃うときは Docker に問い合わせない。Docker の判定はプロセス内で 1 回だけ評価する。
- skip の理由には不足している依存名・環境変数・値の例・`scripts/e2e-local-infra.sh`（3 つをまとめて起こして env を出す）を並べる。
- 試験 7 クラスはコンストラクタの先頭で門を通し、その後にコンテナを組み立てる。
- 変えないもの: 片側だけの外部注入は `UseExternal` が fail fast（誤設定）。Docker に届くのにコンテナが起動できなければ fail（握り潰さない）。

### 決定 2: 担保（MSP/ADR-0090 決定 3）

- `scripts/check-integration-skips.js` が `integration.yml` の TRX（`--logger trx --results-directory integration-results`）を読み、対象アセンブリ
  （既定 `AiStockTrading.IntegrationTests`）について **G1 TRX が在る／G2 skip 件数 ≤ 0／G3 実走（合格＋失敗）≥ 1／G4 壊れた TRX は赤** を検査する。
- 発火時の出力: `::error::` に「アセンブリ・skip 件数・上限・実走件数・全件数」を 1 行、ログと `$GITHUB_STEP_SUMMARY` に試験名と skip の理由の一覧。
- 🔴 skip は `<Counters>` からは数えない —— xUnit v3 ＋ VSTest は skip を `outcome="NotExecuted"` の結果で書くが `notExecuted="0"` のまま残す
  （実測: total=68 executed=54 notExecuted=0）。結果を 1 件ずつ数える。
- `integration.yml` では検査（と自己試験）を `!cancelled() && steps.tests.outcome != 'skipped'` で走らせる（テストが赤でも評価する）。
- 既存の「fail-closed の門（0 件実行で落とす）は要らなくなった」は `--filter` 由来の 0 件の話であり、本検査とは別物である旨をワークフローに注記した。

## 理由

- 決定 1 は MSP/IADR-0414 の形を写し、判定だけを AST の実測に合わせて Testcontainers 自身の問いへ寄せた（問いを 1 つに保つ）。
- 決定 2 の上限 0 は、門の意味（依存を得られないときだけ skip）から直接導ける最も狭い条件であり、導出値を持たない。
  G3 の実走の下限は同アセンブリの非 Integration 試験（`E2EInfrastructureTests` 等）も数えるため、**全 skip の検知は G2 が担う**。
  G1・G3 は「アセンブリごと走らなかった」形の保険である。

## 結果

- Docker 不達の本環境: `AiStockTrading.IntegrationTests` は **Passed 54 / Skipped 14 / Failed 0**（従前 Failed 14）。
- 依存ごとの判定: `E2E_POSTGRES_CONNECTION`・`E2E_RABBITMQ_CONNECTION` だけを与えると、PostgreSQL だけを要る試験は走り（端点が偽なので接続で fail）、
  Keycloak を要る試験だけが skip した（1 つの真偽値へまとめていない）。
- 検査器: 本環境の実 TRX で発火（exit 1。「14 件が skip された（上限 0 件。実走 54 件 / 全 68 件）」と 14 件の名前・理由）。
  同じ TRX の skip を合格へ書き換えた合成では通過（exit 0）。自己試験 13 件。
- 悪い影響・トレードオフ: 将来、CI でも正当に skip する試験を同アセンブリへ足すと本検査が赤になる。そのときは許容一覧を持たせる改定を IADR で行う
  （上限を黙って上げない）。
- フォローアップ:
  - 担保が CI で実際に発火したら planning#575 / MSP/ADR-0090 フォローアップ 4 に従い報告する（本 PR の時点では未発火）。
  - 計画側への完了の環流（planning 側 `10_feedback/` への追記）は計画リポの作業。

## 関連

- Supersedes: なし
- Superseded by: なし
- 前提として扱い覆さないもの: [IADR-0049](./IADR-0049_integration-e2e-foundation.md)（Testcontainers 基盤・外部注入の口）／
  [IADR-0208](./IADR-0208_ci-pr-latency-reduction.md)（`integration.yml` は PR から外した検証の回収先。フィルタを付けない）／
  [IADR-0277](./IADR-0277_backend-test-failure-legibility.md)（TRX の読み取り）
