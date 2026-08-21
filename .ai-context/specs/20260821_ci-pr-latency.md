---
title: 作業仕様書 — PR の CI 所要時間を実測起点で縮め、落とした精度を後段で担保する
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0049
  - IADR-0080
  - IADR-0087
  - IADR-0127
  - IADR-0208
author: claude
created: 2026-08-21
updated: 2026-08-22
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0029_impl-docs-restructure.md
related_specs:
  - "../adr/IADR-0208_ci-pr-latency-reduction.md"
  - "../adr/IADR-0049_integration-e2e-foundation.md"
---

# 仕様書: PR の CI 所要時間を実測起点で縮め、落とした精度を後段で担保する

> 起点 ID は**無採番 `NFR`**（場合 2・メタ作業。`.claude/rules/traceability.md`「起点 ID の種別」）。
> 稼働する製品の要件ではなく開発工程の性能であるため、計画側の非機能要件表に当たる番号が無い。
> **環流しない**。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 該当なし（CI・工程のメタ作業）
- ユースケース（UC）/ 画面（SC）: 該当なし
- 関連 ADR: 計画 `ADR-0029` 決定 6（kit との乖離は受容する）／
  実装 [IADR-0208](../adr/IADR-0208_ci-pr-latency-reduction.md)（本作業の判断）／
  実装 [IADR-0049](../adr/IADR-0049_integration-e2e-foundation.md)（統合テストの nightly 分離。本作業が倣った型）

## 背景 —— 推測ではなく実測

利用者から「PR の GitHub Actions が遅く作業が止まる」と指摘があった。
**着手前に実 run を測った**（2026-08-21・run `32443950165`）。

| ワークフロー | 所要 | 必須チェック |
| --- | --- | --- |
| **Claude Code Review** | **4分21秒** | `claude-review` ← **最長はここだった** |
| **CI** | **3分29秒** | `build-and-test` / `lint` / `commit-messages` |
| Security | 1分30秒 | `secret-scan` / `dependency-review` |

### 測って初めて分かったこと

**1 つ目。最長は CI ではなく AI レビューだった。** しかも `claude-review` は必須チェックである。
内訳の大半は「AI が動き出す前」の工具一式
（`setup-dotnet` → `dotnet tool install -g dotnet-ef` → `npm ci` →
`playwright install --with-deps chromium`）で、**4 つとも `continue-on-error: true`**、
**docs だけの PR でも全額を払っていた**。
プロンプト側は「`backend/` が変わっていなければビルドするな」と指示しているが、
**セットアップはその時点で既に払い終わっている**。指示では回収できない。

**2 つ目。「ジョブが多いから遅い」ではなかった。** CI の 21 ジョブは `needs:` を 1 つも持たず
全並列であり、16 個の軽量ジョブ（各 10〜15 秒）は待ち時間をほとんど作っていない。
CI のクリティカルパスは `build-and-test`（3分13秒。Restore 20s / Build 47s / **Test 111s**）1 本である。

ただし軽量ジョブは無害ではない。**同時実行スロットを 16 個占有**して本命の開始を遅らせるうえ、
**同じ 1147 個の `.cs` を 4 ジョブがそれぞれ個別に走査**していた。

**3 つ目。`setup-dotnet` は 6 箇所すべてが `cache:` 未設定**で、101 個の csproj を毎 run
コールドで restore していた。`frontend-e2e` の Playwright ブラウザ導入も毎回 31 秒掛かっていた。

## 変更内容

### 1. `concurrency` を追加（`ci.yml` / `security.yml` / `codeql.yml` / `helm.yml`）

持っていたのは `pr-title` / `pr-size` / `claude-*` / `integration` だけで、
**重いワークフローには 1 つも無かった**。
`cancel-in-progress` は式で分岐し、**`develop` / `main` への push は完走させる**
（後述 5 の担保がそこに載るため、途中でキャンセルしてはならない）。

### 2. 🔴 `claude-code-review.yml` の工具導入を「docs だけなら省く」で出し分ける（本作業で最大の効果）

