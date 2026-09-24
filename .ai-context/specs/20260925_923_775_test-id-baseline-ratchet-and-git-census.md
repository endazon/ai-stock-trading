---
title: テスト ID 重複 baseline に「増える側」のラチェットを足し、樹形 census を git の追跡パスから取る
type: spec
status: accepted
related_ids: [NFR, IADR-0376, IADR-0258]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs: []
---

# テスト ID 重複 baseline の増加ラチェット ＋ 樹形 census の git 化（#923 / #775）

同じファイル `scripts/check-test-traceability.js` を触る同型の検査器改修 2 件を 1 PR にまとめる。

## 1. #923 — baseline に「増える側」のラチェットが無い

### 症状（issue 本文。PR #912 の監査の実測）

`scripts/test-id-duplicate-baseline.json` のヘッダは「新しい重複を足すことは許さず」と書くが、
新しい重複 `T-10-633` を注入して赤にしたあと、baseline へ 1 件足すと緑になる。**減る側（解消した重複を
残す）だけが強制されていた。**

### 決定

- 検査 T2 に **T2b（baseline の増加ラチェット）** を足す。比較の基準は
  **マージベース**（`<基準>...HEAD` の 3 ドット。`check-adr-index-addendum-loss.js` と同じ優先順:
  `--dup-baseline-range=` → `COMMIT_RANGE` → `GITHUB_BASE_REF` → `origin/develop` → `develop`）。
- 「増えた」の定義: HEAD の baseline に在ってマージベースの版に無い ID、または件数（`count`）が増えた
  ID、または在り処（`files`）に新しいファイルが加わった ID。
- 正当な増加（並行レーンの採番衝突が develop 上で起き、既存 ID を改番しないと決めた場合など）は、
  **範囲内のコミット本文に行単独で** `[add-test-id-duplicate] T-10-633` と ID ごとに宣言する
  （`check-adr-index-addendum-loss.js` の `[remove-adr-addendum]` と同じ作法。全体スキップは用意しない。
  本文中の言及では発動しない＝トリムした行の完全一致のみ）。宣言は PR のレビューで人が承認する。
- **基準が取れないとき**（浅いクローン・git の無い一時ツリー・基準 ref 不在）:
  - **CI の pull_request 実行**（`GITHUB_EVENT_NAME=pull_request` かつ `TEST_TRACE_ROOT` 未指定）では
    **赤にする（fail-loud）**。`static-checks` は `fetch-depth: 0` なので通常は取れる。取れないのは
    ワークフローの退行であり、黙って緑にしない。
  - それ以外（ローカル・push 実行・模擬ツリー）は **理由つきの notice を出して skip** する。

### CI での基準の取り方（ci.yml を読んで確認）

- `Check test traceability` は `static-checks` ジョブ内で走る。同ジョブの `actions/checkout` は
  **`fetch-depth: 0`**（`check-action-versions` の `--compare-with-ref` と `adr-index-sync` のため）。
  全ブランチが `refs/remotes/origin/*` に入るので `origin/develop` が解決できる。
- pull_request では HEAD は `refs/pull/N/merge`（マージコミット）であり、
  `merge-base(origin/<base>, HEAD)` は **base ブランチの先端**になる。範囲 `base..HEAD` は
  PR のコミットとマージコミットだけを含む。
- `GITHUB_BASE_REF` は Actions が pull_request で自動設定する既定の環境変数（ジョブで `env:` を書く必要は無い）。
- push（develop / main）では `GITHUB_BASE_REF` が空で `origin/develop...HEAD` の基準は HEAD 自身になり、
  差分 0 件で OK になる（push 後の比較はしない。PR で止める検査である）。

### 規則 11（窓の両側のプローブ）— 3 通りの形の表

窓 = 「PR が分岐してから、比較する時点まで」に develop 側で baseline が動くこと。
プローブ（すべて `scripts.repo.test.js` に実 git の一時リポジトリで書いた）:

- **増える側 P+**: PR 自身が baseline へ entry を 1 件足す（宣言なし）。→ 期待: 赤
- **増える側（宣言あり）P+d**: 同上で、コミット本文に `[add-test-id-duplicate] <ID>` を宣言。→ 期待: 緑
- **減る側 P−**: PR の分岐後に develop が baseline から entry を 1 件消した（解消済み）。PR は rebase していない
  （PR 側の baseline にはその entry が残る）。→ 期待: T2b は緑（増加ではない。残った entry は既存の T2
  「解消しています」側の責務）
- **2 コミットの PR P+2**: 1 コミット目で entry を足し、2 コミット目は無関係の変更。→ 期待: 赤

| 形（基準の取り方） | P+ | P+d | P− | P+2 |
| --- | --- | --- | --- | --- |
| 2 ドット: 基準 = `develop` の先端 | 赤 ✓ | 緑 ✓ | **赤 ✗**（develop が消した entry を PR が「足した」と誤読） | 赤 ✓ |
| `HEAD^1`（直前コミット） | 赤 ✓ | 緑 ✓ | 緑 ✓ | **緑 ✗**（1 コミット目の追加を見逃す） |
| **3 ドット: 基準 = マージベース（採用）** | **赤 ✓** | **緑 ✓** | **緑 ✓** | **赤 ✓** |

