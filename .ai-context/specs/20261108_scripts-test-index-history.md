---
title: 手元でだけ赤の「過去の索引追随漏れ 2 件」の原因究明と是正（旧配置 docs/adr/ の偽陰性・CI に無い回帰の素材）
type: spec
status: accepted
related_ids: [NFR, IADR-0648, IADR-0193, IADR-0363]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
---

# 作業仕様書: scripts.test.js の「過去の索引追随漏れ 2 件」（NFR）

> 着手 2026-11-08（develop `0dcdf51ab`）。ブランチ `fix/NFR-scripts-test-index-history`。

## 起点

- `node scripts/scripts.test.js` が、全エージェントの手元で 1 件だけ赤:
  `過去の索引追随漏れ 2 件を検出できる（#491 / #495 の実コミットで回帰）`。develop の CI は緑。
- 「手元は浅いクローンで、テストが全履歴に依存している」という推測があったが、誰も確かめていなかった。
- 起点 ID: 工程の統制（無採番 NFR。#497 / IADR-0193 と同じ扱い）。

## 再現の実測（着手前）

```
$ git rev-parse --is-shallow-repository        # 手元・worktree とも
false
$ git log --oneline | tail -1                  # 根のコミットまで辿れる
fc103e42b Initial commit
$ node scripts/scripts.test.js
  ✗ 過去の索引追随漏れ 2 件を検出できる（#491 / #495 の実コミットで回帰）
      AssertionError [ERR_ASSERTION]: 270a7c1^..270a7c1 は索引追随漏れとして赤になるべきだが exit=0 だった
✗ 1 tests failed
```

テストは `scripts/scripts.repo.test.js`、検査器は `scripts/check-adr-index-sync.js`。報告された 2 件:

| 範囲 | 事故 | 変更したファイル |
| --- | --- | --- |
| `270a7c1^..270a7c1` | PR #491（IADR-0190 本文を更新・索引は据え置き） | `docs/adr/IADR-0190_review-verdict-gate.md` ほか |
| `4788905..a39e3e7` | PR #495（IADR-0191 本文を訂正・索引は据え置き） | `docs/adr/IADR-0191_kit-sync-classification.md` ほか |

```
$ COMMIT_RANGE=270a7c1^..270a7c1 node scripts/check-adr-index-sync.js
[check-adr-index-sync] OK: 本 PR は実装ADR の本文を変更していません。      # exit=0
$ COMMIT_RANGE=4788905..a39e3e7 node scripts/check-adr-index-sync.js
[check-adr-index-sync] OK: 本 PR は実装ADR の本文を変更していません。      # exit=0
```

## 原因

### 1. 手元で赤になる理由: 資料再編で検査器が旧配置を見なくなった（偽陰性）

- 素材のコミットは IADR が `docs/adr/` に在った頃（2026-08-14）のもの。資料再編（#536・`9b9c67636`・2026-08-21）が
  `INDEX_PATH` と `ADR_RE` を `.ai-context/adr/` へ書き換え（`git log -S"docs/adr/README.md" -- scripts/check-adr-index-sync.js`
  の該当は 889e41fd8〔導入〕と 9b9c67636 だけ）、再編前の範囲では IADR を 1 件も拾えなくなった。
- 再編前の版（`git show 9b9c676^:scripts/check-adr-index-sync.js`）を同じ範囲へ当てた実測:

```
270a7c1^..270a7c1 exit=1   （IADR-0190 … docs/adr/README.md の索引行を変更していない）
4788905..a39e3e7  exit=1   （IADR-0191 …）
270a7c1..04126d8  exit=0   （是正範囲。索引行も変更されている）
```

→ 2 件は**実在する過去の索引追随漏れ**であり（当時 #491 / #495 の後続コミットで是正済み）、現在の索引・文書の漏れではない。
壊れていたのは検査器の側（再編前の範囲での偽陰性）。

### 2. CI で緑になる理由: 素材が CI に存在せず、誤った理由で skip していた