前段に `git diff --name-only` の判定ステップを置き、4 つのセットアップを `if:` 条件付きにする。

> **［2026-08-22 追記］判定を逆向きに書き直した。**
> 当初は「`backend/` `frontend/` のパターンに当たったら入れる」（＝**当たらなければ省く**）だった。
> 利用者裁定「なるべく精度は落とさない方向で」に従い、
> **「入れる」を既定にし、docs だけだと確かめられたときにだけ省く**形へ改めた。
>
> **同じ取りこぼしでも倒れる先が違う。** 旧は「省いてしまう」（＝検証が落ちる）、
> 新は「余計に入れる」（＝**遅くなるだけ**）である。
> 差分を解決できなかった場合も入れる（fail-safe）。

🔴 **`--allowedTools` と `.claude/settings.json` は触っていない**（3 系統一致の規約。
`check-ai-workflow-config.js` が STRICT で見ている）。変えたのは**事前導入の有無だけ**である。
取りこぼすと AI が「実行したが失敗した」と報告し、原因が環境不備であることは本文から判らない ——
**拒否されるより質が悪い**（#391 の教訓）。

### 3. Node 検査ジョブ 15 個 → 1 個（`static-checks`）へ統合

🔴 **検査は 1 本も減らさない。** ジョブが減るだけで、走る検査器の本数と対象は不変である。

🔴 **失敗の可読性を落とさない。** 単純に連続ステップへ並べると**最初の失敗以降が走らず**、
1 回の CI で 1 件しか分からなくなって往復が増える。
各ステップへ `if: ${{ !cancelled() }}` を付け、落ちた検査が複数あれば一度に全部出るようにする。

🔴 **コメントを 1 行も捨てない。** 統合前のジョブのコメントは ADR 番号・事故の経緯・
「これを外すと検査が空回りする」という設計要点を持つ。統合先の各ステップ直前へ移す。

### 4. NuGet キャッシュ / Playwright ブラウザのキャッシュ

`packages.lock.json` を持たないため `setup-dotnet` の `cache: true` は使えない。
`actions/cache` を `~/.nuget/packages` と `~/.cache/ms-playwright` へ直接張る。

### 5.（取り下げ）CodeQL の `build-mode` と `vulnerable-scan` は PR のまま変えない

> **［2026-08-22 追記］利用者裁定が更新された。**「なるべく精度は落とさない方向で」。
> 当初あった 2 件の「PR から外す」変更を**取り下げた**。

- **CodeQL は常に `autobuild`。** `none` は生成コードを解析対象外にし、かつ
  「PR は緑だが push で赤」という読み分けの難しい状態を作る。
- **`vulnerable-scan` は PR に残す。** `dependency-review` は依存グラフの差分を見るのに対し、
  本ジョブは実際に restore した推移閉包を `--include-transitive` で見る。
  **見ている面が違う以上「重複だから外してよい」と言い切れない。** 迷ったら残す。

速さは `concurrency` と NuGet キャッシュで取る。どちらも**中身を 1 行も削らない**。
両ワークフローとも CI と並列に走り**クリティカルパスではない**。

### 6. changelog 自動 PR の空振りを止める

`automation/changelog-update-develop` の PR で CI が `action_required` のまま毎回起動していた
（実測 6 件以上）。重い 4 ジョブへ `if: !startsWith(github.head_ref, 'automation/')` を足す。

⚠️ **必須チェックのジョブへ `if:` を付けてよい根拠**: `if` でスキップされたジョブは
**必須チェック上「合格」として扱われる**（`docs/ai-workflow.md` に明記）。
`paths:` でワークフローごと起動しない場合と違い、恒久 pending にはならない。

### 7. 失敗時の自動 issue 起票

利用者裁定は 2 段階で入った。**後が前を狭めている。**

- **［08-21］**「毎 PR の精度は下がってもよいが、develop マージ時か日次実行でどこかで担保する。
  そこで失敗したら自動で issue を起票する」
- **［08-22］**「なるべく精度は落とさない方向で」

