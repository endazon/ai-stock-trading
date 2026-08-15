---
title: IADR-0185 必須チェックは check 名で指定し、設定できないことは能力の不在と規則の禁止に分けて記録する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0184]
author: endazon (with Claude Code)
created: 2026-08-14
updated: 2026-08-14
plan_refs:
  - ../../planning/docs/ai-implementation-workflow-guide.md
---

# IADR-0185: required status check の指定と、blocked:human の記録（#473）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-14
- 決定者: Claude Code（実装）

## 起点・関連

- **NFR**（運用保守）
- 実装 issue: **#473**（受け入れ基準 3・4）
- 正本: `planning/docs/ai-implementation-workflow-guide.md` §4・§6
- 参照実装: MSP#706（MSP/IADR-0182。**同じキット由来の同じ欠陥を先に踏んだ先例**）／PR #702（MSP/IADR-0180。blocked 判定の再検証）
- 作業仕様書: [20260814_473](../specs/20260814_473_guide-enforcement.md)

## コンテキストと課題

issue #473 基準 3 は「`build-and-test` と claude-review 完了を develop の required status check にする（AI で完結しない場合はその旨を記録し設定手順を書き残す）」である。手順書は `docs/ai-workflow.md` §必須チェックの有効化に既に在った。**そして、そこに書かれた必須チェック名の一部は check として存在しなかった。**

| 既存節が挙げるもの | 実在 |
| --- | --- |
| **`CI`** | **無い**（`ci.yml` の**ワークフロー名**） |
| **`Security`** | **無い**（`security.yml` の**ワークフロー名**） |
| `pr-title` / `CodeQL`（matrix 展開名 `Analyze (csharp)` 等） | 在る（`CodeQL` 単体は無い） |

MSP が同じキット由来の同じ誤りを 2026-08-11 に実測している（MSP/IADR-0182 決定1。**キット `repo-template/docs/ai-workflow.md` から継承した欠陥**であり、本リポも同型を持っていた）。**手順書どおりに設定すると、存在しないチェックを待ち続けて develop が恒久的にマージ不能になる。** 同じ節が「`paths:` フィルタ付きは永久 pending」と警告しながら、原因を `paths:` に限定して書いていたのも同型である。

## ★★ 決定 1: **必須にするのは「check の名前（ジョブ側の名前）」。ワークフロー名は書かない**

GitHub Actions が report する status check の context は**ジョブ側の名前**である。ワークフロー名（`name:`）は context として存在しない。`docs/ai-workflow.md` へ**実名の表**を置き（`build-and-test` / `lint` / `commit-messages` / `pr-title` / `secret-scan` / `dependency-review` / `Analyze (csharp)` 等 / `claude-review`）、**`CI` / `Security` / `CodeQL` を明示的に禁じた**。誤りは消さず訂正として残す（`docs/blocked-tasks.md` のポリシー）。

## ★★ 決定 2: **設定できないことを、能力の不在と規則の禁止に分けて記録する**

「できない」と書く前に実測した（MSP/IADR-0180 決定1「判定には賞味期限がある」）。

| 経路 | 実測（**2026-08-14**） | 種別 |
| --- | --- | --- |
| ローカルの `gh` CLI | `repo` スコープで認証済み・本リポに `admin: true` | **能力は在る** |
| CI（Actions の `GITHUB_TOKEN`） | branch protection の変更権限が無い | 能力の不在 |
| AI がリポジトリ設定を変更すること | **運用ガイド §6 が人間の関与に留保**（フェーズ計画承認・監査サンプリング・裁定＋required check 配備までのマージ操作）。どのチェックを必須にするかは統制の設計判断であり、**AI が自分に課す関門を自分で決めない** | **規則による禁止** |

**能力の不在は環境が変われば消えるが、規則の禁止は指示が変わらない限り残る。** 混ぜて「できない」と書くと、環境が変わっても誰も測り直さない。B-2 へ「**最後に測った時点: 2026-08-14 / #473**」と再測定手順（`gh auth status` → `gh api …/permissions` → ガイド §6 の読み直し）を書いた。**#473 基準 3 は blocked:human の記録として消化し、設定の実施は利用者の操作として残る。**

## ★ 決定 3: **`claude-review` は「必須にできる状態」にしてから候補に挙げる**

`claude-code-review.yml` の `types:` へ **`reopened` を足した**（1 行）。無いと close → reopen した PR で起動せず、必須化した瞬間に当該 PR が恒久 pending になる。**足さずに「必須にせよ」と書けば、手順どおりにした人が詰む**——決定 1 で見つけた誤りと同じ型の被害を、こちらは書く前に潰した。他の PR 起動ワークフロー（ci / security / pr-title）は実測ですべて `reopened` を持つ。

あわせて手順書へ 2 点を明記した: **必須化で担保できるのは「完走」であって「指摘なし」ではない**（claude-review は 🔴 でも success を返す。#473 の文言「claude-review **完了**」と一致）／AI 基盤の停止・トークン失効で**全 PR がマージ不能になる**副作用。

## ★ 決定 4: **blocked 判定の再検証を週次監査の観点に足す**（#473 基準 4）

`backlog-audit.yml` の監査プロンプトへ観点 5「**blocked 判定の再検証**」を追加した。環境固有の観測（その環境で失敗した・権限が無かった）を恒久制約（原理的に AI にできない）と誤分類していないかを、`docs/blocked-tasks.md` の「なぜ AI にできないか」列に対して点検する。**根拠は MSP の実測**——「AI だけでは完結しない」と保留された 3 件（MSP#554 / MSP#556 / MSP#562）が別環境で同日中に着地した（MSP#617）。本リポにも前例がある: A-4 の「日銀サイトが自動取得を拒否している」は環境依存の観測の一般化であり、実測で覆った（2026-08-05 の訂正）。

## 決定 5: **検査器・回帰テストは足さない**

MSP は「`reopened` 欠落」の同型事故 2 回目で検査器を足したが（MSP/IADR-0182）、**本リポでの発生はまだ 0 回**（先例に学んで事前に直した）。「検査器・規約の追加は同型事故 2 回から」（CLAUDE.md・ガイド §6）に従い、文書と 1 行の修正に留める。`paths:` の側も機械検査にしない（`helm.yml` が意図して `paths:` を持ち、必須にしないことで正しく運用されている。例外が在る規則は検査器にできない）。

## 結果

- 良い影響: **手順書どおりに設定して壊れる状態が消えた**。B-2 が「いつ・何を測ったか」を持ち、棚卸しで再検証できる。週次監査が blocked の誤分類を毎週点検する
- 悪い影響・トレードオフ: **ブランチ保護は依然として未配備**。本 PR が動かしたのは手順の正しさであって統制の有無ではない（暫定手段を手順書と B-2 に併記した）
- フォローアップ: 設定の実施は利用者の操作（決定 2）。キット側の同型欠陥は MSP が環流済み（MSP/IADR-0182 フォローアップ）のため二重環流しない

## 関連

- Supersedes: なし
- Superseded by: なし
