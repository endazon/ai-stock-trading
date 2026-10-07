---
title: IADR-0504 計画 ID レンジ宣言のずれは週次監査の前段が専用 issue へ起票し、宣言の不読は exit 1 にする
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0029, MSP:ADR-0093, IADR-0170, IADR-0320]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0093_plan-id-ranges-derived-and-published.md (決定 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0029_impl-docs-restructure.md (決定 2)
---

# IADR-0504: 計画 ID レンジ宣言のずれは週次監査の前段が専用 issue へ起票し、宣言の不読は exit 1 にする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-08
- 決定者: claude（#1208 の案 1 と受け入れ基準に沿って起案）

## 起点・関連

- 関連する計画書 ID: 無採番 NFR（メタ作業）。計画 `MSP/ADR-0093` 決定 3（実装リポの突合は読み取り専用の HTTP・警告に限り、ビルドの前提にしない）、計画 ADR-0029 決定 2（planning 非依存）
- 対象 Issue: [#1208](https://github.com/endazon/ai-stock-trading/issues/1208)（#1199 受け入れ基準 3 の後続）。対の issue は MSP#1775
- 関連する実装仕様書: [20261008_1208_plan-range-lag-issue](../specs/20261008_1208_plan-range-lag-issue.md)
- 関連 IADR: [IADR-0320](IADR-0320_plan-id-range-sync-via-published-ranges.md)（突合の出典と走査件数。決定 3 の一部を本 IADR が改める）、[IADR-0170](IADR-0170_backlog-audit-automation.md)（週次監査。決定6 の「自動でクローズしない」を引き継ぐ）

## コンテキストと課題

`.claude/rules/traceability.repo.md` の計画 ADR レンジ宣言が計画側への ADR 追加に遅れた事例は 3 回目である。

| 回 | 日付 | 見つけた経路 | 遅れ |
| --- | --- | --- | --- |
| 1 | 2026-09-09 | 横断監査 A-3 → #710 | `0032` に対し実物 `0035` |
| 2 | 2026-10-05 | 週次監査 #483 §6（本突合の `behind`） | `0051` に対し実物 `0052` |
| 3 | 2026-10-07 | 第 4 回全体監査 B-1 → #1199 | `0053` に対し実物 `0055` |

遅れの窓では、実在する ADR を引くコミット件名・PR タイトル・trace ブロックが `check-commit-messages.js` /
`check-trace-blocks.js` に「存在しない ID」として拒否される。

🔴 **2 回目と 3 回目の間で、検知は動いていた。** 2026-10-05 の週次監査は `status: behind`・`scanned: 4` を正しく報告し、
「この遅れを追う open issue は見当たらない（起票候補）」と書いた。**誰も引き直さず、2 日後に全体監査が同じ遅れを拾った。**
#1208 の案 1 は「週次で突き合わせ、issue を起票するか報告に載せる」だが、**「報告に載せる」は既に実施済みで、効かなかった。**

## 検討した選択肢

| # | 案 | 評価 |
| --- | --- | --- |
| A | **週次監査の前段（決定的なステップ）が、ずれのとき専用 issue を upsert する** | **採用**。報告の 1 行ではなく、ラベル付きの単独の作業項目になる。引き直し PR が `Closes #N` で閉じれば窓の終わりが記録に残る。AI の判断に依らない（前段は決定的） |
| B | 報告に載せるだけ（現状。#1208 の案 1 の前半） | 採らない。2026-10-05 に実施済みで効かなかった（上表 2 → 3） |
| C | 監査の AI に起票させる | 採らない。プロンプトの指示は「起票候補」で止まった実績があり、AI の判断に依る。決定的な前段で足りる |
| D | 引き直し PR の自動作成 | 採らない（本 issue の範囲外）。`contents: write` と、CI を起動できる token（GITHUB_TOKEN で作った PR は CI を起動しない）が要る。転記元の実測を人が確かめる手順も外れる |
| E | 計画側の ADR 追加 PR のチェックリストに起票を足す（#1208 の案 2） | 本 PR では扱わない。planning#742 が計画側へ同趣旨を既に提案している |
| F | 現状維持（全体監査で拾う。#1208 の案 3） | 採らない。窓が監査の間隔になる |

## 決定

### 決定 1: ずれ（`behind` / `ahead`）を検知したら、前段が専用 issue を upsert する

- `scripts/check-planning-adr-range.js --upsert-issue`。ラベル `plan-range-lag`・マーカー `<!-- plan-range-lag -->` の open issue を探し、
  在れば本文を上書きし、無ければ作る（`GITHUB_TOKEN`・`issues: write`。ジョブは元から持つ）。
- 本文は種別ごとの表・`reason`・引き直しの手順（転記元は計画リポの `gen-plan-ranges.js --check` の実測・別紙への追記・`Closes #N`）。
- **自動でクローズしない**（IADR-0170 決定6）。`ok` / `unverified` / `error` では API を呼ばない。
- 起票の結果を JSON の `lagIssue`（`action` と `number`。失敗なら `reason`）へ残し、監査の AI はそれを報告に添える（自分では起票しない）。

### 決定 2: 計画側に届かないときは exit 0 のまま、明示の skip にする

計画 `MSP/ADR-0093` 決定 3（落とし方は警告に限り、取得に失敗しても本体は通る）を維持する。`unverified`・`scanned: 0`・warning と、
監査報告 §6 の「未確認」で明示する。**黙って `ok` にはしない**（IADR-0320 決定 3 のまま）。

### 決定 3: 宣言が読めないときは `unverified` ではなく `error`・exit 1 にする（IADR-0320 決定 3 の一部を改める）

宣言が読めない（節が無い・書式が崩れた）のは計画側の到達性ではなく本リポジトリの欠陥である。`readPlanIds()` /
`readPlanAdrRange()` が例外で落とすのと揃える。IADR-0320 決定 3 は「宣言不読も `unverified`・exit 0」としていたが、
これを `unverified` に混ぜると「計画側に届かない」と区別できない。ずれを検知したのに起票できない（token 不在・API 失敗）ときも exit 1 にする。
監査本体（Claude ステップ）は `if: ${{ !cancelled() }}` で前段の失敗に巻き込まれずに走る。

## 理由

- 3 回の事故のうち、検知が動いていたのに止められなかった回（2 → 3）が本件の核心であり、手当てすべきは「検知 → 作業」の段である。
- issue は人とエージェントが日常に見る作業の単位であり、報告の 1 節より埋もれにくい。マーカーで 1 件に保つので週次で増えない。
- planning 非依存（計画 ADR-0029 決定 2）を崩さない。計画側の取得は従来どおり公開 `kg-ranges.json` の読み取り 1 回で、
  PR CI・必須チェックには入れない。新たに足す書き込みは本リポジトリの issue だけである。

## 結果

- 良い影響: ずれは検知の週のうちに単独の issue になる。窓の開始（起票）と終了（`Closes`）が記録に残り、計画 `MSP/ADR-0093` フォローアップ 4 の
  「宣言と実物の差が 0 でない日数」を issue の開閉から数えられる。
- 悪い影響・トレードオフ:
  - 窓は最大 7 日のまま（週次）。今回の 3 件は窓より「拾った後に動かない」が主因だったため、頻度は据え置いた。
  - 宣言が崩れた週は前段が赤になり、`backlog-audit.yml` のジョブが失敗する（本ワークフローは `ci-failure-issue.yml` を呼んでいないので、ジョブの赤で見る）。
- フォローアップ: 窓を縮める必要が出たら、前段だけを日次の軽いワークフローへ分ける（本 IADR の決定はそのまま使える）。

### 実測した変異

自己試験 25 件・`scripts.repo.test.js` の新設 2 件に対し、次の変異はすべて落ちた。

| 変異 | 落ちた試験 |
| --- | --- |
| `exitCodeFor` を常に 0 | 自己試験・実バイナリ |
| 宣言不読を `unverified` へ戻す | 自己試験・実バイナリ |
| issue 本文からマーカーを落とす | 自己試験 |
| 既存 issue をマーカーで絞らない | 自己試験 |
| `behind` 判定を `>` から `>=` へ | 自己試験 |
| `behind` を起票対象から外す | 自己試験 |
| ワークフローから `--upsert-issue` を外す | `scripts.repo.test.js` |
| 監査本体の `!cancelled()` を外す | `scripts.repo.test.js` |

## 関連

- Supersedes: なし（IADR-0320 決定 3 の「宣言不読も exit 0」の 1 点だけを改める。IADR-0320 は他の決定が有効なため Accepted のまま）
- Superseded by: なし