後者により **5 の「PR から外す」案は取り下げた**。
一方 **`ci-failure-issue.yml` は残す** —— 外すのをやめても、日次（`integration.yml`）・
週次（`codeql.yml` / `security.yml`）の実行は**誰も見ていない**ためである。
落ちたまま何日も気付かれない形を塞ぐ。

**`integration.yml` は今回落とすものではない**（IADR-0049 で既に nightly 分離済み）が、
**自動起票が無く赤が黙って積み上がる形**だったので同じ仕組みを入れ、
`push: [develop]`（マージ時の担保）も足した。同型の穴を 2 つ残さない。

## 受け入れ基準

1. 必須 check 名（`build-and-test` / `lint` / `commit-messages` / `pr-title` /
   `secret-scan` / `dependency-review` / `claude-review`）が**1 つも消えていない**。
2. 走る検査器の本数と対象が統合前後で**一致する**。
3. NuGet / Playwright キャッシュが 2 回目の run で**ヒットする**。
4. CodeQL が **PR / push の両経路**で `autobuild`（＝従来の精度）で走る。
5. `claude-review` が docs のみの PR で 4 セットアップをスキップし、
   **それ以外のあらゆる PR では実行する**（許可リスト方式の倒れ方を実測する）。
6. `report-failure` が **① 失敗時に issue を立て ② 2 回目はコメントを足すだけ**であり、
   **PR では起票しない**。

## 検証（実測でしか確かめない）

- ジョブ単位の `started_at` / `completed_at` を変更前後で突合する（run 全体だけを見ない）。
- キャッシュは**2 回 push して**比較する（初回は必ずミスするため）。
- 統合ジョブは**変異試験**で確かめる: 1 検査を故意に失敗させ、
  ① ジョブが赤くなり ② **後続の検査も走り切って全失敗が一度に出る**ことを実測する。
- 自動起票は `integration.yml` の `workflow_dispatch` 入力 `force_failure` で
  **実際に 2 回走らせて**① 立つ ② 重複しない を実測する。
  **平時は常に skip で緑になる仕組みなので、実測しない限り壊れていることに永久に気付けない。**
- 🔴 `claude-review` は **docs のみの PR と backend を触る PR の両方**を作って実測する。
  とくに**後者で dotnet が入らないまま AI がビルドを試みて失敗する形**（判定条件の取りこぼし）を
  最も警戒する。

## 母集合の取り方（規則 6: 引いた結果と除外の理由を残す）

- **`concurrency` の有無**: `grep -l "concurrency:" .github/workflows/*.yml` で全 13 本を走査し、
  持っていたのは `backlog-audit` / `claude-code-review` / `claude-coding` / `integration` /
  `pr-size` / `pr-title` の 6 本と確定した。残る 7 本のうち、PR 経路を持つ
  `ci` / `security` / `codeql` / `helm` の 4 本へ足した。
  **除外**: `changelog` / `openapi` / `copilot-setup-steps` は PR のレイテンシに関わらないため
  （push 専用、または自ファイル変更時のみ）。
- **`setup-dotnet` の `cache:` 未設定**: `grep -rn "setup-dotnet" .github/workflows/` で 6 箇所、
  うち `cache:` を持つものは 0 件と確定した。
- **統合対象のジョブ**: `ci.yml` の 21 ジョブを全数列挙し、必須チェック（`commit-messages`）・
  重量級（`scripts-tests`）・dotnet 依存（`lint` / `build-and-test`）・
  Node 依存（`frontend` / `frontend-e2e`）を除いた 15 本を統合対象とした。
  **除外の理由は上記のとおりで、黙って落としたものは無い。**
- **検査器呼び出しの不変**: 統合前後の `ci.yml` から
  `node scripts/*.js` / `bash *.sh` の呼び出しを全抽出して `diff` を取り、**完全一致**を実測した。

## 未決事項

- なし（`helm.yml` の約 40 回の直列 `helm template` は `paths: deploy/helm/**` 持ちで
  ほとんどの PR では起動しないため、本作業では `concurrency` の追加のみに留め、
  レンダリングの整理は別 issue へ切り出す）。
