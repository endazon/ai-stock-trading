---
title: audit-service が GC ヒープ上限適用後も RSS 476Mi で OOMKilled —— ヒープ外（glibc アリーナ）の伸びを MALLOC_ARENA_MAX=2 で断つ
type: spec
status: done
related_ids: [NFR-01, ADR-0006, IADR-0129]
author: endazon (with Claude Code)
created: 2026-09-16
updated: 2026-09-16
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_infrastructure-and-deployment.md
---

# 仕様書: audit-service のヒープ外の伸びの根本原因と是正（#808）

## 起点

- #778（PR #779）で Workstation GC ＋ `GCConserveMemory=5`、#782（PR #805・2026-09-16 14:07Z マージ）で
  `DOTNET_GCHeapHardLimitPercent=60` を全 .NET サービスに入れた。それでも audit-service は**起動 10 分で RSS ≈476Mi →
  OOMKilled**（exit 137・14:17:43Z・`audit-service-65f7d54d7c-kvp9v` restarts=1）。#782 は「再起動が続くならヒープ外
  （Npgsql / OTel exporter / Wolverine / logging）の調査へ切り替え、limit は 768Mi へ上げない」と申し送っている。
- 本仕様書は稼働クラスタの**読み取りのみ**（`kubectl get/logs/top/exec -- cat /proc/1/*`・`/sys/fs/cgroup/*`）で
  原因を特定し、chart の env 1 件で是正する。

## 根本原因（実測）

### 1. GC ヒープは小さく、伸びているのはネイティブ（glibc malloc のアリーナ）

再起動後 12 分の audit-service: `memory.current` 517,144,576 / `memory.max` 536,870,912（**96%**）。
`memory.stat`: anon **428Mi**・file 82Mi・shmem 77Mi。fd 519・threads 20（risk-management / cost-control と同水準）。

`/proc/1/smaps` の anon `rw-p` を **64MiB 境界に整列した block（直後に `---p` の残り）＝glibc の非 main アリーナ heap**
（`HEAP_MAX_SIZE`=64MiB）とそれ以外に分けると:

| サービス | 実行時に生成したハンドラ数（`Generated code for …`） | アリーナ heap の RSS | それ以外の anon（GC ヒープ等） |
| --- | --- | --- | --- |
| backtest | 0 | 16.8 MB | 18.6 MB |
| configuration | 0 | 18.7 MB | 25.5 MB |
| cost-control | 1 | 58.3 MB | 122.0 MB |
| risk-management | 4 | **206.5 MB** | 54.6 MB |
| audit-service | 6 | **336.9 MB**（満杯 64MiB が 5 個） | **64.1 MB** |

アリーナ RSS ≈ 17 MB ＋ **≈53 MB × 実行時コンパイル回数**。audit の GC ヒープ側は 64 MB で、`GCHeapHardLimitPercent=60`
（≈307Mi）には遠い。**#782 の「ヒープ上限に達して 472Mi で頭打ち」は誤読で、新しい型のメッセージが来なくなっただけ**である。

### 2. 引き金は Wolverine の実行時コンパイル（メッセージ型ごと・1 通目・リスナスレッド上）

OOMKilled されたコンテナのログ（WRN は DataProtection の 2 行のみ・ERR 0・export / Npgsql / RabbitMQ の失敗なし）:

```
14:07:47 Generated code for WolverineHandlers.InformationCollectedHandler…
14:07:49 Generated code for WolverineHandlers.InformationSourceStateObservedHandler…
14:07:55 Generated code for WolverineHandlers.LlmCostIncurredHandler…
14:12:43 Generated code for WolverineHandlers.BrokerAvailabilityObservedHandler…
14:12:43 Generated code for WolverineHandlers.BrokerAccountObservedHandler…
14:17:43 Generated code for WolverineHandlers.BrokerPositionsObservedHandler…  ← 最終行 ＝ OOMKilled と同じ秒
```

- `TypeLoadMode.Dynamic`（IADR-0129 決定 6）では、Wolverine は**その型の 1 通目を受けたときに**ハンドラの C# を生成し
  Roslyn でコンパイルする（Wolverine 公式 codegen ガイド「Dynamically generate the types on the first usage」）。
- audit-service は**契約イベント全数（45 型）を購読する**（`AuditEventHandlers.cs`・AuditConsumerCoverageTests）ため、
  稼働中に最大 45 回のコンパイルが、それぞれのキューのリスナスレッドで走る。他サービスは数型で済む。