- CI の scripts-tests は `fetch-depth: 0`・`REQUIRE_REPO_TESTS: "1"` で `node scripts/scripts.test.js` を走らせる（浅くない）。
- しかし素材は develop から辿れない:

```
$ for r in 270a7c1 4788905 a39e3e7 04126d8; do git merge-base --is-ancestor $r origin/develop || echo "$r NOT on develop"; done
270a7c1 NOT on develop / 4788905 NOT on develop / a39e3e7 NOT on develop / 04126d8 NOT on develop
$ git log origin/develop --format='%h %s' | grep -E '#491\)|#495\)'
0371c30ae chore(NFR,IADR-0191): … (#495)
792a8f215 fix(NFR,IADR-0190): … (#491)       # スカッシュマージ
$ git ls-remote origin 'refs/pull/491/*' 'refs/pull/495/*'
04126d829…  refs/pull/491/head
21665f0c6…  refs/pull/495/head                # 4 コミットとも、この 2 つの head の祖先（merge-base --is-ancestor で確認）
```

- `actions/checkout` はブランチとタグしか取らないので CI には素材が無い。develop の run 42448659197（job 128632938791）のログ:

```
543: （履歴が浅いため過去コミットでの回帰は skip した）
544:   ok  過去の索引追随漏れ 2 件を検出できる（#491 / #495 の実コミットで回帰）
636:       (skip) 67628182..c1023d74 が履歴に無い
640:       (skip) be97cf2b / e6f58d1e が履歴に無い
642:       (skip) be97cf2b / e02b4229 が履歴に無い
791: ✓ 634 tests passed
```

- 手元のクローンは素材を**どの参照からも辿れないオブジェクト**として持っている（`git for-each-ref --contains 270a7c1` は空）。
  そのため手元でだけテストが走り、原因 1 で赤になる。
- まとめると「浅いから」ではなく、**「CI は素材が無く誤った理由で skip・手元は素材があり偽陰性で赤」**だった。

## 母集合（規則 9・10）

- 誤りの側の文字列で走査: `git grep -e "履歴が浅いため過去コミット" -e "実コミットで回帰" -e "履歴が浅い環境では skip" -e "当該ブランチ未取得"`
  → `scripts/scripts.repo.test.js`（2769・2770・2813・4184 行）と `.ai-context/adr/IADR-0193_adr-index-sync-check.md:85`。
  IADR-0193 の索引行の残余リスク「浅いクローンでは skip する」も同じ前提（README 255 行）。
- 同じ仕組み（develop から辿れない素材で CI が skip）の実データ回帰を全件洗い出した:
  `check-adr-index-addendum-loss` の事故 1（`67628182..c1023d74`＝PR #830）・BLK-1（`be97cf2b`＝#878・`e6f58d1e`＝#877・`e02b4229`＝#830）。
  事故 2・3（`13e8e19f..c53876d4`・`bdff6736..1cf92ed8`）は develop 上にあり CI でも走っていた。PR の対応は
  `gh api repos/endazon/ai-stock-trading/commits/<sha>/pulls` で引いた。
- 規則 10（是正で新たに誤りになる自分の記述）: 検査器の出力文言は `.ai-context/adr/README.md` 固定だったが、旧配置では
  `docs/adr/README.md` と突き合わせるため「同じ配置の索引（README.md）」へ改めた。`ci.yml` scripts-tests の
  「浅いクローンでは当該テストが best-effort で skip」は他のテストについて依然正しいので残した。
- 除外: `.ai-context/specs/` の確定済み記述（point-in-time の記録）。`scripts/README.md` は本検査器の配置に触れていないため変更不要。
- 並走: `fix/NFR-iadr-0292-citation`（IADR-0292 の引用是正）は origin に未 push で突合できなかった。開いている PR の変更ファイル
  （REST で全件取得）に本変更の 5 ファイル・IADR-0648 以降と重なるものは無い。

## 直し方（IADR-0648）

