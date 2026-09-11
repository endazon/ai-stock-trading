---
title: check-test-traceability.js の T1 が大文字小文字を区別しない FS（Windows）で Tests/ を旧樹形 tests/ と誤認する事象の是正
type: spec
status: done
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs: []
---

# 仕様書: check-test-traceability.js T1 の大文字小文字誤検出の是正（#757）

## 起点

issue [#757](https://github.com/endazon/ai-stock-trading/issues/757)。
`scripts/check-test-traceability.js` は「受け入れ基準 → テスト写像」を検査する外部依存ゼロの
チェッカーで、計画 ID を持たないメタ作業（検査器そのものの不具合修正）である。
2026-09-11 の subagent 6 本すべてがローカル（Windows）で本事象を踏んだ（#750 #743 #632 #584 #637 の報告）。

## 事象（実測）

Windows（大文字小文字を区別しない FS）で以下を実行すると赤になる。

```
$ node scripts/check-test-traceability.js
notice: check-test-traceability: planning submodule が未 populate のため、テストが参照する FR/UC/SC の実在検査を skip しました
[check-test-traceability] 違反 1 件を検出しました:
  - [T1] 旧樹形のサービス配下テストディレクトリが 12 件あるのに、旧樹形のテスト .cs を 1 件も走査できていません（期待する形: backend/Services/<Svc>/tests/**）。その樹形のサービスは母集合から静かに落ちている可能性があります。
```

一方 `git ls-files | grep -c '^backend/Services/[^/]*/tests/'` は `0`（追跡下に旧樹形の `tests/`
ディレクトリは実在しない）。CI（`ubuntu-latest`、大文字小文字を区別する FS）は緑のまま。

## 原因

`serviceTestDirs()`（`scripts/check-test-traceability.js:136-146`）が

```js
if (fs.existsSync(path.join(services, e.name, 'tests'))) dirs.old++;
if (fs.existsSync(path.join(services, e.name, 'Tests'))) dirs.new++;
```

と、大文字小文字だけが異なる 2 つのパス文字列を個別に `fs.existsSync` へ渡している。
`fs.existsSync` は OS のパス解決を経由するため、大文字小文字を区別しない FS（NTFS 既定・APFS 既定）
では実在する `Tests/` が `tests` への `existsSync` にも一致し、`dirs.old` と `dirs.new` の両方が
真になる（実際には `Tests/` だけが実在する 12 サービスすべてで `old` が誤って積み上がる）。
`testFiles()`／`serviceTestLayoutCounts()` は `git ls-files` 相当の実ファイル走査（`fs.readdirSync` の
再帰 ＋ 相対パスの文字列一致）で `tests`/`Tests` を区別しており、こちらは大文字小文字を区別しない FS
でも壊れない。結果として「母数（`serviceTestDirs`）は `old:12` を主張するのに、走査件数
（`serviceTestLayoutCounts`）は `old:0`」という T1 の警戒条件（部分移行の痩せ）に一致してしまう。

CI（Linux・大文字小文字を区別する FS）では `existsSync('tests')` が実在する `Tests/` に一致しないため
再現しない——**ローカル（Windows/macOS 既定）でだけ恒常的に赤くなる**、実行環境依存の偽陽性である。

## 走査した母集合（是正漏れが無いことの確認）

同型の事故（大文字小文字だけが異なる 2 パスを個別に `fs.existsSync` へ渡し、結果を作り分ける）が
他の `scripts/check-*.js` に無いか、`existsSync` の全呼び出しを母集合として走査した。

```
$ grep -n "existsSync" scripts/check-*.js
```

| ファイル:行 | 呼び出し | 判定 |
| --- | --- | --- |
| `check-action-versions.js:198` | 単一パス（`companionPath`） | 対象外（ペアでの大文字小文字比較ではない） |
| `check-consumer-endpoint-names.js:263` | `path.join(root, SERVICES_DIR, svc, 'src')` の**1 本だけ**を見て、真なら `old`・偽なら `new`（二値の either/or） | **対象外**——新樹形は `src` と大文字小文字だけが違う兄弟ディレクトリ（`Src` 等）を持たず、`Features/` 等の別名を使う。ペアの `existsSync` 比較ではないため本件と同型の穴は無い（`listServiceDirs()` は `fs.readdirSync` の実名で母数を取っており、こちらも問題なし） |
| `check-coverage.js:134,235,454` | 単一パス（探索対象ファイル・レポートの存在確認） | 対象外 |
| `check-doc-links.js:109` | `!fs.existsSync(resolved)` でリンク切れを判定する単一パス | 対象外（大文字小文字だけが異なる 2 パスを比較する構造ではない。OS 依存でリンク切れを見逃す余地は理論上あるが、本 issue の「ペアの母数を二重計上する」事故とは別種であり、既存 issue 化されていないため本修正のスコープ外とする） |
| `check-frontend-empty-frames.js:56` | 単一パス（ルートディレクトリの存在確認） | 対象外 |
| `check-permission-denials.js:847` | 単一パス（一時ファイル） | 対象外 |
| `check-reading-budget.js:89,94` | 単一パス（必読ファイルの存在確認） | 対象外 |
| `check-test-traceability.js:72,187,198,299` | いずれも単一パス | 対象外 |
| `check-test-traceability.js:142-143` | **本件**（`tests`/`Tests` のペアを個別に `existsSync`） | **是正対象** |

→ **是正が必要な箇所は `check-test-traceability.js:142-143` の 1 箇所のみ**。
`check-consumer-endpoint-names.js` は同じ「新旧樹形の母数をディレクトリの有無から引く」設計
（コメントが本ファイルを名指しで参照し合っている）だが、二値判定であり大文字小文字のペア比較を
行っていないため変更しない。

## 対象範囲

### 対象（In scope）

- `scripts/check-test-traceability.js` の `serviceTestDirs()`: 大文字小文字を区別しない FS でも
  区別する FS と同じ結果になるよう、`fs.existsSync(<pathA>)` / `fs.existsSync(<pathB>)` の代わりに
  `fs.readdirSync(<services>/<Svc>, { withFileTypes: true })` で実ディレクトリエントリ名を取得し、
  `=== 'tests'` / `=== 'Tests'` の完全一致（大文字小文字を保持した文字列比較）で判定する。
- `scripts/scripts.repo.test.js`: 本事象の回帰テスト（陰性対照・陽性対照）を追加する。
- `scripts/README.md`: T1 のロジック記述があれば、実装（`fs.existsSync` → 実名の完全一致）に揃える。

### 対象外（Out of scope）

- `check-consumer-endpoint-names.js` ほか、母集合走査で対象外と判定した箇所（上表）。
- `testFiles()` / `serviceTestLayoutCounts()`（走査ロジック）は元々 `git ls-files` 相当の文字列一致で
  大文字小文字を区別しており、変更不要。
- `check-doc-links.js` の OS 依存リンク切れ検出（既存 issue 化されておらず、本 issue の記述範囲外）。

## 方式

`serviceTestDirs()` を、サービスディレクトリ直下のエントリ名を `fs.readdirSync` で取得し、
取得した**実名の集合**に対して文字列の完全一致（`===`）で判定するよう変更する。

```js
function serviceTestDirs(root) {
  const dirs = { old: 0, new: 0 };
  const services = path.join(root, 'backend', 'Services');
  if (!fs.existsSync(services)) return dirs;
  for (const e of fs.readdirSync(services, { withFileTypes: true })) {
    if (!e.isDirectory()) continue;
    let entries;
    try {
      entries = fs.readdirSync(path.join(services, e.name), { withFileTypes: true });
    } catch {
      continue;
    }
    const names = new Set(entries.filter((x) => x.isDirectory()).map((x) => x.name));
    if (names.has('tests')) dirs.old++;
    if (names.has('Tests')) dirs.new++;
  }
  return dirs;
}
```

`fs.readdirSync` はディレクトリエントリを OS のファイルシステムから列挙した**実名**で返す
（大文字小文字を区別しない FS でも、実際に作成された大文字小文字のまま返る。Node.js の `fs` ドキュメント・
実測とも一致）。`Set.has()` は通常の文字列の厳密等価であり、OS のパス解決を経由しないため、
大文字小文字を区別する/しない FS の違いに影響されない。これにより Windows・macOS・Linux のいずれでも
同一の判定結果になる。

`isNewLayoutServiceTestsDir()`（新樹形の判定）は文字列比較のみで実装済みであり、本件の影響を受けない
（変更不要）。

## 受け入れ基準

- [x] Windows（本ホスト）で `node scripts/check-test-traceability.js` が緑になる
      （`git ls-files | grep -c '^backend/Services/[^/]*/tests/'` が `0` の実態と一致）
- [x] 大文字小文字を区別しない FS で、実在は `Tests/` のみなのに `serviceTestDirs()` が `old` を
      誤って計上しない（陽性対照）
- [x] 実在の小文字 `tests/` は引き続き `old` として正しく計上される（陰性対照——検出漏れも作らない）
- [x] `Tests/` と `tests/` が両方実在するサービスでは両方が計上される（新旧混在期間の既存挙動を壊さない）
- [x] `serviceTestLayoutCounts()` ほか走査ロジックの挙動・T1 の発火条件（走査 0 件 vs 母数 > 0）は変更しない
- [x] `scripts/scripts.repo.test.js` の既存 T1 テスト（旧樹形/新樹形の正負確認）が全て緑のまま
- [x] `scripts/README.md` の T1 記述（あれば）を実装に揃える

## テスト方針

`scripts/scripts.repo.test.js`（外部依存ゼロ・Node 標準 `assert` のみ。既存の T1 テスト群
（636 行目付近）と同じ idiom: `fs.mkdtempSync` で一時ツリーを作り `tt.serviceTestDirs(root)` を直接呼ぶ）
へ以下を追加する。

| ケース | 内容 | 期待 |
| --- | --- | --- |
| 陰性対照（本件の回帰） | 実在するのは大文字始まり `Tests/` のみの一時ツリーを作る | `serviceTestDirs()` は `{ old: 0, new: 1 }`（`old` を誤計上しない） |
| 陽性対照 | 実在するのは小文字 `tests/` のみの一時ツリーを作る | `serviceTestDirs()` は `{ old: 1, new: 0 }` |

これらは `fs.readdirSync` の実名比較を直接検証する単体テストであり、大文字小文字を区別しない FS
（Windows）・区別する FS（Linux CI）のどちらで実行しても同じ結果になることを主張として固定する
（Windows 実行環境固有の分岐は設けない——`fs.existsSync` に依存しない実装そのものが「環境に依存しない」
ことの証明になるため、CI 側で追加の OS マトリクスは不要）。

🔴 **「同一サービス配下に `tests/` と `Tests/` を両方作る」混在テストは書かない**——大文字小文字を
区別しない FS（本ホスト）では、同一親ディレクトリ下で大文字小文字だけが異なる 2 エントリは物理的に
共存できない（2 回目の `mkdir` は 1 回目のエントリを指すだけで、実際には 1 個のディレクトリしか
作られない）。新旧混在は「サービスをまたいで」起きる事象であり、既存テスト
「`serviceTestDirs` / `serviceTestLayoutCounts` は新旧を分けて数える」（別サービス A: `tests/` ・
B: `Tests/`）が既に固定している。

## Mutation evidence（回帰確認の実測）

- 是正前（`fs.existsSync` によるペア判定）: 本ホスト（Windows）で
  `node scripts/check-test-traceability.js` が `[T1] 旧樹形...12 件` で赤（exit 1）。
- 是正後: 同じホスト・同じ作業ツリーで `node scripts/check-test-traceability.js` が緑（exit 0）。
- 追加した単体テスト 3 本は、是正前のロジック（`fs.existsSync(join(dir,'tests'))`）に対しては
  本ホスト（大文字小文字を区別しない FS）で「陰性対照」が **失敗**する
  （`{ old: 0, new: 1 }` を期待するが実際は `{ old: 1, new: 1 }` になる）ことを、
  実装前に一時的に旧コードへ戻して確認する（下記「検証記録」参照）。

## 計画書との差異

- 差異: なし。本件は検査器（メタ作業）のバグ修正であり、計画書（FR/UC/SC/ADR）には抵触しない
  （`.claude/rules/traceability.repo.md`「メタ作業（規約・検査器・文書統制）は代表例で、製品の
  作業にも当たる番号が無いことはある」に該当し、`NFR` の無採番を用いる）。

## 未決事項

- なし。`check-doc-links.js` の OS 依存リンク切れ検出（母集合走査で発見した別種の懸念）は、
  本 issue の記述範囲外として別 issue 化を検討する余地があるが、実害（誤検出の実例）が
  まだ無いため本 PR では起票しない。
