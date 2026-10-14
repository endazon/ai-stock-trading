---
title: DOTNET_GCHeapHardLimitPercent="60" は 16 進数として読まれ 96% になっている —— 0x3C（60%）に直し、16 進数で読んだ値を CI で固定する（#1348）
type: spec
status: done
related_ids: [NFR, ADR-0006, IADR-0129]
author: claude (Claude Code)
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_infrastructure-and-deployment.md
---

# 仕様書: GC ヒープ上限の値を 16 進数で書く（#1348）

> 本仕様書は実装着手前に作成した（着手 2026-10-14）。基点は AST `origin/develop` `7ba4c0e4`。

## 起点

- #1348: `templates/deployment.yaml` は全 .NET サービスに `DOTNET_GCHeapHardLimitPercent: "60"` を与えている（#782）。
  .NET は GC 設定の**環境変数の値を 16 進数として読む**ため、`"60"` は 0x60＝**96%** として効いている。
  #782 の意図（limits 512Mi の 60%＝≈307Mi）に対し、実効の上限は ≈491Mi である。
- 出典: Microsoft Learn「Garbage collector config settings」
  （https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector）の「Ways to specify the configuration」の表。
  環境変数の行は「`0xn` or `n` — n is interpreted as a hex value in either format」。`GCHeapHardLimitPercent` の欄も
  *hexadecimal value* で、例は「30% → `0x1E` or `1E`」。
- 見つけた経緯: MSP 側で同じ値を移植する PR（MSP#1928）の AI レビューの指摘。
- 影響の範囲: #811 の追記どおり OOMKilled の主因は GC ヒープの外（Roslyn の実行時コンパイルと malloc のアリーナ）であり、
  今の安定は GC ヒープの上限によらない。直すのは「上限を 60% に固定した」という記述と実効値の食い違いである。

## 設計

| 対象 | 変更 |
| --- | --- |
| `templates/deployment.yaml` | `DOTNET_GCHeapHardLimitPercent` の値を `"0x3C"` にする。値のそばのコメントに出典と「環境変数は 16 進数」を書く。#782 / #811 の既存コメントは本文を変えず、［2026-10-14 追記 / #1348］を足す |
| `helm.yml` fail-safe 検査 | (1) 既定描画の .NET Deployment 全件の `DOTNET_GCHeapHardLimitPercent` を **16 進数として読んで 60** であること（`0x` の明示も要求）。(2) 既定と values-local の両描画で、`DOTNET_GC*` / `COMPlus_GC*` / `DOTNET_gc*` の値に **`0x` の無い 10 以上の 10 進数**が 1 つも無いこと（一桁は 16 進数でも同じ値なので許す） |
| 記録 | #782 の作業仕様書・IADR-0129（#808 追記の「#778 / #782 の GC 設定」）・MSP#1919 の作業仕様書（棚卸しで `GCHeapHardLimitPercent=60` を「矛盾なし」とした行）に日付つきの追記 |

- 新しい IADR は作らない。決定（ヒープ上限を limits の 60% にする）は #782 のまま変わらず、書き方の誤りを直すだけである。
  「GC の環境変数は 16 進数で読まれる」の根拠は値のそばのコメントと CI の検査が持つ。
- opend-auth-gateway（`deploy/opend/k8s/opend.yaml` と chart の `opend.enabled=true` 描画）は #782 以来**意図して対象外**で、
  GC の env を持たない。本 PR でも入れない（ログイン済み OpenD セッションを切らないため）。検査 (1) の母集合は既定描画
  （`opend.enabled=false`）の `ASPNETCORE_URLS` を持つ Deployment なので、opend-auth は入らない。

## 走査した母集合（規則 2・9・10）

