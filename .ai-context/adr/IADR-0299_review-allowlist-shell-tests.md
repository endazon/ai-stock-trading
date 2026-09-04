---
title: IADR-0299 AI が実走できるシェルテストは CI が実走している 3 本だけを列挙で許可し、CI との非対称を検査で固定する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0145, IADR-0190]
author: claude (Claude Code)
created: 2026-09-04
updated: 2026-09-04
plan_refs: []
related_specs:
  - ../specs/20260904_683_review-allowlist-shell-tests.md
---

# IADR-0299: AI が実走できるシェルテストは CI が実走している 3 本だけを列挙で許可し、CI との非対称を検査で固定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

## 起点・関連

- 関連する計画書 ID: なし（AI 運用装備の是正。`NFR` 無採番＝工程のメタ作業）
- 関連する実装仕様書: [20260904_683_review-allowlist-shell-tests](../specs/20260904_683_review-allowlist-shell-tests.md)
- 起点 issue: [#683](https://github.com/endazon/ai-stock-trading/issues/683)
- 関連 IADR: [IADR-0145](IADR-0145_permission-denial-fixability-classification.md)（権限拒否を
  「許可リストで直せるか」で分類し、直せる拒否だけで失敗判定する。**本件はその「直せる拒否」が
  2 PR にわたって直されないまま放置されていた事例である**）、
  [IADR-0190](IADR-0190_review-verdict-gate.md)（レビュー判定ゲートと検証の絞り込み）

## コンテキストと課題

`.github/workflows/claude-code-review.yml` の `--allowedTools` には 56 件の許可があるが、
**`bash` / `sh` で始まる許可が 1 件も無い**。一方で `.github/workflows/ci.yml` の `static-checks`
ジョブは **3 本のシェルテストを `bash <path>` で実走している**。

```yaml
- run: bash scripts/k8s-local-deploy.test.sh
- run: bash deploy/opend/entrypoint.test.sh
- run: bash scripts/cutover-count-reconcile.test.sh
```

**CI 自身が実行しているものを、レビュワーだけが実行できない。** この非対称が、シェルテストを
触る PR で毎回 `claude-review` を赤くしていた（#678 で 6 件・#647 で 5 件の拒否）。

`develop` のルールセットは `pr-title` しか必須にしていないため、この赤はマージを止めない。
**止めないまま、シェルテストの主張だけが未検証で通過し続ける**という劣化が本質である。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | 何もしない（拒否を許容する） | ❌ 2 PR で再発済み。`check-permission-denials` が毎回赤くなり、他の指摘が埋もれる |
| B | `Bash(bash:*)` を許可 | ❌ **任意のシェルコマンドの実行**と等価。PR が持ち込んだ `.sh` も、ワンライナーも通る |
| C | `Bash(bash scripts/*.test.sh:*)` の glob | ❌ PR が追加した `*.test.sh` に**自動で**実行権が付く。許可の拡大がレビューを経ずに起きる |
| **D（採用）** | **CI が実走している 3 本を、そのパスで列挙** | ✅ 新しい能力の階級を持ち込まない。許可の拡大にワークフロー変更＝レビューが要る |
| E | `check-permission-denials.js` の側で許す | ❌ 拒否は依然として起き、**レビュワーは実走できないまま**。症状だけ隠す |

## 決定

### 決定 1: CI が `bash <path>` で実走している 3 本だけを許可する

`claude-code-review.yml` / `claude-coding.yml` の `--allowedTools` と `.claude/settings.json` の
`permissions.allow` へ、次の 3 件を足す（**3 系統一致**）。

```
Bash(bash scripts/k8s-local-deploy.test.sh:*)
Bash(bash deploy/opend/entrypoint.test.sh:*)
Bash(bash scripts/cutover-count-reconcile.test.sh:*)
```

**`claude-coding.yml` にも足す理由**: これらのテストを書き換えるのは実装側である。書き換えた者が
実走できないなら、レビュワーが実走できても片手落ちになる。

### 決定 2: glob にせず列挙する（許可の拡大を必ずレビューに掛ける）

`Bash(bash scripts/*.test.sh:*)` は書かない。glob にすると、**PR が `scripts/evil.test.sh` を
追加するだけで、その PR 自身のレビュー中にそれが実行される**。列挙であれば、新しいシェルテストを
AI に実走させたい時に `--allowedTools` の変更が要り、**その変更自体が差分としてレビューに載る**。

### 決定 3: 呼び方は `bash <path>` の 1 形へ寄せる

`sh <path>` も `./<path>` も許可しない。Bash の許可は**コマンド文字列の前方一致**であり、
呼び方を増やすほど「許可した形」と「実際に呼ぶ形」がずれる面が増える。**CI と同じ 1 形**に
揃えておけば、CI で緑になったコマンドはそのままレビュワーでも通る。

プロンプト（【検証の実行】節）に、拒否される形を明示した:
`sh <path>` / `./<path>` / `cd <dir> && bash …` / `VAR=1 bash …`。
出力を絞る `| tail -20` は**パイプが各コマンドを個別に判定する**ため問題なく通る。

### 決定 4: 非対称そのものを検査で固定する（`shellTestDrift`）

`scripts/check-ai-workflow-config.js` へ `shellTestDrift()` を新設した。**`ci.yml` から
`run: bash <path>.test.sh` を正規表現で抽出し**、`claude-coding.yml` / `claude-code-review.yml`
の許可に `Bash(bash <path>:*)` があるかを突き合わせる。無ければ CI を赤くする。

**`ci.yml` を単一情報源にした**のが要点である。スクリプト名を検査器へ直書きすると、新しい
シェルテストを CI へ足した時に検査器の側が古いままになり、同じ穴が再発する。CI に足した瞬間に
許可の不足が赤くなる。

`ci.yml` が読めない場合（キット配布先で名前が違う等）は **skip する**（fail-open）。この検査は
「CI にあるものが AI に無い」ことだけを見ており、CI が読めないなら比較する基準が無い。

### 決定 5: 「同型事故 2 回」の条件を満たしたうえで足す

CLAUDE.md は「検査器・規約の追加は同型の事故が 2 回起きてから」と定める。本件は
**#647（8 件中 5 件）と #678（6 件すべて）の 2 回**で条件を満たす。1 回目の時点で足していれば
早かったが、規約は「1 回目は記録に留める」であり、それに従った結果である。

## 安全性の評価（`on: pull_request` で許可を足すことについて）

| 論点 | 評価 |
| --- | --- |
| 新しい能力の階級が増えるか | **増えない。** `Bash(dotnet test:*)` / `Bash(npm run:*)` が既にあり、**PR が書いたコードの実行**は元から許可されている。シェルテストはその部分集合である |
| fork PR から悪用できるか | 本ワークフローは `on: pull_request`（`pull_request_target` ではない）。fork PR には secrets が渡らず、アクションは認証できないため**そもそも起動しない** |
| PR が 3 本の中身を書き換えたら | **書き換えられる。** ただしそれは `dotnet test` / `npm run test` でも同じである。差分がレビュー対象に載ることが唯一かつ既存の防御であり、本変更はその前提を変えない |
| 副作用のあるスクリプトが動くか | 動かない。`k8s-local-deploy.test.sh` は helm / kubectl を**スタブに差し替えて**動く自己完結のテストである（実クラスタに触らない）。`entrypoint.test.sh` は `AST_OPEND_LIB=1` で関数だけを読み込む。`cutover-count-reconcile.test.sh` も同様 |
| 許可が黙って広がるか | 広がらない（決定 2）。**新しい `*.test.sh` は明示的な `--allowedTools` 変更なしには実行されない** |

## 影響

- レビュワーが「78 件緑」のような PR 本文の主張を**自分で追試できる**ようになる。
- `check-permission-denials` の赤が、この系統では出なくなる。
- **CI にシェルテストを足すと、許可を足すまで `check-ai-workflow-config` が赤くなる**（意図した挙動）。

## 残余リスク

- **`claude-review` は必須 check ではない**（`develop` ルールセット `18662050` は `pr-title` のみ）。
  本 IADR はレビュー品質の劣化を直すが、**必須化そのものは別問題**である（#501 / #644 のブランチ
  保護整備で扱う）。
- 検査は `ci.yml` の `run:` 行の**書式**に依存する（`run: bash <path>` の 1 行形）。複数行 `run: |`
  の中へ移すと抽出されず、黙って 0 件検査へ落ちる。**`wanted.size === 0` で早期 return するため
  fail-loud にはならない** —— これは「ci.yml が読めない環境で skip する」ことと同じ入口を
  共有しているためであり、書式を変える時は本 IADR を読むこと。
