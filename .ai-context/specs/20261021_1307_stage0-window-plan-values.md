---
title: Stage 0 の評価の窓 365 暦日と DSR 0.95 が計画の値であることを IADR に残し、コードの既定と判定用の構成を計画の値と突き合わせる試験を置く（#1307・ADR-0069 フォローアップ 1）
type: spec
status: done
related_ids: [FR-15, FR-20, ADR-0069, ADR-0033, ADR-0064, ADR-0036, ADR-0039, IADR-0110, IADR-0337, IADR-0528, IADR-0554, IADR-0563]
author: claude (Claude Code)
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0069_stage0-evaluation-window-365-days-and-dsr-threshold.md
  - planning#806（裁定 2026-10-21）
---

# 仕様書: Stage 0 の評価の窓と DSR の閾値を計画の値と突き合わせる（#1307）

> 本仕様書は実装着手前に作成した（着手 2026-10-21。AST `origin/develop` `179e1e80`＝計画 ADR のレンジを 0071 へ引き直した #1404 の後）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-15**、FR-20
- 関連 ADR: **ADR-0069 決定 1・2・5・フォローアップ 1**（原文を読んだ。計画 `origin/main` `577d21f`）、ADR-0033、ADR-0064 決定 3、ADR-0036 決定 2、ADR-0039 決定 2・3
- 裁定: planning#806 の 2026-10-21 のコメント「残作業（実装）」
- 新規 IADR: **IADR-0563**
- issue: #1307（`Closes`。理由は下）／切り出し #1405（窓が届いた後の報告）

## #1307 を閉じる理由

#1307 の受け入れ基準 1〜5 は「3 点を計算して計画の環流 issue に報告する」であり、planning#806 の起票（#1307 の 2026-10-1x のコメント）で満たされた。ADR-0069 決定 6 も「ADR-0064 決定 3 の依頼 3 点は、環流 #806 で満たされた」とする。
窓が 365 暦日に届いた後の報告（ADR-0069 フォローアップ 2・3）は ADR-0069 が新たに定めた義務で、時期が 2027-07-01 以降と 8 か月以上先である。#1307 を開けたままにすると「3 点の報告」の題で別の義務を抱え続けるため、#1405 に切り出して `blocked`・再検証期限 2027-07-01 で追う。

## 母集合（規則 9・10）

走査語: `LookbackDays`・`Backtest__Stage0__LookbackDays`・`MinDeflatedSharpe`・`0.95`（BacktestService と構成）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `Hosted/Stage0EvaluationOptions.cs` の既定 365 | 公開定数 `LookbackDaysPlanValue` に寄せる |
| 2 | `Domain/Stage0Gate.cs` の `Default`（0.95 直書き） | 公開定数 `MinDeflatedSharpeDefault` に寄せる |
| 3 | `appsettings.Development.json`（365） | 試験の対象（列挙） |
| 4 | `appsettings.json`（Stage0 の節なし＝既定） | 試験の対象（列挙） |
| 5 | `values.yaml`（書かない＝既定） | 試験の対象（列挙） |
| 6 | `values-grpc-measurement.yaml`（backtest に LookbackDays なし） | 試験の対象（列挙） |
| 7 | `values-local.yaml`（24） | **除外**（PoC の経路の確認用。ADR-0069 決定 5） |
| 8 | `docker-compose.yml` | Stage0 の構成なし（DSR の構成の口が無いことの検査だけに入れる） |
| 9 | InformationCollection の `LookbackDays`（FINRA 7 日）・`NewsLookbackDays` | 別機能。対象外 |
| 10 | `Stage0GateCriteriaTests` の `0.95` の固定 | 残す（較正の記録の試験） |
| 11 | `docs/functional/FR-15_backtest.md` の評価期間・DSR の行 | 計画の値である旨を足す |

## 受け入れ基準

- [x] AC-1: 窓 365 暦日と DSR 0.95 が計画の値であることを IADR に記録する（IADR-0563）
- [x] AC-2: コードの既定と公開定数が計画の値と一致する（`Stage0EvaluationWindowPlanValueTests` の 2 件）
- [x] AC-3: 判定用の構成（appsettings*.json・values*.yaml を列挙）の `LookbackDays` が 365 か未記載（同 2 件）
- [x] AC-4: PoC の構成だけを名前で除き、除外の名前が実在しないと赤（同）。否定形: 除外しなければ PoC の 24 が検知される（同 1 件）
- [x] AC-5: DSR の閾値を書く構成が無い（同 1 件）
- [x] AC-6: カットオフ 2026-06-30 で窓が届くのは終端 2027-07-01（同 1 件）
- [x] 変異: values.yaml に `30`・Development に `200`・`Default` を 0.9 → それぞれ赤（3 件 Failed）

## 範囲外

- 窓が届いた後の報告（#1405）。
- 365 暦日に満たない窓の評価を合格の根拠にしない機械の検査（ADR-0069 決定 7 の表で人の手順のまま）。
