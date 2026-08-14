---
title: 運用ガイドの機械的強制化（PR サイズ検査・issue 受け入れ基準欄・required check 化の記録・blocked 再検証）
type: spec
status: approved
related_ids: [NFR, IADR-0184, IADR-0185]
author: endazon (with Claude Code)
created: 2026-08-14
updated: 2026-08-14
---

# 仕様書: 運用ガイドの機械的強制化（#473）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#473](https://github.com/endazon/ai-stock-trading/issues/473)
- 起点 ID: **NFR**（運用保守）
- 計画リポの正本: `planning/docs/ai-implementation-workflow-guide.md` §1・§4・§6（fixed・2026-08-08）
- 参照実装 2 系統:
  - キット `planning/tools/impl-handoff-kit/repo-template/`（pin `cff0e7b`。kit `e0bc81c` が pr-size.yml と issue テンプレ改定を配布）
  - microservices-platform の同型対応（MSP PR #702 / #704 / #706・IADR-0180〜0182 相当）
- 先例: [#472](https://github.com/endazon/ai-stock-trading/issues/472)（ガイドの組み込み / [作業仕様書 20260808_472](20260808_472_workflow-guide-integration.md)）

## 背景

ガイドのうち機械的に強制できる項目を CI・テンプレートへ落とし、「規範」から「統制」へ格上げする。規律だけで保たれている状態は、崩れても検知されない。

## やること（受け入れ基準との対応）

| # | 基準 | 実装 |
| --- | --- | --- |
| 1 | PR サイズ検査（400 行超で警告・マージは止めない） | キットの `pr-size.yml` を `.github/workflows/` へ移植。**しきい値・warn 方式・警告文・permissions は 1 文字も変えず、`EXCLUDES` だけを本リポ向けに較正する**（キットが明示した調整点）。較正の判断と実測は [IADR-0184](../adr/IADR-0184_pr-size-check-calibration.md) |
| 2 | issue テンプレートの改定 | `ai-implementation.yml` の acceptance 欄へ Given-When-Then 形式の説明（「〜できること」だけの記述を避ける）を追加し、`file_scope`（触るファイル領域の宣言・必須）欄を追加する。MSP の同ファイルと同一の欄構成 |
| 3 | required check 化 | **設定そのものはリポジトリ管理者権限が要るため blocked:human**。`docs/blocked-tasks.md` B-2 を「能力の不在」と「規則による禁止」を書き分けた記録へ更新し（最後に測った時点・再測定手順つき）、`docs/ai-workflow.md` の必須チェック手順を**実在する check 名**へ正す（従前はワークフロー名 `CI` / `Security` を挙げており、そのとおり設定すると develop が恒久的にマージ不能になる）。あわせて `claude-code-review.yml` の `types:` へ `reopened` を足し、claude-review を「必須にできる状態」にする。判断は [IADR-0185](../adr/IADR-0185_required-check-contexts-and-blocked-record.md) |
| 4 | blocked 再検証 | `backlog-audit.yml` の監査プロンプトへ「blocked 判定の再検証（環境固有の観測を恒久制約と誤分類していないか）」の観点を追加する。根拠は MSP の誤分類実測（#554 / #556 / #562 → #617。「AI だけでは完結しない」と保留された 3 件が別環境で同日中に着地した） |

あわせて planning submodule の pin を `90f5251` → **`cff0e7b`** へ独立コミットで前進させる（キットの参照実装を含めるため）。

## pin の前進

| | 値 |
| --- | --- |
| 変更前 | `90f5251` |
| 変更後 | **`cff0e7b`** |

範囲内で `projects/ai-stock-trading/` に触れるのは 4 ファイル。`02_requirements/01_requirements.md` は非機能要件表への **NFR-XX の ID 付与のみ**（値の変更なし・機能要求節はハッシュ一致を実測）、`ADR-0003` は追補のみ（`## 決定` 節は不変）、ほかは INDEX / 07_adr/README。`PlanSourceDigests` が指す全節のハッシュ一致を実測済みであり、`PlanRiskDefaults` の再照合は不要（#459 検査2 と同じ論法）。

## 受け入れ基準（Given-When-Then）

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | Given 較正済み EXCLUDES / When 実装 diff 追加 400 行超の PR / Then `pr-size` ジョブが step summary へ警告を出し、**ジョブは成功のまま**（マージを止めない） | ワークフロー定義（`if:` は warn ステップのみ・`exit 1` なし）＋ 本 PR での実走 |
| 2 | Given 新テンプレ / When issue 起票 / Then acceptance 欄に GWT の説明と例が出て、`file_scope` が必須欄として存在する | YAML の `validations.required: true` |
| 3 | Given B-2 の記録 / When 読者が再測定する / Then 「最後に測った時点」と再測定手順が読め、能力の不在と規則の禁止を混同しない | `docs/blocked-tasks.md` B-2 |
| 4 | Given 監査プロンプト / When 週次監査が走る / Then blocked 再検証の観点（誤分類の検査）が監査 5 点目として指示される | `backlog-audit.yml` のプロンプト本文 |
| 5 | `node scripts/check-commit-messages.js origin/develop..HEAD` と `node scripts/check-doc-links.js` が緑 | ローカル実測＋CI |

## やらないこと

- **しきい値 400 の変更**（正本 §1 と issue の両方が名指しした数値。動かすなら実測と裁定が要る）
- **ブランチ保護の設定そのもの**（管理者権限。B-2 に blocked:human として記録し、手順を正すまでが本 PR）
- **`paths:` フィルタの機械検査**（正当に `paths:` を持つワークフローがあり、例外なしと言い切れない規則は検査器にしない）
- **`reopened` の有無を固定する回帰テストの新設**（本リポでの同型事故はまだ 0 回。「検査器・規約の追加は同型事故 2 回から」の統制に従う）
