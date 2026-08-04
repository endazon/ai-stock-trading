---
title: 作業仕様書 — 環流記録 5 件の「送付は未実施」注記を、実際の起票先 Issue へ張り替える
type: work
status: review
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs: []
related_specs:
  - ../../feedback/README.md
---

# 作業仕様書: 環流記録の「送付は未実施」注記を起票先 Issue へ張り替える

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（計画環流の運用記録の是正。**NFR** 相当）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（**新規 IADR も作らない**。文書の事実誤りの訂正であり、設計判断を伴わない）
- 手順の一次情報: [feedback/README.md](../../feedback/README.md) 「手順」3
- 同じ引継ぎ資料から切り出したもう一方の作業（項目 B）:
  [PR #369](https://github.com/endazon/ai-stock-trading/pull/369)。**本 PR とは独立**であり、どちらを先に
  マージしてもよい（相互にファイル参照を持たせていない。`related_specs` に相手の仕様書を書くと、
  未マージのあいだ `check-doc-links` が破損として検出するため）

## 目的・背景

`feedback/` の環流記録 5 件は、冒頭に次の趣旨のブロック引用を持っていた。

> **本書は起草のみである。** 計画リポジトリへの送付（`plan-feedback` ラベル付き Issue の起票、または
> 計画リポ `draft/feedback/` へのコピー）は**未実施**。送付は人間または別セッションに委ねる。

しかし 5 件はいずれも **2026-08-04 に `endazon/project-planning` へ起票済み**である（`plan-feedback`
ラベル付き・Issue フォーム経由）。注記だけが起草時のまま残っており、**事実と食い違っている**。

放置すると、次に `feedback/` を見た担当者（または AI セッション）が「まだ送付されていない」と読み、
**同じ内容を重複起票する**。実際に本作業では、引継ぎ資料が 4 件を「未到達」として残作業に挙げていたため、
起票の直前に既存 Issue を照合するまで重複起票の一歩手前であった。注記の張り替えはその再発を止める。

## 対象範囲

- **対象**: `feedback/` の 5 件のブロック引用を、起票先 Issue へのリンク付きの「送付済み」注記へ置き換える。

  | 記録 | 起票先 |
  | --- | --- |
  | `20260804_adr0016-short-ratio-denominator.md` | [endazon/project-planning#177](https://github.com/endazon/project-planning/issues/177) |
  | `20260804_adr0016-stop-order-rejection-reason.md` | [endazon/project-planning#178](https://github.com/endazon/project-planning/issues/178) |
  | `20260804_fr19-guard-scope.md` | [endazon/project-planning#179](https://github.com/endazon/project-planning/issues/179) |
  | `20260803_adr0030-project-structure-per-service-scope.md` | [endazon/project-planning#180](https://github.com/endazon/project-planning/issues/180) |
  | `20260804_adr0027-wolverine-migration-caveats.md` | [endazon/project-planning#181](https://github.com/endazon/project-planning/issues/181) |

- **対象外**:
  - **新規の起票**。5 件とも起票済みであり、追加で起こすことは無い。
  - frontmatter の `status: open`。これは**計画側のトリアージ状態**を表す欄であり、送付の有無を表さない
    （送付済みの既存記録 `20260715_adr0002-opend-unattended-limited.md` も `open` のままである）。
    ここを送付フラグとして流用すると、欄の意味が二重になる。
  - 各記録の本文（現状・問題点・提案）。内容は起票時のまま正しい。
  - `feedback/README.md` の手順。手順自体に誤りは無い。

## 設計

置き換え後の注記は次の形に揃える。

> **送付済み（2026-08-04）。** 計画リポジトリへ `plan-feedback` ラベル付き Issue として起票した:
> endazon/project-planning#NNN。
> 以降のトリアージ・裁定は当該 Issue で行う。本書は実装リポジトリ側の控えである。

- 「以降のトリアージ・裁定は当該 Issue で行う」を入れる理由は、`feedback/README.md` が定めるとおり
  **原典の反映先は計画リポジトリ側**であり、この記録が控えにすぎないことを読み手に明示するためである。
  これが無いと、記録側に追記して計画へ届いたつもりになる経路が残る。
- **クロスリポジトリの issue 番号は修飾する**（`endazon/project-planning#NNN`）。裸の `#NNN` は GitHub 上で
  本リポジトリの issue／PR へ誤リンクする（`.claude/rules/traceability.md`「クロスリポジトリの issue / PR
  番号の修飾」）。
- ADR-0030 / ADR-0027 の 2 件は、記録の原文が宛先を「計画リポジトリ（`microservices-platform`）」と
  書いていた。実際の宛先リポジトリは `project-planning` であり、`microservices-platform` はその中の
  **プロジェクト名前空間**（`projects/microservices-platform/`）である。この 2 件だけは注記に
  「宛先はリポジトリ `project-planning`・裁定対象は同リポ内の `projects/microservices-platform/`」を
  併記して、リポジトリ名とプロジェクト名の混同を残さない。

### 引継ぎ資料の記述との差異

引継ぎ資料「Issue #163 / #165 / #167 / #168 対応の引継ぎ資料」（2026-08-04）の項目 D は、環流 4 件を
「未到達（`/plan-feedback` で起票が必要）」としている。**この記述は資料作成時点で既に古い。**

- 資料が挙げた 4 件は #177 / #178 / #180 / #181 として起票済みである（起票時刻 11:48〜11:52 UTC）。
- さらに資料の一覧に**無い** 5 件目（`20260804_fr19-guard-scope.md` → #179）も同時に起票されていた。
  本作業はこれも対象に含める。同じ失敗形の注記を 1 件だけ残す理由が無いためである。

## 受け入れ基準

- [x] `feedback/` に「起草のみ」「未実施」の注記を持つ記録が 1 件も残っていない。
- [x] 5 件すべてに起票先 Issue へのリンクがあり、リンク先が実在し、記録の主題と一致する。
- [x] issue 番号がすべて `endazon/project-planning#NNN` の形で修飾されている。
- [x] 記録の本文（現状・問題点・提案・影響範囲）と frontmatter に変更が無い。
- [x] **重複起票を行っていない**（新規 Issue を 1 件も作成していない）。

## テスト方針

文書のみの変更であり、テストコードの対象ではない。検証は次で行う。

| 検証 | 期待 | 実測 |
| --- | --- | --- |
| `grep -l 起草のみ feedback/*.md` | 該当なし | **該当なし** |
| 「送付済み」を含む記録の数 | 5 件 | **5 件** |
| `gh issue view` による #177〜#181 の実在・ラベル・主題の照合 | 5 件とも `plan-feedback` ラベル付きで主題が一致 | **一致**（#181 は本文も記録の写しであることを確認） |
| `node scripts/check-doc-links.js` | 本変更が破損リンクを増やさない | 既存 20 件から**増減なし**（別件・下記参照） |
| `git diff --stat` | `feedback/` の 5 件のみ | **5 ファイル** |

## 計画書との差異

- 差異: なし。

## 未決事項

なし。

## 補足: 本作業で見つけた別件（対象外）

夜間の `doc-links-planning` ワークフローが少なくとも 3 日連続で失敗している。計画リポ側で ADR ファイルが
改名された（例: `ADR-0007_kill-switch-authz.md` → `ADR-0007_trading-guard-and-margin.md`、
`ADR-0008_staged-rollout.md` → `ADR-0008_staged-gates-and-backtest.md`）のに `docs/` 内のリンクが追随して
おらず、破損リンクが 20 件ある。PR CI の `doc-links` は submodule 無しで走るため検出されない。

本作業とは無関係のため触らない。**機械置換で直すべきではない**（ADR-0007 は改名で対象そのものが
「統制の権限」から「取引ガードと信用」へ変わっており、参照文脈が正しいかの確認が要る）。別途対応する。