- コンパイル（Roslyn 本体の JIT を含む）の作業メモリは glibc malloc に載り、解放後もそのスレッドのアリーナに残る
  （64MiB heap の top が trim されない）。**スレッドが違えばアリーナも違う**ため、回数分だけ積み上がり、GC の上限では止まらない。

### 3. 棄却した候補（証跡）

| 候補 | 棄却理由 |
| --- | --- |
| OTel exporter のキュー滞留（collector 不達） | export 失敗のログ 0 件。file/shmem・fd・threads は 3 サービスで同水準 |
| Npgsql プール / HttpClient / fd リーク | fd は 519〜523 で全サービス同数（うち 120 は dll の mmap） |
| 監査台帳の全件再水和 | `Program.cs` に読み込みは無く、GC ヒープ側 anon が 64 MB しかない |
| GC 設定の不足 | ヒープ外なので `DOTNET_GC*` は効かない（#778 / #782 の実測どおり） |

## 設計

| 対象 | 変更 |
| --- | --- |
| `templates/deployment.yaml` | 共通 env に **`MALLOC_ARENA_MAX=2`** を足す（全 .NET サービス・本番既定にも。#778 / #782 と同じ場所） |
| `helm.yml` fail-safe 検査 | `ASPNETCORE_URLS` を持つ Deployment すべてに `MALLOC_ARENA_MAX` が在る（1 つでも欠ければ赤。同じ母集合） |
| IADR-0129 | 決定 6 の再評価条件（メモリ）が成立した実測と、chart 側の暫定策・恒久策（Static）を追記 |

- glibc のアリーナ数を 2 に固定すると、コンパイルの作業メモリは同じ 2 アリーナで再利用され、
  **購読型数（コンパイル回数）に比例した伸びが構造的に消える**（上限 ≈2×64MiB）。
- limits は据え置き（768Mi へは上げない。#782）。`opend.yaml` は触らない（#782 と同じ。OpenD Pod を再起動させない）。
- **引き金（実行時コンパイル）そのものは残す。** 恒久策は Wolverine の本番推奨（`dotnet run -- codegen write` で事前生成＋
  `TypeLoadMode.Static`・本番イメージから Roslyn を外す）で、11 サービスの `Program.cs`（`RunJasperFxCommands`）と
  Dockerfile（ビルド段で `codegen write`。DB 無しで通す並べ替え）に跨るため別 issue（IADR-0129 決定 6 の見直し）。

## 走査した母集合（規則 2・9・10）

- `grep -rlE "GCHeapHardLimitPercent|DOTNET_gcServer|GCConserveMemory"`（`.ai-context/specs/` を除く）:
  `helm.yml`（本 PR で検査を追加）、`templates/deployment.yaml`（本 PR で追記）、`CHANGELOG.md`（自動生成・触らない）。
  `docs/`・chart README・values・Dockerfile に GC / malloc 設定の記述は無い（追随なし）。
- `#778` / `#782` を引く IADR は無い（追記先は決定 6 を持つ IADR-0129）。
- 規則 10: 本変更で「ヒープ上限に達して頭打ち」と書いた #782 の仕様書・deployment.yaml のコメントは**当時の読み**であり、
  凍結記録（仕様書）は書き換えない。deployment.yaml 側は新しいコメントで訂正を隣に置く。

## 受け入れ基準 → 検証

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | 既定描画の全 .NET Deployment（11 件）に `MALLOC_ARENA_MAX=2` が在る | `helm template` ＋ helm.yml と同じ awk で欠落 0 件（ローカル・CI） |
| 2 | 既定描画・`opend.enabled=true`・values-local の描画差分が deployment.yaml 由来の env 追加のみで、`opend.yaml` 由来の Deployment は不変 | develop の template との描画 diff |
| 3 | 配備後、audit-service のアリーナ合計が ≈128MiB で止まり、45 型すべてを受けても RSS が 512Mi の内側に留まる | `/proc/1/smaps` の 64MiB 整列 `rw-p` 合計と restarts を観測して #808 に記録（**本 PR では未検証**） |

## 未検証

- 効果の実測（基準 3）。稼働 Pod を再起動せずに測れるのはここまでである。
- アリーナに残る作業メモリが「解放済みで trim されない」のか「解放されていない」のかの最終確定（外部からは区別できない）。
  後者なら `MALLOC_ARENA_MAX` では止まらず、恒久策（Static）を前倒しする。

## 計画書との差異

- 差異: なし（ADR-0006 の配備方式の範囲内。limit を変えない）。
