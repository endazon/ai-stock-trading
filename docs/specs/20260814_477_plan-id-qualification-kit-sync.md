---
title: 計画 ID 修飾の検査器を kit から取り込み、トレーサビリティ規約を kit へ揃える（#477 の残り 3 項目）
type: spec
status: approved
related_ids: [NFR, IADR-0047, IADR-0188, IADR-0189]
author: endazon (with Claude Code)
created: 2026-08-14
updated: 2026-08-14
---

# 仕様書: 計画 ID 修飾の検査器とトレーサビリティ規約の kit 同期

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#477](https://github.com/endazon/ai-stock-trading/issues/477)（kit 追随の棚卸し）の**残り 3 項目**
- 起点 ID: **NFR**（運用保守。工程の統制であり、計画側の非機能要件表に当たる番号が無いため無採番とする。planning#311 の 2 に該当）
- 先行作業: [IADR-0188](../adr/IADR-0188_feedback-vocabulary-and-dispatch-kit-sync.md)（環流語彙の項。PR #486 でマージ済み）
- 根拠: [IADR-0047](../adr/IADR-0047_kit-template-sync-policy.md) 決定1「kit テンプレート更新への追随を原則とする」

## 実装の現状（実測 2026-08-14・`develop` = `32ff861`・計画 pin `cff0e7b`）

#477 が挙げる残り 3 項目を 1 件ずつ実測した。**3 項目は難易度がまったく違った。**

| # | #477 の項目 | 実測 |
| --- | --- | --- |
| 1 | `.claude/rules/traceability.md` の companion 機構 | **本リポ固有の内容は 0 件**。kit との差分 3 行はすべて**kit の旧版**であった。**companion は不要** |
| 2 | `check-plan-id-qualification.js` 新設 | **未導入。** 依存する `check-cross-repo-refs.js` も未導入。**違反 46 件** |
| 3 | `claude-code-review.example.yml` の `reopened`・必須チェック名 | **`reopened` は導入済み**（IADR-0185 決定3）。差分はコメントと**本リポ固有のセットアップ**のみ |

### 1: `traceability.md` に固有の内容は無かった

kit の companion 機構は「配布物を直接編集せず、固有規約は `traceability.repo.md` へ書く」というものである。
本リポの差分を実測したところ、**固有の追加は 1 行も無く**、次の 3 行がいずれも**kit の旧版**であった。

| 本リポ（旧） | kit（新） |
| --- | --- |
| `- \`NFR\`: 非機能要件` | `NFR-xx` の個別 ID 参照と、無採番を許す 2 例外（planning#311） |
| 起点 ID の書式に `NFR` | `NFR-\d+`（計画側が ID 列を持たない場合に限り `NFR`） |
| bot 除外を `user.type == 'Bot'` で行う | **スクリプト側の `BOT_AUTHORS` 完全一致**で行う（planning#202。GitHub App の PR まで除外され「最後の砦」が skipped になる） |

**したがって kit 版をバイト一致で取り込めば足り、companion ファイルは作らない**
（kit 自身が「companion が無くても壊れない。未作成なら固有規約が無いだけ」と定めている）。

> `check-commit-messages.js` は既に `NFR(?:-\w+)?` を受理するため、**書式の更新に伴う変更は不要**である（実測）。

### 2: 検査器は 2 本セットで、違反は 46 件あった

`check-plan-id-qualification.js` は **`check-cross-repo-refs.js` を実行時に `require` する**
（`maskCode` を借りている）。**片方だけ入れると動かない。**

**両者は対象が違う** —— 前者は**計画 ID / ADR ID**（`AST/FR-17`）、後者は **issue / PR 番号**（`AST#24`）を見る。

置換点 `PROJECT_PREFIXES` の実測:

| 設定 | 違反 |
| --- | ---: |
| `MSP` のみ | **1** |
| **`MSP,AST`** | **46** |

45 件の差は `AST IADR-0164`（空白区切り）の形である。**本リポは `AST/` を自プロジェクトの修飾として実際に使っており**
（`AST/FR-17` 24 件・`AST/SC-02` 6 件 ほか）、**同じ文書内で `/` と空白が混在していた**。

### 3: `claude-code-review.yml` は既に条件を満たしていた

