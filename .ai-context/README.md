# .ai-context/ — AI 向け文脈資料・凍結記録

このディレクトリは、実装ADR（`adr/`）と作業仕様書（`specs/`）を置く場所である。**`docs/` が人が読む生きた仕様書、`.ai-context/` が AI 向けの文脈資料・凍結記録**という主従を、README の但し書きではなくディレクトリ構造そのもので表す（計画 ADR-0029〔project-planning `projects/ai-stock-trading/07_adr/ADR-0029_impl-docs-restructure.md`〕決定 1）。

## 構成

```text
.ai-context/
├── adr/     # 実装ADR（IADR-XXXX。docs/templates/adr_template.md から /new-spec adr で作成）
└── specs/   # 作業仕様書（YYYYMMDD_<概要>.md。docs/templates/spec_template.md から /new-spec work で作成）
```

本リポジトリに `superpowers/` の実体は無い（存在するリポジトリでは `.ai-context/superpowers/` を追加する）。

## 「凍結記録」とは何か

ここに置く文書は、**書いた時点の判断・経緯をそのまま保存する記録**である。実装ADR は「その時点でその決定を下した」という事実、作業仕様書は「その PR をその仕様で進めた」という事実を記録する。**確定した記録の本文（プロズ）は、後から書き換えない。** 判断の誤りに気付いた場合や制約が変わった場合は、本文を書き換えるのではなく、新しい IADR を起票して `Superseded by IADR-XXXX` を旧 IADR に追記するか、（作業仕様書の場合は）新しい作業仕様書・PR で対応する。

この凍結の原則は、資料の**置き場が変わっても変わらない**。移設・パス修正・frontmatter の整形といった機械的な変換はあっても、本文プロズは不変のまま保たれる（2026-08-21 の `docs/adr` `docs/specs` → `.ai-context/` 移設も、リンクと frontmatter のみの機械変換であり本文は不変）。

## `docs/` との違い（主従）

| | `docs/` | `.ai-context/` |
| --- | --- | --- |
| 読み手 | 人間（レビュー・引き継ぎ） | AI（実装セッションが参照する文脈） |
| 性質 | 生きた文書（更新し続ける） | 凍結記録（書いた時点で確定する） |
| 例 | 機能仕様書・画面仕様書・テスト仕様書・運用仕様書 | 実装ADR（IADR）・作業仕様書 |
| 計画 ID・IADR・issue 参照の書き方 | 表示テキストへ出さず、frontmatter 直後の trace ブロック（HTML コメント）へ非表示メタデータとして持つ | 本文にそのまま書いてよい（凍結記録は本文プロズを変えないため） |

`docs/` 配下の trace ブロックの書式は `.claude/rules/traceability.md`「trace ブロック規約」を参照。

## planning への言及について

このディレクトリの文書には、計画リポジトリ（project-planning）の ADR・要求・ユースケースなどへの言及が本文中に残っていることがある。これは**当時そう判断した、という史実の記述**であり、平文の歴史的記述として読む（パスリンクではない）。本リポジトリは planning への依存を持たない（ADR-0029 決定 2）ため、これらの言及を辿って計画書を機械的に開くことはできない。計画書・裁定を直接確認したい場合は、CLAUDE.md「計画リポジトリとの関係」の参照手段（GitHub URL または隣接クローンの読み取り専用）に従うこと。

## 運用ルール

- 新規の実装ADR・作業仕様書は、従来どおり本リポジトリ内（このディレクトリ）で起草・維持する（ADR-0029 決定 7）。
- 起票・作成は `/new-spec adr` `/new-spec work` を使う。テンプレートは `docs/templates/` に残る（出力先だけがここへ変わる）。
- 実在性の機械検査（IADR 番号・作業仕様書の有無）は `scripts/check-commit-messages.js` と `.claude/hooks/check-impl.js` が行う。
