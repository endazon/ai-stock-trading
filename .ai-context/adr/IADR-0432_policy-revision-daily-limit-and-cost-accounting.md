---
title: IADR-0432 /policy の 1 日の回数上限は JST の暦日ごとの試行の台帳で数え、費用は policy-revision へ付け替えて月報 §7 に回数と費用を載せる
type: impl-adr
status: Accepted
related_ids: [FR-14, FR-06, FR-07, ADR-0042, ADR-0003, ADR-0037, IADR-0431, IADR-0251, IADR-0318, IADR-0120]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0042_discord-apply-ai-watchlist-proposal-and-revision-limit.md (決定 3)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§6・§6.1 LLM の費用と上限)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (月報 §7)
---

# IADR-0432: `/policy` の 1 日の回数上限と費用の計上区分

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（起票 [#1024](https://github.com/endazon/ai-stock-trading/issues/1024)。計画 ADR-0042 決定 3〔planning#663 の利用者裁定〕の実装。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #1024（前提: #1016 / IADR-0431 の `/policy` 本体）
- 関連する実装仕様書: [20260926_1024_policy-daily-limit](../specs/20260926_1024_policy-daily-limit.md)
- 関連 IADR: [IADR-0431](IADR-0431_policy-revision-from-discord-instruction.md)（`/policy`）、[IADR-0318](IADR-0318_stage0-ai-decision-record-and-replay.md)
  （Stage 0 記録の費用を計上の境界で付け替える先例）、[IADR-0251](IADR-0251_report-numeric-aggregation-outside-llm-context.md)（月報 §7）、
  [IADR-0120](IADR-0120_report-kind-purpose-and-parent-policy-feedforward.md)（報告書の用途キー）

## コンテキスト

ADR-0042 決定 3 は `/policy` に 1 日の回数上限を置き（値は設定値・既定値は 1 回あたりの費用の見積りと一緒に IADR に残す）、
費用を月次 LLM 上限（取引判断サイクルの 15,000 円）に算入せず、月報 §7 に用途別の実績（回数・費用）を載せると定めた。
IADR-0431 の実装は `/policy` を報告書と同じ用途（`report-daily` 等）で呼び、費用も同じ用途で計上していたため、
月報では報告書の自動生成と区別できず、回数の上限も無かった。

## 決定

### 決定 1: 回数は JST の暦日ごとの試行の台帳で数え、LLM を呼ぶ前に数えて書く。既定の上限は 10 回/日

- 報告書サービスに試行の台帳（`policy_revision_attempts`。EF マイグレーション `AddPolicyRevisionAttempts`）を置く。
  **LLM を呼ぶ直前に 1 行書き（Pending）、結果（Proposed〔案の版・入れ替え案〕／AiFailed／SaveFailed）で閉じる。**
  行は案の監査記録も兼ねる（指示者・時刻・会話キー・結果・入れ替え案）。
- 上限は**その JST の暦日に書かれた行の数**で判定する。**失敗した試行も数える**（応答が返らない呼び出しも費用が掛かり得るため）。
  入力の検証（指示・会話キー・対象の決定・営業日の自動生成前）で断った要求は LLM を呼ばないので数えない。上限に達した要求は
  LLM を呼ばず 429（「本日の /policy は上限の N 回に達しています … 方針は変わっていません」）で断り、行も書かない。
- 上限は構成 `Reports:PolicyRevision:DailyLimit`。空・未設定・不正・1 未満は既定へ倒す（上限を無効にする値を作らない）。
- **既定値 10 回/日。1 回あたりの費用の見積り**（用途キー `report-daily` の第 1 候補 `claude-sonnet-5`・
  前提条件 §6.1 の単価 327 円/1M 入力・1,637 円/1M 出力〔ADR-0037 の是正後〕）:

  | 項目 | トークン | 円 |
  | --- | --- | --- |
  | 入力（指示 1000 字・現在の方針 2000 字・上位方針・規則。日本語はおよそ 1 字 1〜1.5 トークン） | 約 3,000〜5,000 | 約 1.0〜1.6 |
  | 出力（方針 2000 字＋入れ替え案＋説明の JSON。思考トークン込みの上限 4,096） | 典型 約 1,500〜2,500／上限 4,096 | 典型 約 2.5〜4.1／上限 6.7 |
  | **合計（1 回）** | | **典型 約 4〜6 円／上限 約 8.3 円** |

  既定 10 回/日の上限は **1 日 約 83 円・月 30 日で 約 2,500 円**（最悪）。典型の使い方（1 日 1〜3 回）では月 100〜500 円程度。
  月次総費用上限 20,000 円（§6）に対して最悪でも 1 割強に留まり、取引判断の予算（§6.1 の 15,000 円）とは別の区分である。
  見積りは第 1 候補での値であり、第 2 候補（`claude-haiku-4-5`）へのフォールバックでは下がる。**実績は月報 §7 で見る**。

### 決定 2: 費用は計上の境界で `policy-revision` へ付け替え、用途キー（モデル割当）は変えない

- `LlmPurposes.PolicyRevision = "policy-revision"` を**計上区分**として足す。ゲートウェイへ送る用途キーは報告書と同じ
  （`report-daily` 等）のまま——未登録の用途キーは基盤で既定モデルへ無音で落ちる（IADR-0120 の罠）ため、用途キーは増やさない。
  `LlmReportPolicyReviser` が `ILlmUsageReporter` へ渡すときだけ付け替える（Stage 0 記録〔IADR-0318〕と同じ作法）。
- `LlmCostScope.IsGoverned("policy-revision")` は偽＝月次 LLM 上限に積まない・抑制しない（ADR-0042 決定 3）。
  `IsReport` にも該当しない＝報告書生成の費用へ混ぜない。
- 月報 §7 に「利用者起点の方針改訂（`/policy`・`policy-revision`。上限の対象外）の回数と費用実績」の行を Stage 0 記録の行の後に足す。
  回数は**計上の件数**（応答が返った呼び出し）。当月に計上が無ければ「0 回・0 円」と書かず「当月の呼び出しはありません」と書く。
  割当逸脱の通知（ADR-0017 決定 4）は用途キー（`report-daily` 等）のまま出す（モデル割当の検証は用途キーの問題であるため）。

## 却下した案

- **プロセス内の計数**: 再起動で 0 に戻り上限が効かない。
- **費用統制サービスの計上から回数を数える**: 別サービスへの同期照会が要り、応答の返らない試行を数えられない。
- **新しい用途キーをゲートウェイへ送る**: 基盤の割当表に無い値は既定モデルへ無音で落ちる。

## 残余リスク

- 同時に 2 つの `/policy` が走ると、数えてから書くまでの間に上限を 1 回超え得る（利用者本人だけが呼べる窓口で、許容する）。
- 月報 §7 の回数（計上の件数）と台帳の試行の数は一致しない（応答が返らなかった試行は計上されない）。台帳は DB に残る。
- 見積りの単価は前提条件 §6.1 の値であり、単価の改定に追随しない（実績は月報で見る）。
