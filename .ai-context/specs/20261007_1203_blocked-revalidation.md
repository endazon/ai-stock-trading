---
title: blocked 判定の再検証の結果を issue と blocked-tasks.md へ反映する（#1203）
type: spec
status: accepted
related_ids: [NFR, ADR-0019, ADR-0025, ADR-0026, IADR-0185]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0026_short-fee-rate-unit-poc.md（決定 2: 確定手段は moomoo のドキュメント／サポートへの照会、決定 3: 期限 2026-08-31）
  - planning:projects/ai-stock-trading/06_technical/03_moomoo-integration.md:24,35（日本株の時価取得・現物発注は 2026-06〜）
---

# 仕様書: blocked 判定の再検証の反映（#1203）

## 起点

- 起点 issue: #1203（第 4 回全体監査 2026-10-07 の指摘 B-17・C-1）
- 起点 ID: **NFR**（無採番。運用ガイド §6「blocked 判定は棚卸しごとに再検証する」）。規律は IADR-0185 決定 4
- 監査記録: planning `draft/cross-project/20261007_poc-operational-overall-audit.md` §9.1（planning#743 のブランチ `claude/sleepy-shannon-ayoxjs`。**`origin/main` には未マージ**のため PR ブランチから読み取りのみで読んだ）
- 🔴 **受け入れ基準 3（否定形）**: 監査の判定をそのまま写さない。各項目を本日 2026-10-07 に自分で測り直し、成り立たない判定は成り立たないと書く。

## 対象範囲

- GitHub の issue 操作（ラベル・タイトル・本文。本文は原文を残し、末尾に `［2026-10-07 再検証 / #1203］` 節を足す）: #346・#690・#397・#342・#565。#1184 のクローズ
- `docs/blocked-tasks.md`（`scripts/check-trace-blocks.js:62` の除外ファイル＝ ID をキーとする作業台帳。既存の書き方どおり本文に issue 番号・計画 ADR を書く）
- 対象外: 計画リポへの新規起票（#397 の計画との食い違いは planning#740 が既に扱う）・planning#741 の裁定中の論点への追随・コード

## 再検証期限の決め方

**2026-10-12** とする。根拠: 棚卸しは `.github/workflows/backlog-audit.yml:20` の `cron: "0 0 * * 1"`（毎週月曜 00:00 UTC）で走り、その観点 5 が「blocked 判定の再検証」である。本日（水）の次の実行が 2026-10-12（月）。直近の実行は 2026-10-05（`actions/workflows/backlog-audit.yml/runs` で schedule / success を確認）。

## 実測（2026-10-07）

