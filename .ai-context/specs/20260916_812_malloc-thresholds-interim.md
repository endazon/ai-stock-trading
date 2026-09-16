---
title: MALLOC_ARENA_MAX=2 では伸びが main arena（[heap]）へ移るだけで OOMKilled が続く —— 固定 mmap / trim 閾値で解放を強制する（#811 までの暫定）
type: spec
status: done
related_ids: [NFR-01, ADR-0006, IADR-0129]
author: endazon (with Claude Code)
created: 2026-09-16
updated: 2026-09-16
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_infrastructure-and-deployment.md
---

# 仕様書: `MALLOC_ARENA_MAX=2` の後も続く OOMKilled への暫定策（固定 mmap / trim 閾値）（#812）

## 起点

- #808（PR #810・2026-09-16 15:02Z 配備）で全 .NET サービスに `MALLOC_ARENA_MAX=2` を入れた。根本原因は Wolverine の実行時
  Roslyn コンパイル（`TypeLoadMode.Dynamic`。IADR-0129 決定 6）の作業メモリが glibc malloc に残ること、恒久策は
  `codegen write`＋`TypeLoadMode.Static`（#811。別エージェントが並行実装中＝**11 サービスの `Program.cs` と Dockerfile には触らない**）。
- 本仕様書は、配備 23 分後に稼働クラスタを**読み取りのみ**（`kubectl get/logs/top`・`kubectl exec … cat /proc/1/smaps`・
  `/sys/fs/cgroup/memory.stat`。再起動・patch・set env はしない。OpenD Pod `opend-7cfd694994-2j75c` には触らない）で再測し、
  #811 までの **chart 側の暫定策**を 1 件足す。

## 実測（2026-09-16 15:26Z）

### 1. `MALLOC_ARENA_MAX=2` は効いているが、OOMKilled は続いている

- audit-service（`audit-service-587c5987c-tf429`）: **restarts=1**。前コンテナ 15:02:52Z 起動 → **15:03:01Z OOMKilled（exit 137・9 秒後）**。
  その 9 秒のログに `Generated code for …` が 6 行（起動直後のメッセージ束で 6 型が同時にコンパイルされた）。
- 現コンテナ（6 コンパイル: 15:07:58〜15:12:57Z）: `kubectl top` 459Mi、cgroup `memory.current` 488,722,432 / `memory.max` 536,870,912（91%）、
  `memory.stat` anon 399,159,296（381Mi）・file 83,447,808、threads=19。env に `MALLOC_ARENA_MAX=2`・`DOTNET_GCHeapHardLimitPercent=60`・
  `DOTNET_GCConserveMemory=5` が入っていることを `env` で確認。64MiB 整列のアリーナ heap は **2 個**（#808 の 6 個から減った＝env は効いている）。

### 2. 積み上がる先が per-thread アリーナから main arena の `[heap]` へ移っただけ

`/proc/1/smaps` の anon `rw-p`（Rss ≥ 4MB。すべて Rss = Private_Dirty）:

| サービス | コンパイル回数 | `[heap]`（main arena・brk） | 64MiB 整列の anon（非 main アリーナ heap） | `[heap]`＋アリーナ | cgroup anon |
| --- | --- | --- | --- | --- | --- |
| audit-service | 6 | **214,624 kB** | 64,652 kB ＋ 34,588 kB | 313.9 MB | 381Mi |
| risk-management | 4 | **111,056 kB** | 65,252 kB ＋ 37,112 kB | 213.4 MB | 270Mi |
| cost-control | 1 | 11,912 kB | 40,372 kB | 52.3 MB | 171Mi |
| trade-decision | 1 | 10,416 kB | 41,780 kB | 52.2 MB | 166Mi |

**≈52〜53 MB × コンパイル回数**で、#808 の実測（アリーナ 6 個で 337Mi・≈53Mi/型）と傾きが同じ。各サービスに共通して現れる
非整列の 35〜36MB・15MB の anon（GC の初期コミット・loader heap と読む）はコンパイル回数に依らない。
glibc は Ubuntu 24.04 の **2.39**（`/lib/x86_64-linux-gnu/libc.so.6`）。

