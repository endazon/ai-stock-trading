---
title: キットの docstring が IADR 番号を裸で引き、配布先で別の ADR を指す（さらに前提が古い）
type: plan-feedback
status: open
category: その他
related_ids: [NFR, IADR-0200, IADR-0201]
source_repo: ai-stock-trading
source_ref: docs/specs/20260815_515_cross-repo-refs-commit-face.md（#515 / PR #516）
author: endazon (with Claude Code)
created: 2026-08-15
dispatched: true
planning_issue: 354
---

# キット docstring の番号衝突と、古い前提

## 事実 1: 🔴 `IADR-0140` が配布先で別の ADR を指す

`check-cross-repo-refs.js` の docstring は **`IADR-0140` を裸で 2 回引いている**。

**`IADR-xxxx` はリポジトリごとに独立採番である。** ai-stock-trading の `IADR-0140` は
**「発注先（Broker Provider）を独立した軸として導入し `TradeMode` を廃止する」**であり、
ワークフロー権限とは無関係である（該当語の grep → **0 件**）。

> 🔴 **キット自身の規約（`traceability.md`）が「計画 ID はプロジェクトごとに独立採番のため衝突する」と定め、
> `<PROJ>/<ID>` の修飾を求めている。docstring はその規約に反している。**

**実害が出た。** #515 の実装でこの番号を裏取りせず引き写し、
**無関係な ADR を出典として引く記述を 2 ファイルへ持ち込んだ**（AI レビューが検出・PR #516 で訂正）。

## 事実 2: 🔴 「ワークフローを編集できない」という前提が古い

同 docstring は「`.github/workflows/` は GitHub App 権限で編集できないため相乗りする」と書くが、
**ai-stock-trading では成立していない。**

- ワークフローは**実測 65 コミット分、実際に変更されている**
- うち `889e41f`（PR #505）は**キット由来の検査器を `ci.yml` へ足した変更そのもの**
- 本リポの作業仕様書は **2026-08-01 に「`workflow` スコープを持つローカル認証から push することで解消した」と記録済み**

**「相乗りする」という判断自体は有効**（env を落とすと fail-open するため）だが、
**理由づけが古い制約に依存している。**

## 🔴 これで 3 件目である

キット配布物が**自身の規約・自身の前提と食い違う**事例が、1 セッションで 3 つ見つかった。

| # | 内容 | 環流先 |
| --- | --- | --- |
| 1 | `traceability.md` が**自身の表記規約に違反**（`planning issue #202`） | planning#349 |
| 2 | docstring が「`check-commit-messages.js` から検査する」と書きながら**未配線** | 本リポで実装（IADR-0201） |
| 3 | docstring の**番号衝突**と**古い前提**（本記録） | planning#354 |

**配布物を検査対象に入れる価値は、もう十分に実証されたと考える。**

## 依頼した内容（planning#354）

1. docstring の `IADR-0140` を**番号に依存しない書き方**へ改める
2. 「ワークフローを編集できない」の記述を見直す（**配布先で成立しない前提を配らない**）
3. **キット配布物全体を、キット自身の検査器にかける**ことを検討する（planning#349 と同趣旨）

## トリアージ結果

（計画側で記入）

## 関連

- [#515](https://github.com/endazon/ai-stock-trading/issues/515) / [PR #516](https://github.com/endazon/ai-stock-trading/pull/516)
- [IADR-0201](../docs/adr/IADR-0201_cross-repo-refs-commit-face.md) 決定2 の訂正
- planning#349（`traceability.md` が自身の規約に違反）