| 対象 | 監査の判定 | 本日の実測 | 反映 |
| --- | --- | --- | --- |
| #346 | 失効（人の判断 4 点は 09-26 裁定済み） | ✅ 成り立つ。#346 の 2026-09-26T02:50 コメントが 4 点の判断を記録。MSP#1560（バックアップ）closed 2026-09-26、#1028（KB への入れ直し）closed 2026-09-26。前段ゲート #204 / #342 / #24 / #344 は 4 件とも open（API） | `blocked:human` を外す。本文へ「依存待ち」と前段ゲート |
| #690 | 誤分類の候補（PoC セッションが開場中に kubectl で取れる） | 🟡 **半分だけ成り立つ**。「API キー待ち」は失効（#690 の 2026-09-11 コメント、A-13）。計器は在る（`BusinessMetricNames.cs:60,67,78`）。**しかし「kubectl で取れる」は成り立たない**: サービスは OTLP でしか出さず（MSP `ObservabilityExtensions.cs:49,57` は `AddOtlpExporter` のみ。AST に Prometheus exporter のパッケージ参照は 0 件）、稼働クラスタの collector の exporters は `debug` だけ（MSP `deploy/local/infra/otel-collector.yaml:90`）。Prometheus は opt-in（`deploy/local/observability/otel-collector-forward.yaml:103`）で未適用（#1203 の 2026-10-07T14:24 コメントの実地確認、AST `PositionQueryStatusChanged.cs:5`「稼働クラスタに Loki / Prometheus は無い」）。#690 の 2026-09-23 コメントの `wget /metrics` 手順は通らない | タイトルと本文を「開場中の実測 1 回（観測スタックの opt-in 適用待ち）」へ。**`blocked:env` は維持**（誤分類ではない）。担当は PoC 運転セッション、適用はオーナーの判断 |
| #397 | 誤分類（文書調査・環流は AI 可） | ✅ 成り立つ。moomoo API Doc v10.11 `intro/authority.html` を本日取得し、Quote Right の表（Futu・moomoo の 2 表とも）が `Japanese Market Securities (including Stocks, ETFs) Unsupported.` と書くことを確認。計画 `03_moomoo-integration.md:24,35` は「2026-06 から日本株の時価取得・現物発注に対応」。食い違いは planning#740（open）の論点 3 が扱う。`TradingDefaults.cs:144` は `EnabledMarkets = { Japan, UnitedStates }` のまま | `blocked:env` → `blocked:human`（実弾口座の `trdmarket_auth` 確認と `EnabledMarkets` の判断）＋ `blocked:decision`（planning#740）。権限申請の手順は「文書上 API では不可」を添えて残す |
| #342 | 期限超過・項目 9 は文書で確認可・ラベル不一致・タイトルの「6 項目」 | ✅ 成り立つ。A-1b の期限 2026-08-15 から 53 日、項目 8・9 の期限 2026-08-31 から 37 日。`trade/get-margin-ratio.html`（v10.11）を本日取得し、`short_fee_rate | float | Borrow rate. This field is in percentage form, so 20 is equivalent to 20%.` を確認（年率かは書かれていない）。項目 8 は ADR-0025 決定 1、項目 9 は ADR-0026 決定 1 が追加（計 9 項目）。本文は 2026-09-23 コメントで「C（人待ち）」、ラベルは `blocked:env` | タイトルを 9 項目へ。項目ごとに待ちの種別と期限を表にする。ラベルは `blocked:env` → `blocked:human` ＋ `blocked:decision`（planning#740）。A-2 の表に項目 9 の行を足す |
| #565 | タイトルの前提は失効 | ✅ 成り立つ。MSP `Create/Command.cs:12` が `string? Body = null`、AST `HttpKnowledgeBaseWriter.cs:44-50` の `CreateDocumentBody` が `Body` を持ち `:79` で `document.Content` を渡す。2026-09-26 以後 #565 にコメントは無く、本文つき確定報告書での RAG 実測は未記録（稼働クラスタへは本セッションから接続できない） | タイトルを残件（本文つき確定報告書 1 件での RAG 実測）へ。ラベルは据え置き。実測は PoC 運転へ依頼 |
| #1184 | クローズ待ち | ✅ 成り立つ。失敗 run 37486052651（`15ca3a6`・2026-10-06T15:16）の後、develop の push で 4aefa04 / 9d74d12 / 017cbb5 / d581e29 / d0a4b23 / c17abf4 / cbc8fa5 / 1692de1 / 3787046 / 2458fc9 がすべて success（cancelled 2 件を除く）。cbc8fa5 以後は skip 件数の上限 0（#1200）の下での success | `state_reason=completed` でクローズ |

### blocked-tasks.md の欄（「最後に測った時点」）