### 3. 仮説（根本原因の読み）と、#808 で読み違えたこと

- #808 は「アリーナ数を 2 にすれば作業メモリが**同じアリーナで再利用**される」と読んだ。再利用は起きるが、**heap の top が OS へ返らない**
  機構は別にあった —— glibc の**動的閾値**（mallopt(3)）: 128 KiB 超〜32 MiB（`DEFAULT_MMAP_THRESHOLD_MAX`＝64bit で 4×1024×1024×sizeof(long)）
  の mmap チャンクを free するたび `M_MMAP_THRESHOLD` がその大きさへ、`M_TRIM_THRESHOLD` がその 2 倍（最大 64 MiB）へ上がる。
  以後、その大きさまでの割り当ては mmap ではなく heap（brk / アリーナ heap）に積まれ、**top の空きが閾値（最大 64 MiB）に届くまで trim されない**。
- 非 main アリーナの heap は 1 個 64 MiB なので構造的に一度も trim されず（#808 で各アリーナが ≈53Mi のまま残った形）、main arena も
  同じ理由で `[heap]` が伸び続ける。`MALLOC_ARENA_MAX` は「アリーナの数」しか変えないため、この機構に無関係。
- コンパイル 1 回ごとの ≈52 MB を「解放済みだが返っていない」と読む根拠: GC ヒープは別マッピング（mmap 予約領域）で小さく、生成される
  handler アセンブリは数 KB であり、live なネイティブデータが 52 MB/回ずつ増える経路が見当たらない。ただし**外部からは確定できない**
  （後述「未検証」）。

### 4. 棄却した候補

| 候補 | 棄却理由 |
| --- | --- |
| GC ヒープの伸び（`DOTNET_GC*` の不足） | `[heap]` は brk 領域で GC の予約領域ではない。GC 側の anon は各サービス 35〜36MB で回数に依らない |
| env が効いていない（イメージが古い等） | `env` で 3 変数を確認。アリーナ heap が 6 個 → 2 個に減っている |
| 起動時の同時コンパイルだけの問題（定常では止まる） | 再起動後の定常運転でも 6 コンパイルで 459Mi・91%。次の新しい型で 512Mi を超える |

## 設計

| 対象 | 変更 |
| --- | --- |
| `deploy/helm/ai-stock-trading/templates/deployment.yaml` | 共通 env に **`MALLOC_MMAP_THRESHOLD_=131072`** と **`MALLOC_TRIM_THRESHOLD_=131072`** を `MALLOC_ARENA_MAX` の直後に足す（コメントに NFR-01 / ADR-0006 / #812 / IADR-0129 決定 6 追記）。全 11 .NET サービス・本番既定にも入れる |
| `.github/workflows/helm.yml` fail-safe 検査 | `ASPNETCORE_URLS` を持つ Deployment すべてに 2 変数が在る（1 つでも欠ければ赤。#778 / #782 / #808 と同じ母集合・同じ awk） |
| IADR-0129 | `［2026-09-16 追記 / #812］` を #808 追記の直後に置く（読み違え・暫定策・限界）。`updated:` は 2026-09-16 のまま（同日） |
| `.ai-context/adr/README.md` | IADR-0129 の索引行の状態欄に #812 追記を足す（`check-adr-index-sync` の要求） |

- `MALLOC_ARENA_MAX=2` は**残す**（アリーナ数の上限は依然として有効。外すと per-thread アリーナに戻る）。
- どちらかの閾値を明示すると glibc は動的調整を**無効化**する（mallopt(3)。`MALLOC_TOP_PAD_` / `MALLOC_MMAP_MAX_` でも同じだが、
  最小の 2 件に留める）。効果: 128 KiB 超の割り当ては常に mmap → free で即 munmap。heap top の空きが 128 KiB を超えれば free のたびに
  trim（main arena は `systrim` で brk 縮小、非 main アリーナは `heap_trim` → `shrink_heap`）。末尾の `_` は glibc の env 名の規約
  （`MALLOC_ARENA_MAX` だけ無し）。
- `templates/opend.yaml` は触らない（OpenD Pod を再起動させない。#782 / #808 と同じ）。limits は据え置き。
- **#811 の領域（`Program.cs`・Dockerfile）には触らない。**