`reopened` は導入済みである（本リポは IADR-0185 決定3、kit は planning#313 と、**別経路で同じ結論に達していた**）。
残る差分は **kit 側のコメントの厚み**と、**本リポ固有のセットアップ**（`dotnet-ef` 導入・frontend の
`npm ci`・Playwright ブラウザ取得・`sed` の許可）である。いずれも根拠付きの意図的な追加であり、
**このファイルはバイト一致の対象ではない**。

## 決定（[IADR-0189](../adr/IADR-0189_plan-id-qualification-and-traceability-kit-sync.md)）

### 決定1: `traceability.md` は kit 版をバイト一致で取り込み、companion は作らない

固有の内容が 0 件であることを実測で確かめたため、**失うものが無い**。

### 決定2: 検査器 2 本をバイト一致で取り込み、置換点は**環境変数で与える**

kit は「配布時に置換点を書き換えること」と書くが、**両スクリプトとも環境変数での上書きに対応している**。
**環境変数を使えばファイルをバイト一致のまま保てる**ため、次の kit 更新がそのまま届く（IADR-0047 決定1 の趣旨）。

- `PLAN_ID_PREFIXES=MSP,AST`
- `CROSS_REPO_NAMES` / `CROSS_REPO_SELF_NAMES` は**本作業では設定しない**（決定4）

### 決定3: `PROJECT_PREFIXES` に **`AST` も含める**

kit の定義は「**他**プロジェクトの短縮名」であり、字面どおりなら `MSP` だけである。しかし**本リポは
`AST/` を自プロジェクトの修飾として実際に使っている**（複数プロジェクトの ID が同じ文書に並ぶため）。

**修飾を使うと決めた以上、その表記は一貫していなければ機械的突合の役に立たない。**
`AST/IADR-0164` と `AST IADR-0164` が混在する状態は、修飾を導入した目的（誤帰属の防止）を損なう。

**46 件を `<PROJ>/<ID>` へ揃える。** 対象はコード内コメント・`.csproj` のコメント・文書であり、
**振る舞いは変わらない**。

### 決定4: `check-cross-repo-refs.js` は**入れるが CI へは配線しない**

**依存として必要**（決定2）だが、**実ツリーには 269 件の違反がある**（実測）。

| 型 | 件数 |
| --- | ---: |
| 長い表記（`project-planning#220` → `planning#220`） | 213 |
| 空白区切りの修飾（`MSP #710`） | 44 |
| 列挙形の修飾漏れ | 12 |

**これは追随ではなく規約の決定である。** 本リポの規約は
「**短縮形とフルパス形式のどちらに寄せるかを最初に決め、混在させない**」と書くが、
**本リポはその決定をしていない** —— 実測の分布は `project-planning#N` 288 件 / `planning#N` 241 件で拮抗している。

さらに**違反の 24 件は `feedback/`、244 件は `docs/`** にあり、その多くが
**作業仕様書・環流記録＝point-in-time の記録**である。kit 自身が
「後から表記だけ直すと当時の記述と食い違う」として、姉妹検査器ではこれらを除外している。