1. `check-adr-index-sync.js`: 配置を `ADR_LAYOUTS`（`.ai-context/adr`・旧 `docs/adr`）に列挙し、IADR は**同じ配置の索引の行**で突き合わせる。
   自己試験へ 5 件追加（旧配置で赤／旧配置＋旧索引で緑／旧配置の IADR を新索引で満たさない／逆も満たさない／git 経路で配置ごとに読む）。
2. `ci.yml` scripts-tests: `refs/pull/{491,495,830,877,878}/head` を取得するステップを足し、`REQUIRE_HISTORY_FIXTURES: "1"` を渡す。
3. `scripts.repo.test.js`: `HISTORY_FIXTURE_PRS`（単一情報源）と `historyFixtureMissing()` を置き、3 群の実データ回帰の skip を置き換える
   （`REQUIRE_HISTORY_FIXTURES=1` なら赤、未設定なら理由と取得コマンドを出して skip）。陰性対照 1 件と、ci.yml の配線テスト 1 件を追加。

## 受け入れ基準

- [x] 手元（素材あり）で `node scripts/scripts.test.js` が全件緑。
- [x] CI 相当（素材なし）＋`REQUIRE_HISTORY_FIXTURES=1` では素材の欠落が赤（黙って緑にしない）。
- [x] CI 相当で PR head を取得すると実データ回帰がすべて走って緑。
- [x] 是正前の検査器へ戻すと、素材ありの実データ回帰が赤（テストが効いている）。
- [x] 自己試験・配線テストに陰性対照がある。

## 検証（実測）

```
$ node scripts/check-adr-index-sync.js --self-test
[check-adr-index-sync] 自己試験 24 件 all passed。
$ COMMIT_RANGE=270a7c1^..270a7c1 node scripts/check-adr-index-sync.js   → exit=1（IADR-0190・突き合わせた索引: docs/adr/README.md）
$ COMMIT_RANGE=4788905..a39e3e7 node scripts/check-adr-index-sync.js    → exit=1（IADR-0191）
$ COMMIT_RANGE=270a7c1..04126d8 node scripts/check-adr-index-sync.js    → exit=0
$ COMMIT_RANGE=9b9c676^..9b9c676 node scripts/check-adr-index-sync.js   → exit=0（再編のコミット自体: 208 件すべて索引行あり）
$ REQUIRE_REPO_TESTS=1 REQUIRE_HISTORY_FIXTURES=1 node scripts/scripts.test.js   （手元 worktree）
✓ 636 tests passed
```

CI 相当のシミュレーション（`git clone --no-local --branch develop https://github.com/endazon/ai-stock-trading.git`＝参照から辿れるオブジェクトだけ。
`--is-shallow-repository`＝false、`git cat-file -t 270a7c1` は `Not a valid object name`）へ本変更の 3 ファイルを写して:

```
# 素材なし・REQUIRE_HISTORY_FIXTURES=1
  ✗ 過去の索引追随漏れ 2 件を検出できる（…）  Error: REQUIRE_HISTORY_FIXTURES=1 なのに 270a7c1^..270a7c1 が手元のオブジェクトに無い（…）
  ✗ check-adr-index-addendum-loss: 事故 1（…）  Error: … 67628182..c1023d74 …
  ✗ check-adr-index-addendum-loss[BLK-1 実データ]: #877 … Error: … be97cf2b / e6f58d1e …
# 素材なし・未設定 → (skip) を 6 行出して ✓ 636 tests passed
# ci.yml と同じ git fetch の後・REQUIRE_HISTORY_FIXTURES=1
  ok  過去の索引追随漏れ 2 件を検出できる / ok 事故 1〜3 / ok BLK-1 #877・#830
✓ 636 tests passed
# 同じ状態で検査器だけ develop の版へ戻す
  ✗ 過去の索引追随漏れ 2 件を検出できる（…）
✗ 1 tests failed
```

push 前の検査の結果は PR 本文に記す。