## 走査した母集合（規則 2・9・10）

- `git grep -n -I -E "MALLOC_|mallopt|malloc_trim|M_TRIM|M_MMAP|アリーナ"`（除外: `.ai-context/specs/`＝point-in-time の記録、`CHANGELOG.md`＝自動生成）:
  `.ai-context/adr/IADR-0129_…md`（本 PR で追記）・`.ai-context/adr/README.md`（索引行を更新）・`.github/workflows/helm.yml`（検査を追加）・
  `deploy/helm/ai-stock-trading/templates/deployment.yaml`（env を追加）。`docs/`・chart README・values・Dockerfile には malloc 設定の記述は無い（追随なし）。
- `git grep -n -I -E "#808|#810"`（同じ除外）: 上の 4 ファイルのみ。#808 の「別 issue」は #811 として起票済みなので、索引行の「（別 issue）」を「（#811）」へ直した。
  IADR-0129 の #808 追記本文の「（別 issue）」は凍結記録として書き換えず、新しい追記で #811 を名指しする。
- 規則 10: #808 の仕様書・deployment.yaml の #808 コメント（「同じアリーナで再利用させ、比例の伸びを断つ（上限 ≈2×64MiB）」）は当時の読みであり、
  仕様書は書き換えない。deployment.yaml 側は新しいコメントで訂正を隣に置く（#808 が #782 に対して取ったのと同じ形）。

## 受け入れ基準 → 検証

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | 既定描画の全 .NET Deployment（11 件）に `MALLOC_MMAP_THRESHOLD_=131072`・`MALLOC_TRIM_THRESHOLD_=131072` が在り、`MALLOC_ARENA_MAX=2` も残っている | `helm template` ＋ helm.yml と同じ awk で欠落 0 件（ローカル・CI）。`helm lint --strict` が通る |
| 2 | 既定描画・`values-local.yaml`＋`opend.enabled=true` の描画差分が env 追加 4 行 × 11 Deployment のみで、`opend.yaml` 由来の Deployment は不変 | develop（`git archive develop`）と本ブランチの `helm template` を diff。opend Deployment の md5 一致 |
| 3 | 文書検査が通る | `check-trace-blocks` / `check-adr-index-sync` / `check-commit-messages` / `check-cross-repo-refs` / `check-plan-id-qualification` |
| 4 | **配備後**: audit-service で ≥6 型のコンパイル後に `[heap]`＋64MiB 整列 anon の合計が ≈52 MB × 回数で伸びない（目安: 6 回で 100 MB 未満）、30 分で restarts=0、`kubectl top` が 512Mi の内側で頭打ち | `kubectl exec deploy/audit-service -- cat /proc/1/smaps` の `[heap]` Rss と 64MiB 整列 `rw-p` の合計、`kubectl get pod` の RESTARTS、`kubectl logs | grep -c "Generated code for"` を #812 に記録（**本 PR では未検証**） |

## 未検証（本策の限界）

- **解放されていない（live な）割り当て**なら閾値をいくら固定しても返らない。**top より下に断片化して残る空き**（128 KiB 未満の割り当てが
  live なチャンクに挟まれる形）も glibc は自動では返さない（`malloc_trim` の明示呼び出しでしか内側の空きを `MADV_DONTNEED` しない）。
  この場合は本策では止まらず、**#811 だけが直す**。稼働 Pod で `malloc_info` を取る手段（gdb / LD_PRELOAD）は無く、判定は基準 4 の実測で付ける。
- 128 KiB 固定は mmap / munmap のシステムコールとページフォルトを増やす。.NET の GC ヒープは別経路なので暫定として受容し、#811 の完了時に
  閾値の要否を再評価する（外す場合も `helm.yml` の検査を同時に外す）。
- glibc 2.39 で `MALLOC_*_` の env エイリアスが有効なことは `MALLOC_ARENA_MAX` が効いた実測から推定している（直接の確認は配備後の実測）。

## 計画書との差異

- 差異: なし（ADR-0006 の配備方式の範囲内。limit を変えない。#811 の射程に踏み込まない）。
