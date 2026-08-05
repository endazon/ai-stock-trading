---
title: AI レビューの許可リストに frontend（npm/Playwright）と dotnet ef を足し、拒否の失敗判定を「許可リストで直せるもの」に限定する
type: spec
status: draft
related_ids: [NFR, IADR-0145]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs: []
---

# 仕様書: AI レビューの許可リスト是正と、拒否の失敗判定の見直し（#391）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: — （非機能・NFR）
- 対象 Issue: [#391](https://github.com/endazon/ai-stock-trading/issues/391)
- 関連: `AI_SETUP.md` 共通セットアップ 5（同種の退行が 2 週間継続した実績あり）／#373・#369（先行作業）
- 関連 IADR: **IADR-0145**（本作業で起こす）

## 目的・背景

`claude-code-review.yml` のレビューが、**指摘ゼロでもジョブとして失敗**する事象が起きている（#390）。あわせて、`frontend/` の typecheck・vitest と `dotnet ef migrations has-pending-model-changes` の主張を **AI レビューが検証できていない**。

## 着手前の実測 — issue の前提が 1 つ古い

**#391 は「拒否が 1 件でもあるとジョブが落ちる構成」と書いているが、これは現状と異なる。** `scripts/check-permission-denials.js` は既に段階ポリシーを実装しており、**既定は「4 件超、またはターン数の半分以上」で失敗**する（拒否 1 件では落ちない）。

実際に失敗した run（`30998236984`）のログを確認したところ、原因は **5 件**でしきい値 4 をわずかに超えたことであった。

```
##[error]AI の実行中にツールの権限拒否が 5 件発生した:
  Bash(git -C /home/runner/work/ai-stock-trading/ai-stock-trading log)（1 件）
  Bash(git -C /home/runner/work/ai-stock-trading/ai-stock-trading ls-files)（1 件）
  mcp__github__get_issue_comments（1 件）
  mcp__github__get_pull_request_reviews（1 件）
  mcp__github__search_issues（1 件）
```

**内訳も issue の想定（npm / dotnet ef）と違う。** したがって本作業は「許可リストを足す」だけでは終わらない。**何を足すべきかを実測から決める**必要がある。

## 対象範囲

- **対象**:
  1. 3 系統の許可リスト（`.claude/settings.json` / `claude-coding.yml` / `claude-code-review.yml`）へ、frontend（npm / npx / Playwright）と `dotnet ef`、および**実測で拒否された読み取り系**を追加する
  2. レビュージョブに frontend と `dotnet-ef` のセットアップを足し、**許可しただけで実行できない状態を作らない**
  3. `check-permission-denials.js` の失敗判定を「**許可リストで直せる拒否**」に限定する（②の判断）
- **対象外**:
  - レビュープロンプトの観点そのものの見直し
  - `frontend-e2e` ジョブ（CI 側）の変更

## 設計

### 1. 許可リストへ追加するもの

| 追加 | 理由 |
| --- | --- |
| `Bash(npm ci:*)` / `Bash(npm run:*)` / `Bash(npm --prefix:*)` / `Bash(npx:*)` | frontend の typecheck・vitest・Playwright を実走させる（#391 の本質） |
| `Bash(dotnet ef:*)` | `migrations has-pending-model-changes` / `migrations list` |
| `Bash(git ls-files:*)` | **実測で拒否された。** 読み取り専用 |
| `mcp__github__get_issue_comments` / `get_pull_request_reviews` / `search_issues` | **実測で拒否された。** いずれも読み取り専用 |

**`Bash(git -C <絶対パス> …)` は許可リストで解決しない。** `Bash(git -C:*)` の一括許可は書き込み系まで通るため既存の設計が明示的に禁止している。**プロンプト側で「`git -C` は `planning` の相対パスのみ」と指示する**（絶対パスを使うと必ず拒否される）。

### 2. レビュージョブのセットアップ

`ci.yml` の `frontend-e2e` ジョブと同じ形を使う（既に実績のある手順を複製する）。

```yaml
- uses: actions/setup-node@v7
  with:
    node-version: "20"
    cache: npm
    cache-dependency-path: frontend/package-lock.json
- name: Install frontend deps
  run: npm ci
  working-directory: frontend
- name: Install Playwright browser (chromium)
  run: npm run e2e:install
  working-directory: frontend
- name: Install dotnet-ef
  run: dotnet tool install --global dotnet-ef
```

**許可リストへ足すだけでは実行できない。** `node_modules` が無ければ typecheck も vitest も動かず、`dotnet ef` は tool manifest が無いためグローバル導入が要る。**「許可したのに実行できない」は、拒否されないぶん質が悪い**（レビューは「実行したが失敗した」と報告し、原因が環境不備だと分からない）。

### 3. ②の判断 — 失敗判定を「許可リストで直せる拒否」に限定する

**現状の段階ポリシー（件数しきい値）は、性質の違う 2 種類の拒否を同じ土俵で数えている。**

| 種別 | 例 | 許可リストで直せるか |
| --- | --- | --- |
| **A. 許可漏れ** | `npm ci` / `dotnet ef` / `git ls-files` / MCP ツール | ✅ **直せる** |
| **B. 構文上ありえない形** | `VAR=1 cmd`（env 前置き）・`> file`（リダイレクト）・`<(…)`（プロセス置換）・`for`/`while`（ループ）・`cd` | ❌ **原理的に直せない** |

B はレビュープロンプトが既に「試みる必要はない」と明示しているが、**モデルが試みてしまえば拒否として計上される**。B を数に入れると、**許可リストを完璧にしても偽の赤が出続ける**。逆に件数しきい値を上げると A の検出が鈍る。#391 が「維持すると偽の赤が出続け、緩めると許可漏れに気づけなくなる」と書いたトレードオフは、**この 2 種類を分けていないことから生じている。**

**採用する設計**: 拒否を A / B に分類し、**失敗判定は A の件数のみで行う**。B は件数に関わらず**警告として必ず可視化する**（アノテーション＋実行サマリ）。

- しきい値（既定 4）は**据え置く**。分母から B が抜けるぶん、A に対しては**実質的に厳しくなる**。
- 「見えるが緑」と「見えずに緑」を混同しない、という既存の方針は維持する。B も必ず表示する。
- 棄却案: **しきい値を上げる** — A の検出が鈍る。**ゲートを外す** — 本検査器を入れた目的（レビュー未実施を見逃さない）が消える。**B をプロンプトで根絶する** — 既に試みて残っている（3 巡分の実測がコメントに残っている）。

### 4. 3 系統の同期の検査

`scripts/check-ai-workflow-config.js` が既に「実装用とレビュー用でスタック別の実行ツールが食い違う」を検査している。**追加した npm / dotnet ef が 3 系統で揃っていることを、この検査器が拾える形にする**（拾えないなら検査対象へ足す）。

## 受け入れ基準

- [ ] AI レビューが `frontend` の typecheck / vitest を**実際に実行**できる（許可リスト＋セットアップの両方）
- [ ] AI レビューが `dotnet ef migrations has-pending-model-changes` を実行できる
- [ ] Playwright を許可し、レビューで E2E を実走できる（利用者裁定 2026-08-05・①）
- [ ] 3 系統の許可リストが揃っている（`ai-workflow-config` ジョブが緑）
- [ ] **構文上ありえない拒否（B）だけではジョブが赤にならない**
- [ ] **許可漏れ（A）が 4 件を超えれば従来どおり赤になる**（ゲートを弱めていない）
- [ ] 分類のトレードオフを IADR に残す（#391 やること 3 の要求）

## テスト方針

- `check-permission-denials.js --self-test` に **A/B 分類の否定形テストを正の確認と同数以上**置く。
  - B のみ 10 件 → 失敗しない
  - A が 5 件 → 失敗する
  - A 3 件 + B 10 件 → 失敗しない（A が許容内）
  - A 5 件 + B 0 件 → 失敗する
  - **B を A と誤分類したら落ちる**変異テスト（分類器が壊れたら気付ける）
- `scripts.test.js` から自己試験を呼び、CI で毎回走らせる（既存の形に合わせる）。

## 計画書との差異

- 差異: なし（NFR。計画書に対応する記述は無い）

## 未決事項

1. **Playwright のブラウザ取得がレビュー時間を延ばす。** `npm run e2e:install`（chromium）は 1〜2 分かかる。利用者裁定（①）により許可するが、**レビューの所要時間が伸びることを受け入れる**。
2. **`git -C <絶対パス>` は許可リストで解決できない。** プロンプトで抑止するが、モデルが従わなければ B として計上される（失敗はさせない）。
