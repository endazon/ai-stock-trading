---
title: 統制値の計画適合検査を復活させるかの判断（(b) 復活させない）
issue: "#675"
plan_refs:
  - FR-10
  - NFR
adr_refs:
  - IADR-0302
status: done
created: 2026-09-04
---

# 作業仕様書: 統制値の計画適合検査を復活させるかの判断（#675）

## 背景

`backend/Tests/AiStockTrading.PlanConformance.Tests/`（`PlanRiskDefaults` / `ActualDefaults` /
`KnownPlanDeviations` / `PlanSourceDigests` ほか計 11 ファイル）は、統制値 `TradingDefaults` が
計画書の統制値の表からずれたときに CI を赤くする検査であった。**planning submodule を読んでいた**ため
[#536](https://github.com/endazon/ai-stock-trading/pull/536)（ADR-0029 決定2）で削除された。

[#636](https://github.com/endazon/ai-stock-trading/issues/636) は (a) 復活 / (b) 復活させず記述を是正、の
いずれかを求め、**(b) を採って記述を是正した**（IADR-0296）。**(a) の是非だけが本 issue に残っていた。**

## 判断

**(b) を採る（復活させない）。** 利用者裁定 2026-09-04。根拠は [IADR-0302](../adr/IADR-0302_plan-conformance-check-not-restored.md) 決定1。

1. リポジトリ自身の規約「検査器・規約の追加は同型事故 2 回から」の条件を満たしていない。
2. #378 が同型を試み #536 で削除された。復活は 3 度目の往復になる。
3. 🔴 **人手転記のホップが残り、誤転記は「CI が緑のまま計画とずれる」＝担保があるという誤認**を生む。
   これは「検査が無い」と明示されている現状より悪い。

## 変更したファイル

| ファイル | 変更 |
| --- | --- |
| `.ai-context/adr/IADR-0302_*.md` | 新規（判断と理由の記録） |
| `.ai-context/adr/README.md` | 索引行を追加 |
| `docs/DEFINITION_OF_DONE.md` | 「復活の是非自体は別 issue で判断する」という**未決の含みを外し**、決着済みであることへ差し替える（trace ブロック経由で IADR を参照） |
| `.ai-context/adr/IADR-0127_*.md` | 末尾へ**日付付きの追記**（本文プロズは書き換えない）。レジストリが実在しないことと、復活させない決着を記す |

## 引いた母集合と、除外したものと理由

`git grep -ln 'KnownPlanDeviations\|PlanRiskDefaults\|PlanConformance' -- .ai-context docs` で **20 ファイル**を得た。

| 対象 | 扱い | 理由 |
| --- | --- | --- |
| `docs/DEFINITION_OF_DONE.md` | **是正する** | 生きた文書であり、未決の含みが残ると「いずれ検査が戻る」という誤読を招く |
| `.ai-context/adr/IADR-0127`（レジストリを定義した当の IADR） | **日付付き追記のみ** | `.ai-context/` は凍結記録で本文プロズを書き換えない（CLAUDE.md）。参照先が実在しないことは追記で示す |
| その他の IADR 13 件・作業仕様書 4 件 | **触らない** | すべて凍結記録であり、当時の判断としては正しい。IADR-0127 に追記を置けば、レジストリの現況はそこから辿れる |
| `docs/blocked-tasks.md` | **触らない**（確認のみ） | #636 で既に是正済み。`PlanConformance` 系の語を含まないことを実測で確認した |

## 検証

- `node scripts/check-trace-blocks.js` / `check-doc-links.js` / `check-cross-repo-refs.js` /
  `gen-knowledge-graph.js --check` / `check-adr-index-sync.js` / `check-commit-messages.js`
- **コード変更なし**（`backend/` の差分は 0）。`bin` / `obj` の残骸は gitignore 済みで追跡されておらず、
  掃除してもリポジトリの内容は変わらない。