`git grep -niE 'DOTNET_GC|COMPlus_GC|DOTNET_gc|COMPlus_gc|GCHeap|HeapHardLimit|GCgen0|GCHeapAffin|GCHeapCount|GCConserve|gcServer|gcConcurrent'`
を追跡下の全ファイル（chart・`values*.yaml`・`docker-compose.yml`・`backend/Dockerfile`・`deploy/opend/Dockerfile`・docs・IADR・
作業仕様書・`.github/workflows/`）に掛けた。加えて `DOTNET_[A-Za-z]+ *[=:]` で他の `DOTNET_*` の値を引いた。

| 箇所 | 値 | 判定 |
| --- | --- | --- |
| `templates/deployment.yaml` `DOTNET_GCHeapHardLimitPercent` | `"60"`（＝0x60＝96%） | **誤り。直す**（`"0x3C"`） |
| `templates/deployment.yaml` `DOTNET_gcServer` | `"0"` | 一桁。16 進数でも 0。直さない |
| `templates/deployment.yaml` `DOTNET_GCConserveMemory` | `"5"` | 一桁（値域 0〜9）。16 進数でも 5。直さない |
| `helm.yml` fail-safe 検査（#778 / #782） | 名前の在否だけを見る | 値を見ていない。**検査を足す**（上の設計） |
| `values.yaml`・`values-local.yaml`・`values-grpc-measurement.yaml`（`extraEnv` 含む） | GC の env なし | 該当なし |
| `docker-compose.yml`・`backend/Dockerfile`・`deploy/opend/Dockerfile`・`deploy/opend/k8s/opend.yaml` | GC の env なし（Dockerfile の `DOTNET_VERSION` は SDK の版で GC 設定ではない） | 該当なし。opend は意図して対象外 |
| docs（`docs/`・chart の README） | GC 設定の記述なし | 該当なし |
| `.ai-context/specs/20260916_782_dotnet-gc-heap-hard-limit.md` | 「`=60`」「≈307Mi」 | 凍結記録。本文は変えず**日付つき追記** |
| `.ai-context/adr/IADR-0129_wolverine-messaging-topology.md`（#808 追記） | 「#778 / #782 の GC 設定はこの伸びに効かない」 | 凍結記録。**日付つき追記**（結論は変わらない旨） |
| `.ai-context/specs/20261014_msp1919_host-memory-prereq.md` | 棚卸しで `GCHeapHardLimitPercent=60` を「矛盾は無い」 | **日付つき追記**（実効 96% だった） |
| `.ai-context/specs/20260911_778_*`・`20260916_808_*`・`20260916_812_*` | `=60` は当時の env の実測・引用 | 当時の事実の記録として正しい（文字列 `"60"` が入っていた）。追記しない |
| `CHANGELOG.md` | #805 の件名 `=60` | 生成物。コミット件名の記録なので触らない |
| `MALLOC_ARENA_MAX` / `MALLOC_MMAP_THRESHOLD_` / `MALLOC_TRIM_THRESHOLD_` | 10 進数 | glibc の env で 10 進数として読まれる。対象外 |

規則 10（この変更で新たに誤りになる自分の記述）: deployment.yaml の #782 コメント「ヒープ上限を limit の 60%（≈307Mi）に
固定し」は、本 PR で初めて事実になる。#782 当時から本 PR までは誤りだった旨を追記で示す。

## 受け入れ基準 → 検証

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | 既定描画の .NET Deployment 11 件すべての `DOTNET_GCHeapHardLimitPercent` が `"0x3C"` | `helm template` と helm.yml と同じ検査をローカルで実行 |
| 2 | 値を `"60"` に戻すと検査が赤になる（変異） | ローカルで deployment.yaml を一時的に戻して検査を実行 |
| 3 | 描画の差分が当該 1 行だけ | develop と本ブランチの `helm template`（既定・values-local・values-local＋opend）の diff |
| 4 | opend-auth-gateway の描画は変わらない | 同上（`opend.enabled=true`） |
| 5 | `helm lint --strict`・文書系の検査器・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る | ローカル実行 |
| 6 | 配備後に実効のヒープ上限が limits の約 60%（≈307Mi＝322,122,547 バイト前後）になる | PoC での確認（PR 本文の手順）。本 PR の外 |