| 行 | 旧値 | 本日の測り方 | 結果 |
| --- | --- | --- | --- |
| A-12 | 2026-09-02 | #627 の API（closed 2026-09-10・completed）と同日コメントの実測（4 経路 200・稼働は PERMISSIVE） | 解消（不再現）として記録。STRICT 移行時の再測定は MSP#458（open）側 |
| A-13 | 2026-09-03 | #690 の 2026-09-11 コメント（2026-09-10 投入・`/complete` 200）と #342 の 09-11 / 09-16 / 09-17 の開場中の実 LLM 判断の記録 | 解消として記録。**本セッションは Secret を測っていない**（値を扱わない・クラスタ非接続） |
| A-1 | 2026-08-05 | #342 のコメント一覧（最終 2026-09-23。ToS 判断の記録なし） | 2026-10-07。超過 53 日 |
| A-5 | 2026-08-05 | `docker info`（デーモン不達）・`scripts/` に合成データ投入スクリプト無し | 2026-10-07。測れない（手順 ② ③ が実行不能）と明記 |
| A-6 | 2026-08-14 | `npm ci` → 素の `npx playwright test` は 60 failed（`chromium_headless_shell-1228` 不在）→ `executablePath=/opt/pw-browsers/chromium-1194/chrome-linux/chrome` で **60 passed（42.3 秒）** | 2026-10-07。8 月と同じ構図のまま実走できる |
| A-9 ①③ | 2026-08-07 | #24 の最終更新 2026-09-11・#132 は closed。egress IP の切り替えと実口座の操作は AI セッションから行えない | 2026-10-07。未検証のまま |
| B-1 | 2026-08-14 | `.github/CODEOWNERS`（`* @endazon`）の実在・ruleset 18662050 `develop-rule`（`require_code_owner_review: true`、RepositoryRole 5 が `exempt`） | 2026-10-07。本文（2026-09-26）と同じ |
| B-2 | 2026-08-14 | 同 ruleset の `required_status_checks` は `pr-title` だけ。`permissions.admin: true` | 2026-10-07。`build-and-test` / `claude-review` は未必須 |
| B-3 | 2026-08-14 | `claude-code-review.yml` の run 37639418220 の `claude-review` ジョブが 4 分 25 秒走って success | 2026-10-07。登録済み（間接確認） |
| A-7a | 欄 2026-09-02・本文 2026-09-26 | 食い違いの是正（本文の追記日へ合わせる） | 2026-09-26（実機確認は本日は行っていない） |
| A-2 | 欄なし | 項目 9 は公開文書の取得（上記）、項目 1〜8 は既存記録の日付 | 欄を足し、項目 9 の行を足す |
| A-3 | 欄なし | #342 / Runbook に取得枠の検証口の実行結果が無いこと | 2026-10-07。確認 2 点は未了 |
| A-4 | 欄なし | 日銀 API を本日取得（`STATUS 200`、FXERD04 の最新値は 2026-10-05＝翌々営業日の収録どおり） | 2026-10-07 |
| A-8 | 欄なし | 公開文書の取得（上記）・OpenD の権限一覧は #342 の 2026-09-10 コメント | 2026-10-07 |
| B-4 | 欄なし | 未決の `ShortFeeRate` の単位の行を planning#740（open）と突き合わせ | 2026-10-07（索引の点検。各行の裁定は本文のとおり） |
| B-5 | 欄なし | リポジトリ・open issue に増資の実行の記録が無いこと | 2026-10-07。実資金の移動は AI が測れない |

## 母集合（規則 9・10）

- 規則 9（誤りの側の文字列で走査。`.ai-context/specs/` と `CHANGELOG.md` を除く）:
  - `A-12` / `A-13`: `blocked-tasks.md` 以外に IADR-0314（凍結記録。書き換えない）・`.ai-context/adr/README.md:358`（索引）・`deploy/helm/ai-stock-trading/README.md:925`・`values.yaml:44`。いずれも節への参照で、節は残す（再測定手順は STRICT 移行時に使う）ため追随不要
  - `API キー待ち` / `API キー未投入` / `PoC 6 項目` / `本文を受け取らない`: docs・scripts・deploy・frontend に 0 件。backend の「本文を受け取らない」は手動 `PUT /reports` の経路の話で別の意味（対象外）
  - `blocked-tasks.md` 内の「未解消」（L31 の 2026-09-04 の更新履歴）・優先順位表・補足表: 本 PR で追随する
- 規則 10（この変更で新たに誤りになる自分の記述）: A-2 の見出し「7 項目」は表が 9 行になるため「9 項目」へ改める。見出しのアンカー `#a-2-...` を引く箇所は 0 件（`git grep 'blocked-tasks.md#a-2'`）。導出値（超過日数 53 / 37）は本日の日付から計算し直した（8/15→10/7、8/31→10/7）

## 受け入れ基準

1. 6 件の issue のラベル・本文・状態が反映され、各 issue に「再検証期限: 2026-10-12」が書かれる（#1184 はクローズ）
2. `blocked-tasks.md` の A-12 / A-13 が解消として記録され、50 日超の 8 行（A-1 / A-5 / A-6 / A-9 ①③ / B-1 / B-2 / B-3）の欄が 2026-10-07 になる。測れない行は「測れない」と理由つきで書く
3. 否定形: 各反映に本 issue と監査記録 §9.1 の根拠に加え、本日の実測（`path:line` / API 出力）を引く。成り立たない判定（#690 の「kubectl で取れる」）は適用しない
4. `check-trace-blocks`・`gen-knowledge-graph --check`・`check-doc-links`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-reading-budget`・`check-commit-messages --range`・`REQUIRE_REPO_TESTS=1 scripts.test.js` が exit 0

## テスト方針

コードを変えないため新規テストは足さない。上の検査器の本走で確かめる。

## 計画書との差異

なし。計画と一次情報の食い違い（日本株・`short_fee_rate` の単位）は planning#740 が扱い、本 PR は計画へ寄せも離しもしない。

## 未決事項

- #690 の観測スタックの opt-in 適用（クラスタの変更）はオーナーの判断待ち
- `ShortFeeRate` が年率かどうか（moomoo サポートへの照会）と項目 8・9 の新しい期限は planning#740 の裁定待ち