（CI のマージコミット上ではマージベース ＝ develop の先端 ＝ `HEAD^1` なので 3 形は一致する。
差が出るのはローカルの未 rebase ブランチである。）2 ドットと `HEAD^1` の欠陥は自己テストの中で
`--dup-baseline-range=develop..HEAD` / `HEAD~1...HEAD` を渡して実測する。

## 2. #775 — 樹形 census を git の追跡パスで取る

### 症状（issue 本文。2026-09-11・Windows の作業ツリー）

大文字小文字を区別しない FS で、旧樹形の `tests/` ディレクトリが残ったまま新樹形 `Tests/` のファイルが
その中へ置かれると、`fs.readdirSync` の実名（`tests`）で数える census が「旧 334 / 新 142」と嘘をつく
（git の追跡名では旧 0 / 新 494）。

### 決定

- `testFiles()` と `serviceTestDirs()` の母集合を **`git ls-files -z --cached -- backend`**（インデックス
  ＝追跡パス）から取る。判定規則（`tests` / `*.Tests` / `backend/Services/<Svc>/Tests`・`bin`/`obj` の除外）
  は従来のディレクトリ走査と同じものをパス要素へ適用する。
- git が使えない（`root` が作業ツリーの最上位でない＝模擬ツリー・git 不在・コマンド失敗）ときだけ
  従来の `fs` 走査へ縮退し、**出力に census の出典（`git ls-files` / `fs 走査（縮退・理由）`）を書く**。
- `--others`（未追跡）は含めない。issue の指示どおり「追跡パス」に限定する。ローカルで `git add` 前の
  新規テストは census に入らないが、CI（コミット済み）では必ず入る。
- 空ディレクトリは git に載らないため、git 由来の census ではファイルを 1 件以上持つディレクトリだけが
  「樹形の実在」になる（`.csproj` だけで `.cs` 0 件なら T1 が従来どおり赤）。

### 陰性対照

一時 git リポジトリで `backend/Services/A/Tests/X.cs` をコミットし、作業ツリー上の `Tests` を小文字
`tests` へ（git mv を使わずに）改名しても、census（`serviceTestDirs` / `serviceTestLayoutCounts`）が
`{ old: 0, new: 1 }` のまま変わらないこと。

## 3. 母集合（規則 9・10。2026-09-25・`origin/develop` = `69d098c`）

`git grep -n -I "test-id-duplicate-baseline"` と `git grep -n -I "serviceTestDirs\|実エントリ名"` の生出力から:

| ファイル | 扱い |
| --- | --- |
| `scripts/check-test-traceability.js` | 本体。改修する |
| `scripts/test-id-duplicate-baseline.json` | `$comment` のラチェット説明に「増やすには宣言が要る」を足す（件数・entry は触らない） |
| `scripts/scripts.repo.test.js` | 自己テストを足す（T2b の 4 プローブ＋形の比較、census の git 化の陰性対照） |
| `scripts/README.md` | `test-traceability` 行に T2b を、T1 のコラムに census の出典を追記する |
| `docs/tests/README.md` | 「既に重複している番号は改番しない」の項に、baseline へ足すときの宣言を追記する |
| `.ai-context/adr/IADR-0376_…` | 日付つき追記（決定 2 に T2b を足す。残余リスク 1 の部分解消） |
| `.ai-context/adr/IADR-0258_…` | 日付つき追記（T1 の census の出典を git へ） |
| `.ai-context/adr/README.md` | 上の 2 IADR の索引行に追記 |
| `.github/workflows/ci.yml` | `Check test traceability` のコメントに `fetch-depth: 0` への依存を追記する |
| `.ai-context/specs/20260923_887_…`・`20260911_757_…`・`20260828_w9f3_…`・`20260903_613_…` | **除外**（凍結記録） |
| `scripts/check-consumer-endpoint-names.js` | **除外**。issue は「同じ」と触れるが、同検査器の樹形判定は `src` の有無（小文字のみ）と `tests` の大小無視比較であり、`tests`/`Tests` の食い違いで数が変わる経路を持たない |

規則 10: 本 PR で baseline の件数（24）は変えない。`README` 類の件数表記は増やさない（導出値を新たに書かない）。

## 4. 受け入れ基準

- [x] baseline に entry を足しただけの PR は T2b で赤になる（宣言なし）。
- [x] コミット本文の行単独の `[add-test-id-duplicate] <ID>` 宣言があれば緑になり、宣言した旨を出力する。
- [x] 減る側（develop が分岐後に消した entry が PR 側に残る）で T2b は誤発火しない。
- [x] CI の pull_request で基準が取れないと赤、それ以外は理由つき skip。
- [x] census は git の追跡パスから取り、作業ツリーの大文字小文字の食い違いで変わらない（陰性対照）。
- [x] develop の現状で `node scripts/check-test-traceability.js` が OK のまま（件数は従来の fs 走査と一致）。
