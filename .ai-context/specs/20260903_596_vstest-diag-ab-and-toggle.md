---
title: backend-test の --diag 計装を A/B 計測し、既定無効の切り替え口へ落とす
type: work
status: review
related_ids: [NFR, ADR-0001, IADR-0208, IADR-0257, IADR-0277]
author: claude (Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/02_non-functional.md
---

# 作業仕様書: backend-test の `--diag` 計装の A/B 計測と切り替え口化（#596 フォローアップ）

> [IADR-0277](../adr/IADR-0277_backend-test-failure-legibility.md) の「フォローアップ:
> `--diag` の費用が無視できないと実測されたら opt-in 化する」を実施する。
> [#596](https://github.com/endazon/ai-stock-trading/issues/596) の続き。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（CI の非機能。NFR）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: ADR-0001（リポ構成・スタック）
- 関連 IADR: [IADR-0277](../adr/IADR-0277_backend-test-failure-legibility.md)（`--diag` 導入の決定 6・
  フォローアップ節）、[IADR-0208](../adr/IADR-0208_ci-pr-latency-reduction.md)（シャーディング・
  `-m:1` を入れない決定 8）、[IADR-0257](../adr/IADR-0257_ci-test-sharding-lpt-by-scan.md)

## 目的・背景

IADR-0277 は `backend-test` の Test ステップへ `--logger trx` / `--blame-crash --blame-hang
--blame-hang-timeout 10m` / `--diag "${RUNNER_TEMP}/vstest-diag/log.txt"` を足した。導入後の実測
（計装あり 3 run）は計装なし（2 run）より Test ステップ所要が長く見えたが、run 間のばらつきと
区別できておらず、**`--diag` 単体の寄与**は測っていなかった。

`--diag` が保険として狙う仮説（テストホストの異常終了）は IADR-0277 の調査で**実測により棄却
済み**である（3 観測すべてが名前の付いたアサーション失敗であり、ホストは異常終了していない）。
したがって `--diag` は「棄却済みの仮説のために毎 run 払うコスト」になっている疑いがあり、
測らずに opt-in 化を決めることも、測らずに現状維持を決めることも避け、**A/B 計測してから決める**。

## 対象範囲

- 対象:
  - `.github/workflows/ci.yml` の `backend-test` ジョブ「Test (shard N / 4)」ステップに、
    `--diag` の要否を環境変数 `VSTEST_DIAG` で切り替える口を足す（既定は無効＝空文字列）。
  - `scripts/summarize-test-failures.js` の `CRASH_HINT` の `--diag` 行の案内文を、
    既定無効を前提にした文言へ直す。
  - `scripts/scripts.repo.test.js` へ、`VSTEST_DIAG` が既定空であること・`--diag` が
    ci.yml へ素で（切り替え口を経由せず）直書きされていないことを固定する回帰テストを足す。
  - IADR-0277 の決定 6・§結果の計測表を、本作業の実測値で追記節として更新する（旧記述は残す）。
- 対象外:
  - `--logger trx` / `--blame-crash` / `--blame-hang` の切り替え口化（IADR-0277 決定 D・E の
    採用理由に反する。対象外のまま）。
  - カバレッジの数え方・artifact glob（IADR-0277 決定 12。変更しない）。
  - ジョブ名 `backend-test` / `build-and-test` の変更（`scripts/check-workflow-job-refs.js` が
    検査する必須チェック名。変更しない）。

## 設計

### 計測方法

IADR-0277 の実測 4 と同じ方法を踏襲する: 対象 run の各シャードジョブについて
`gh api repos/endazon/ai-stock-trading/actions/runs/<id>/jobs` の `steps[]` から
`Test (shard` で始まるステップの `started_at` / `completed_at` の差（秒）を取る。

- **「on」側（`--diag` あり）**: IADR-0277 実測 4 で既に取得済みの 3 run
  （33642953401 / 33647669010 / 33648295105）をそのまま用いる。再計測はしない
  （同一構成を再度走らせても新しい情報は増えず、GitHub API のレート制限を無駄に消費するため）。
- **「off」側（`--diag` のみ外す。TRX・blame はそのまま）**: 本 PR で切り替え口を既定無効
  （`VSTEST_DIAG: ""`）にした状態を **2 本以上** 計測する。1 本目は本 PR の実装コミットの
  CI run、2 本目は `git commit --allow-empty` による計測専用コミットの CI run とする。

🔴 develop の構成（テストプロジェクト数・重み）は run ごとに動き得る（IADR-0257 の LPT 分配は
毎回スキャンし直す）。計測期間中に develop 側でテストプロジェクトの追加・削除があれば、
表の注に明記する。

### 切り替え口の実装

```yaml
- name: Test (shard ${{ matrix.shard }} / 4)
  id: test
  if: ${{ steps.gate.outputs.backend == 'true' }}
  env:
    VSTEST_DIAG: "" # "1" で --diag を有効化する（既定は無効）
  run: |
    ...
    diag_args=()
    if [ -n "${VSTEST_DIAG}" ]; then
      diag_args=(--diag "${RUNNER_TEMP}/vstest-diag/log.txt")
    fi
    dotnet test shard.slnx --no-build --configuration Release \
      --filter "Category!=Integration" \
      --collect:"XPlat Code Coverage" \
      --results-directory "$PWD/cov" \
      --logger trx \
      --blame-crash --blame-hang --blame-hang-timeout 10m \
      "${diag_args[@]}" \
      --verbosity normal
```

- `--logger trx` / `--blame-crash --blame-hang --blame-hang-timeout 10m` は据え置く
  （IADR-0277 決定 D・E。対象外）。
- `Upload test diagnostics` ステップの `${{ runner.temp }}/vstest-diag/**` は据え置く
  （`VSTEST_DIAG` 無効時はディレクトリ自体が作られず `if-no-files-found: warn` が拾うだけで、
  既存の「失敗時のみアップロード」という条件と矛盾しない）。

### 決定基準

- 寄与が小さくても既定 off にしてよい（依頼の前提どおり）。**ただし取り外さず、切り替え口を残す**
  （再現・切り分けの手段を失わないため）。
- 計測結果は「## 受け入れ基準」の実測欄と IADR-0277 追記節に記録する。

## 受け入れ基準

- [ ] `--diag` だけを外した CI 実行を 2 本以上取得し、run id・shard 1〜4・合計・最大を記録した
- [ ] 記録した実測に基づいて既定 on/off を決定した（数値を測らずに決めていない）
- [ ] `VSTEST_DIAG` 切り替え口を実装し、既定は無効（`--diag` を実行しない）
- [ ] `--logger trx` / `--blame-crash --blame-hang` は変更していない
- [ ] カバレッジの数え方（`find cov -mindepth 2 -maxdepth 2`）と artifact glob
      （`cov/*/coverage.cobertura.xml`）を変更していない
- [ ] `-m:1` を入れていない
- [ ] ジョブ名 `backend-test` / `build-and-test` を変更していない
- [ ] `scripts/scripts.repo.test.js` に「`VSTEST_DIAG: ""` が既定」「素の `--diag` が直書きされて
      いない」ことを固定する回帰テストを足した
- [ ] `scripts/summarize-test-failures.js` の `CRASH_HINT` の `--diag` 行を、既定無効を前提にした
      文言（`VSTEST_DIAG` を `"1"` にした PR で再現させる）へ直した
- [ ] IADR-0277 の決定 6・§結果の計測表を実測値で更新した（追記節。旧記述は残す）
- [ ] `node scripts/check-workflow-job-refs.js` / `node scripts/scripts.test.js` /
      `node scripts/check-trace-blocks.js` / `node scripts/check-adr-index-sync.js` が通る

## テスト方針

- `scripts/scripts.repo.test.js` の既存 `summarize-test-failures` ブロックへ以下を追加する:
  - `ci.yml` の Test ステップの `env:` に `VSTEST_DIAG: ""` があること（既定無効の固定）。
  - `ci.yml` の `dotnet test` 呼び出し行に素の `--diag "..."` が直書きされていないこと
    （`diag_args` 経由でのみ登場すること）。
  - 上記は否定形（壊れたら赤くなる形）で書き、IADR-0277 の既存自己試験の書き方に揃える。
- CI 実行そのもの（A/B 計測）は自動テストの対象外（GitHub Actions の実測）。

## 計画書との差異

- 差異: なし

## 未決事項

- なし（決定基準は「寄与の大小によらず既定 off とし、切り替え口を残す」で確定済み。実測は
  「小さいから off にした」か「大きいから off にした」かを記録するためのものであり、
  決定そのものを左右しない）。