**よって本作業では配線しない。** 規約の決定と母集合の引き方を含めて**別 issue（[#487](https://github.com/endazon/ai-stock-trading/issues/487)）とする**。
**配線しないことを記録に残す**（黙って入れないと「入れ忘れ」と区別がつかない）。

### 決定7: `pr-title.yml` の bot 除外を**スクリプト側へ移す**

規約を取り込んだ以上、**実装をそれに合わせる**（文面を実装に合わせて緩めるのではない）。

| | 変更前 | 変更後 |
| --- | --- | --- |
| 判定の場所 | ワークフローの `if: user.type != 'Bot'` | **スクリプト（`isBotLogin()`）** |
| 判定の対象 | PR 作成者の**種別** | PR 作成者の**ログイン名**（`PR_AUTHOR`） |
| 照合 | —— | **`BOT_AUTHORS` への完全一致** |
| `claude[bot]` の PR | **skipped**（検査されない） | **検査される** |

**完全一致にする**（コミット著者向けの `isBot()` は部分一致だが、ログイン名で部分一致にすると
`not-dependabot-really` のような無関係な名前まで除外され得る）。**`PR_AUTHOR` が空なら bot 扱いしない**（fail-closed 側）。

### 決定5: `claude-code-review.yml` は変更しない

条件は既に満たしている（`reopened` 導入済み）。kit のコメントを取り込むと
**本リポ固有のセットアップ手順を落とす**ことになり、#391 で塞いだ穴（許可だけ足してセットアップを入れず
「実行したが失敗した」と報告される形）が戻る。

## やらないこと

- **companion ファイル（`traceability.repo.md`）の作成**（固有の内容が無いため）。
- **`check-cross-repo-refs.js` の CI 配線と 269 件の是正**（決定4。別 issue [#487](https://github.com/endazon/ai-stock-trading/issues/487)）。
- **`claude-code-review.yml` の変更**（決定5）。
- **point-in-time 記録（`docs/specs/` / `feedback/`）の表記の書き換え**。

## 受け入れ基準

- [x] `.claude/rules/traceability.md` が kit と**バイト一致**である
- [x] `scripts/check-plan-id-qualification.js` / `scripts/check-cross-repo-refs.js` が kit と**バイト一致**である
- [x] 両スクリプトの `--self-test`（69 件 / 38 件） が通る
- [x] `PLAN_ID_PREFIXES=MSP,AST` での違反が **0 件**である（1718 件走査。着手時 46 件）
- [x] CI（`ci.yml`）が `check-plan-id-qualification.js` を**自己試験つき**で走らせる
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る（**206 件**）
- [x] `dotnet build backend/backend.slnx` が警告 0 で通る（コメントのみの変更であることの確認）
- [x] `node scripts/check-doc-links.js` が通る（458 件 OK）
- [x] **`claude[bot]` の PR が PR タイトル検査を skip しない**（決定7。実測 exit=1）
- [x] **`dependabot[bot]` は除外される**（実測 exit=0）／**部分一致では誤除外しない**（実測 exit=1）

## テスト方針

**検査器の判定は kit の自己試験（69 件 / 38 件）に委ね、本リポで二重に書かない**（IADR-0188 決定5 と同じ規律）。

**本リポが独自に守るのは「実ツリーに違反が無いこと」だけ**とし、`scripts.repo.test.js` へ回帰テストを 1 件置く。

> **CI ジョブだけでは足りない。** ジョブは `PLAN_ID_PREFIXES` を渡して走らせるが、
> **その環境変数を落とすと `PROJECT_PREFIXES` が空になり検査は skip して緑になる**（fail-open）。
> 回帰テストは**環境変数を明示的に与えて**呼び、skip では緑にならないようにする。

## 対照実験（実走した実測）

| # | 壊した箇所 | CI ジョブ相当 | 回帰テスト | 予測 |
| ---: | --- | --- | --- | --- |
| 1 | コメント 1 件を空白区切りへ戻す | — | **赤**（違反 1 件） | 一致 |
| 2 | CI から `PLAN_ID_PREFIXES` を落とす（ツリーは無傷） | 緑（skip） | 緑 | **予測の書き方が誤り**（下記） |
| 3 | **1 と 2 を同時に**（違反あり × env 無し＝fail-open の本番相当） | **緑（skip して素通り）** | **赤**（違反 1 件） | 一致 |

> 🔴 **実験 2 の予測を「回帰テストは赤のまま」と書いたのは誤りであった。** ツリーが無傷なら
> 捕まえる違反が無いのだから、どちらも緑になるのが正しい。**確かめるべきは 2 単独ではなく
> 実験 3（違反 × env 無し）である** —— これが fail-open の本番相当の形である。
> **実験 3 では CI ジョブ相当が緑で素通りし、回帰テストだけが赤くなった。** 穴は塞がっている。

### 決定7（bot 除外）の対照実験

| # | 壊した箇所 | 結果 |
| ---: | --- | --- |
| 4 | `isBotLogin` を**部分一致**へ戻す | **赤** |
| 5 | `claude[bot]` を `BOT_AUTHORS` へ足す（**穴の再現**） | **赤** |

**実挙動**（規約違反タイトルを各作成者で走らせた実測）: `claude[bot]` → **exit 1（検査される）**／
`dependabot[bot]` → exit 0（除外）／`not-dependabot-really` → **exit 1（誤除外しない）**／未設定 → **exit 1**。

### 副産物: 検査器が本作業自身のコメントを捕まえた

CI ジョブのコメントに**違反の実例を平文で書いてしまい**、自分の検査に捕まった。
規約は「意図的な誤例はインラインコードへ入れる」と定めるが、**その除外は Markdown でしか効かない**
（YAML のバッククォートはコード記法ではないため `maskCode` が外さない）。
**誤例を書かない形へ改め、その旨をコメントに残した。**
