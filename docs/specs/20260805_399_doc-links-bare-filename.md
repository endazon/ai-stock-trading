---
title: 作業仕様書 — check-doc-links が同一ディレクトリ内の裸ファイル名リンクを検査するようにする
type: spec
status: review
related_ids:
  - NFR
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
related_specs:
  - "./20260805_coverage-exclude-generated.md"
  - "../adr/IADR-0131_short-selling-controls-fail-closed.md"
  - "../DEFINITION_OF_DONE.md"
---

# 作業仕様書: 裸ファイル名リンクを check-doc-links の検査対象に含める

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **NFR**（CI ゲート・ドキュメント整合）。本作業は計画書由来の機能実装ではなく、CLAUDE.md
  「自動化・検証・安全」が定める CI ゲート（`doc-links` ジョブ）自体の欠陥是正である。
- 起点 issue: [#399](https://github.com/endazon/ai-stock-trading/issues/399)
- 発見経路: [#395](https://github.com/endazon/ai-stock-trading/pull/395) の AI レビュー指摘
  （`docs/adr/IADR-0144` が存在しない `IADR-0138_stage0-drawdown-tightening.md` へリンクしていたが
  `node scripts/check-doc-links.js` は `OK` を返した）。
- 機能要求（FR）／ユースケース（UC）／画面（SC）: 該当なし（開発基盤の検査器の修正）。
- 関連 ADR: なし（後述「実装 ADR の要否」）。

## 背景と問題

`scripts/check-doc-links.js` の `isBrokenRef` は、リンク候補が相対参照かどうかを次で判定していた。

```js
const looksRelative = t.startsWith('./') || t.startsWith('../') || (t.includes('/') && !t.startsWith('/'));
if (!looksRelative) return false;
```

`/` を含まない**同一ディレクトリ内の裸ファイル名**（`[IADR-0119](IADR-0119_xxx.md)`）はこの判定で
`false` になり、**実在検査に到達しないまま素通り**する。`docs/adr/` の IADR 相互参照はこの裸の形で
書くのが通例であり、**最も破損しやすい箇所がまるごと検査対象外**になっていた。

検査器が「効かない方向」に壊れると CI は緑のままであり、破損は無言で蓄積する（issue #139 で
planning 配下 753 件の除外により破損 20 件が蓄積したのと同じ構造）。実際、本作業の着手時点で
`develop` 上に破損 1 件が現存し、CI は `OK: 367 件` を報告し続けていた。

### 実測（着手前・develop `01fda7f`）

`docs/` 全体（367 ファイル）を新旧判定で走査した結果。

| 区分 | 件数 |
| --- | --- |
| 従来から検査対象（`./` `../` または `/` を含む） | 2,488 |
| **新たに検査対象になる裸ファイル名参照** | **724**（本文リンク 721・フロントマター 3） |
| うち現に破損しているもの | **1** |

破損 1 件の内訳:

| ファイル:行 | 誤 | 正 |
| --- | --- | --- |
| `docs/adr/IADR-0131_short-selling-controls-fail-closed.md:37` | `IADR-0119_decision-derived-position-effect.md` | `IADR-0119_decision-derived-close.md` |

## 対象範囲

- 対象: `scripts/check-doc-links.js` の `isBrokenRef` の相対参照判定、
  `scripts/scripts.repo.test.js` のテスト、上表の破損リンク 1 件の是正。
- 対象外:
  - `docs/adr/README.md` の `[[IADR-XXXX]]` 記法（別系統。本検査器の対象ではない）。
  - バックエンド／フロントエンドのコード（変更なし。`git diff --stat` で確認する）。
  - `--require-planning` の挙動・未 populate submodule の除外方針（変更しない）。

## 設計

### 判定の拡張

`looksRelative` に「`/` を含まないが `LINK_EXT` に一致する裸ファイル名」を加える。

```js
// #399: `/` を含まない同一ディレクトリ内の裸ファイル名（例: IADR-0119_xxx.md）。
// docs/adr/ の IADR 相互参照はこの形で書くのが通例であり、最も壊れやすい箇所が対象外だった。
// 拡張子（LINK_EXT）を要求するのは、本文中の普通の語（`README` 等）をリンク扱いしないため。
const bareFileName = !t.includes('/') && LINK_EXT.test(t);
const looksRelative = ... || bareFileName;
```

**判断1: `.md` 限定ではなく `LINK_EXT` 全体を条件にする。** issue の記述は「`.md` で終わる相対パス」だが、
直後の行が `if (!LINK_EXT.test(t)) return false;` で `LINK_EXT`（md/yaml/json/puml/mmd/png/jpeg/svg/drawio）を
要求している。ここだけ `.md` 限定にすると同一ディレクトリの図・スキーマ参照（`diagram.puml` 等）が
対象外に残り、判定の基準がファイル内で二重になる。**単一の基準（`LINK_EXT`）へ揃える。**

**判断2: `!t.includes('/')` を条件に明示する。** これを付けないと新旧の節が重なり、`looksRelative` が
実質 `LINK_EXT.test(t)` と同義になって「相対参照らしさの判定」という変数名が意味を失う。各節が
互いに素になるよう書き、読み手が「何が新たに対象へ入ったか」を 1 行で読めるようにする。

### 誤検出のリスク評価

判定を広げると、これまで見ていなかった 724 件が実在検査に入る。誤検出（実在しないのに正当な
記述）が起き得る経路を洗い、実測で確認した。

| 経路 | 扱い | 根拠 |
| --- | --- | --- |
| 外部 URL・`mailto:`・アンカーのみ・ルート絶対パス | 従来どおり除外（118 行目で先に落ちる） | 判定変更の前段にあり影響しない |
| テンプレ変数（`<...>` / `${...}` / `{{...}}`） | 従来どおり除外（119 行目） | 同上 |
| 拡張子を持たない裸の語（本文中の `README` 等） | 対象外のまま | `LINK_EXT` を要求するため |
| インラインコード内のファイル名（`` `IADR-0119_x.md` ``） | **対象外のまま** | `collectBroken` の第 3 経路が `./` `../` 始まりのみを拾う設計を変えない |
| フェンス内の例示リンク `](x.md)` | 対象に入り得る（残余リスク） | 本文リンク走査はフェンスを区別しない**既存**の設計。実測で該当 0 件 |

実測 724 件のうち破損は 1 件のみで、それは真の破損である。**誤検出は 0 件**であり、新たに受け入れる
トレードオフは上表最終行の残余リスク（フェンス内の例示リンクが将来書かれた場合）に限られる。これは
判定拡張が生んだ性質ではなく本文リンク走査が元から持つ性質であり、発生時は例示側を
`./` 付きにするか `<...>` へ置き換えれば足りる。

### 実装 ADR の要否

**IADR は作成しない。** 本作業は「検査器が自らのヘッダコメントで宣言している対象（`本文の Markdown
リンク [text](path)`）を、実際には一部しか見ていなかった」という**実装漏れの充足**であり、新しい設計
判断・アーキテクチャ選択を伴わない。上記「判断1／判断2」はいずれも既存コード内の基準へ揃える
局所的な選択であり、本仕様書に記録すれば追跡に足りる。誤検出という新たなトレードオフも実測 0 件で
受け入れていない（残余リスクは既存設計由来）。次の空き番号 IADR-0147 は消費しない。

## 受け入れ基準

- [ ] `isBrokenRef` が `/` を含まない裸ファイル名（`LINK_EXT` に一致）を実在検査の対象にする
- [ ] `node scripts/check-doc-links.js` が `docs/adr/IADR-0131` の破損リンクを**名指しして exit 1** する
      （変異検査で実測し、PR 本文に赤・緑の実出力を貼る）
- [ ] 破損リンク 1 件を是正した後、`node scripts/check-doc-links.js` が exit 0 で `OK` を返す
- [ ] 否定形テストを**正の確認と同数以上**置き、`node scripts/scripts.test.js` が green
- [ ] `check-commit-messages` / `check-banned-libraries` / `check-test-traceability` が green
- [ ] 変更が `docs/` と `scripts/` に限られる（`git diff --stat` で確認）

## テスト方針

テストは `scripts/scripts.repo.test.js`（リポジトリ固有テストの companion）へ置く。
`scripts/scripts.test.js` はキット（impl-handoff-kit）配布の共通テストであり、同期のたびに上書き
差し替えされるため、そこへ追記すると本修正のテストが黙って消える。既に同ファイルには
`check-doc-links` の固有テスト（`--require-planning` 系）がある先例に揃える。

検査器は「効かない方向」に壊れても CI が緑のままで気付けないため、**否定形（誤検出しないこと）を
正の確認と同数以上**置く（[IADR-0143](../adr/IADR-0143_coverage-denominator-generated-code-exclusion.md) /
[IADR-0145](../adr/IADR-0145_permission-denial-fixability-classification.md) と同じ思想）。

### 正の確認（検査が効くこと）

| # | 内容 |
| --- | --- |
| P1 | 実在しない裸ファイル名リンクを破損と判定する |
| P2 | 同一ディレクトリの `.png` 等（`.md` 以外の `LINK_EXT`）も対象になる |
| P3 | `collectBroken` が本文の裸ファイル名破損リンクを拾う |
| P4 | フロントマターのリスト項目に書かれた裸ファイル名も対象になる |
| P5 | **変異検査**: 破損した裸ファイル名リンクを含む docs を与えると `main` が exit 1 で名指しする |

### 否定形の確認（誤検出しないこと）

| # | 内容 |
| --- | --- |
| N1 | 実在する裸ファイル名リンクは破損としない |
| N2 | 外部 URL（`https://` / `http://` / スキーム付き） |
| N3 | `mailto:` |
| N4 | アンカーのみ（`#section`） |
| N5 | ルート絶対パス（`/docs/x.md`） |
| N6 | テンプレ変数 `<...>` |
| N7 | テンプレ変数 `${...}` |
| N8 | テンプレ変数 `{{...}}` |
| N9 | 拡張子を持たない裸の語（`README` / `IADR-0119`） |
| N10 | インラインコード内のファイル名に見える文字列（実在しなくても拾わない＝既存の扱いを壊さない） |
| N11 | 未 populate な submodule 配下のリンクは従来どおり skip し、除外件数として報告される |

**N11 の但し書き**: 裸ファイル名は必ず「その Markdown 自身のディレクトリ」に解決するため、
*裸ファイル名が未 populate submodule 配下に落ちる*状況は構造上あり得ない（その Markdown 自身が
submodule 内に必要になるが、未 populate＝空ディレクトリなので Markdown は存在し得ない）。したがって
N11 は「判定拡張が既存の skip 分岐を壊していないこと」を、`/` 形のリンクと裸ファイル名の破損を
同一フィクスチャに同居させて確認する。

## 計画書との差異

- 差異: なし（計画書由来の機能に触れない。開発基盤の検査器の修正）。

## 未決事項

- なし。判定拡張は `scripts/check-doc-links.js` がキット（impl-handoff-kit）配布物であるため、
  同じ欠陥はキットを取り込んだ他リポジトリにも存在する。キット側への還流は本リポジトリの
  範囲外であり、本 PR では扱わない（キット同期時に本修正が巻き戻る可能性があるため、
  テストを companion 側に置いて検知できるようにしてある）。
