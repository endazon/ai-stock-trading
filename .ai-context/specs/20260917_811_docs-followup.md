---
title: "#811 の残項目 —— #782 / #808 / #812 の実測結果（malloc の調整は効果なし）を記録し、PR #814 の監査の非ブロッキング指摘を是正する"
type: spec
status: done
related_ids: [NFR-01, ADR-0006, IADR-0129]
author: endazon (with Claude Code)
created: 2026-09-17
updated: 2026-09-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_infrastructure-and-deployment.md
---

# 仕様書: #811 の文書是正と監査指摘の是正（#811 射程 2・3 の残り）

## 起点

- #811 射程 1（PR #814。`codegen write`＋`TypeLoadMode.Static`）は配備後の実測で受け入れ基準が成立した（#811 のコメント。
  2026-09-16 16:49Z: 実行時コンパイル 0 件・audit `[heap]` 12 MB・11 サービスとも再起動 0）。
- 残りは射程 2（#782 コメントの是正・仕様書の追記・「上限 ≈2×64MiB」の表現）と、#812 のコメントで判明した
  **malloc の調整（#810 / #813）は保持量を減らさなかった**という実測の記録、および PR #814 の監査の非ブロッキング指摘。
- **env の値は一切変えない**（`helm template` の描画はバイト一致。コメントは `{{- /* */}}` 内なので描画されない）。
  クラスタ・イメージには触らない。

## 設計

| 対象 | 変更 |
| --- | --- |
| `deploy/helm/ai-stock-trading/templates/deployment.yaml` | #782 コメントに `［2026-09-17 追記 / #811］`（「512Mi の内側で止める」「472Mi 頭打ち」は #808 で誤りと判明・是正は #811）。#808 コメントの「上限 ≈2×64MiB」を「アリーナの数の上限でありサイズの上限ではない」へ改め、「別 issue」を #811 と名指し。#812 コメントに 3 つの malloc env の実測結果（保持先がアリーナ → `[heap]` → mmap へ移っただけ・据え置き・再評価）を追記。live な文書なので #808 コメントは本文を直す |
| `.ai-context/specs/20260916_782_…md` | `［2026-09-17 追記 / #811］` で受け入れ基準 3 が不成立だったことを記す（本文は書き換えない） |
| `.ai-context/specs/20260916_808_…md` | 同形式で基準 3 不成立・「上限 ≈2×64MiB」の訂正・`RunJasperFxCommands` 記述の実装との差 |
| `.ai-context/specs/20260916_812_…md` | 同形式で基準 4 不成立（mmap 領域へ移っただけ）・§3 の読みの棄却 |
| IADR-0129 | `［2026-09-17 追記 / #811］`: (i) malloc 設定は効果なし（実測）、表現の是正（≈2×64MiB）、(ii) 索引行の `RunJasperFxCommands` 要約の是正（shim が振り分ける）、(iii) Dockerfile の `grep -q 'UseWolverine('` はコメントに反応し検査器と同一ではない（どちらへずれても fail-loud）。凍結済みの追記本文は書き換えない |
| `.ai-context/adr/README.md` | IADR-0129 の索引行: 「`Program.cs` は `RunJasperFxCommands`」を shim の振り分けへ直し、本追記の要約を足す（`check-adr-index-sync`） |
| `WolverineTypeLoadModeTests.共通配線の既定は_Dynamic_である` | テスト中だけ `WOLVERINE_TYPE_LOAD_MODE` を未設定にして元へ戻す。書き換えが並列の他テストへ漏れないよう、クラスを `DisableParallelization = true` のコレクションに入れる。本番コードは変えない |

- IADR-0129 の `updated:` は既に 2026-09-17（同日）。仕様書 3 件の `updated:` は 2026-09-17 へ進める。

## 走査した母集合（規則 2・9・10）

誤りの側の文字列で全追跡ファイルを走査した（`grep -rn`、`.git`・`bin`・`obj`・`node_modules` を除く）。

| 文字列 | ヒット | 扱い |
| --- | --- | --- |
| `2×64` | `deployment.yaml` #808 コメント／IADR-0129 #808 追記／仕様書 808 §設計／仕様書 812 §走査した母集合（引用） | deployment.yaml は本文を是正。IADR・仕様書は凍結記録なので日付つき追記で訂正 |
| `2x64` | 0 件 | — |
| `頭打ち` | `deployment.yaml` #782 コメント／仕様書 782 起点・基準 3／仕様書 808 §根本原因・規則 10（既に「誤読」として記述）／仕様書 812 基準 4 | deployment.yaml・仕様書 782・812 は追記。仕様書 808 の 2 箇所は誤読だと述べる側なので対応不要 |
| 〃（除外） | IADR-0208・IADR-0277・`ci.yml`（CI の並列度）、仕様書 478・ci-shard（別件）、`TokenBucketTests`（レート制限） | 別主題の語。対象外 |
| `RunJasperFxCommands` | IADR-0129 #808 追記（予告時点）／IADR-0129 #811 追記 決定 6-5（正しい）／索引行（不正確な要約）／仕様書 808 §設計（予告時点）／仕様書 20260917_811（正しい文脈）／`JasperFxCommandLine.cs`（実装） | 索引行は本文を是正。IADR #808 追記・仕様書 808 は追記で訂正。他は正しいので対応不要 |
| `同じシグナル` | 0 件 | 同義の `同じ信号` が `backend/Dockerfile` の codegen 分岐のコメントに 1 件 → IADR-0129 追記 (iii) と索引行に記録。Dockerfile 自体は本 PR で触らない（イメージの入力を変えない方針・指示の射程は IADR への記録） |

- 規則 10（本変更で新たに誤りになる自分の記述）: 索引行の旧要約「`Program.cs` は `RunJasperFxCommands`」を直した結果、IADR-0129 追記 (ii) の
  「索引行が…と要約していた」は過去形で書いた（直後の状態と矛盾しない）。deployment.yaml の #808 コメントで「別 issue」→「#811」に
  したため、同コメント内に「別 issue」は残らない。
- テスト側: `UseAiStockTradingRabbitMq` を呼ぶテストは各サービスの Tests にも多数あり、同じく実プロセスの env を読む。
  本 PR の射程は監査が名指しした 1 件に留める（他は `WOLVERINE_TYPE_LOAD_MODE` を設定した環境でテストを走らせない限り表面化しない）。

## 受け入れ基準 → 検証

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | `helm template` の描画が develop と完全一致（既定・`values-local.yaml`＋`opend.enabled=true`） | 変更前後の描画を `diff`（差分 0） |
| 2 | ambient に `WOLVERINE_TYPE_LOAD_MODE=Static` があっても `共通配線の既定は_Dynamic_である` が通る（変更前は落ちる） | 変更前のテストを env つきで実行して FAIL、変更後に PASS |
| 3 | ビルド・テスト・フォーマット | `dotnet build backend/backend.slnx -c Release`／PlatformShim.Tests の `dotnet test`／`dotnet format --verify-no-changes` |
| 4 | 文書検査が通る | `check-trace-blocks`／`check-test-traceability`／`check-adr-index-sync`／`check-commit-messages`／`check-cross-repo-refs`／`check-plan-id-qualification` |

## 計画書との差異

- 差異: なし（コメント・記録の是正とテストの堅牢化のみ。ADR-0006 の配備方式に触れない）。
