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

## ［2026-09-26 追記 / PR #1026 の監査］是正

- **列の溢れ**: `WatchlistChangesJson` を `varchar(8192)` から `text` へ（既定のエンコーダが日本語を `\uXXXX` へ逃がし、理由 200 字 × 10 件で
  12,476 文字になっていた）。直列化は `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`（日本語をそのまま）。
- **保存後の台帳の失敗**: トランザクションで包む案ではなく、**台帳の完了の書き込みを失敗させない（記録して続ける）**を選んだ。
  ドラフトは既に保存されており、確定するまで取引に効かない。包むと、報告書のストアが自分で `SaveChanges` する既存の作り（他の経路と共有）を
  変える必要があり、InMemory の試験ではトランザクションが効かない。台帳の行は Pending のまま残り、上限には数えられる（安全側）。
- **並行更新の SaveFailed**: `EfReportStore.UpsertDraft` が `DbUpdateConcurrencyException` のとき変更の追跡を消す（同じ DbContext の台帳の
  書き込みが失敗した行を保存し直して同じ例外で落ちていた）。失敗の経路の台帳の書き込みも元の例外を上書きしない。
- **同時要求**: 上の残余リスクの追記。
- 試験: T-10-1409〜T-10-1414（JST の暦日の境界 14:59 / 15:00 UTC・EF の台帳は失敗も数える を含む）。

## ［2026-09-26 追記 / PR #1026 の再監査］是正と受容

- **F1**: 台帳の書き込み（`TryBegin` の追加・`Complete`）が失敗したら、その行を追跡から外してから例外を上げる（`SaveOrDetach`）。
  共有の DbContext に失敗した行が残ると、続く提示の保存がそれを保存し直して落ち、保存済みの案が提示されなかった（再監査が EF で再現）。
- **F2**: `EfReportStore.UpsertDraft` は並行更新に限らず**どの `DbUpdateException` でも**変更の追跡を消す（残すと台帳の SaveFailed の
  書き込みが失敗したはずの下書きを保存していた）。
- **F4**: 台帳を閉じられなかったことは試行（AttemptId）と結果（Outcome）を添えて Error で残す（試験で固定）。**F5**: 残りのログの `actor` を無害化。
- 🔴 **F3（受容）: Postgres の勧告ロック（`pg_advisory_xact_lock`）の経路は実 Postgres で試験していない。** 再監査はコードを読んで正しいと判断した。
  Testcontainers 等でポートを公開すると 0.0.0.0 へ bind するため、本リポジトリの試験の規律（ループバック以外で待ち受けない）に反するので入れない。
  試験は InMemory の経路（プロセス内の錠）で同じ「数えて書く」の排他を固定している。配備後に Postgres で同時要求を確かめるのは運用の確認に委ねる。
- 試験: T-10-1424〜T-10-1426。

## ［2026-09-26 追記 / #1029］適用の内訳の記録の原子性と、失敗の片付けの統一

#1027 の再監査（非ブロック）と #1026 の差分監査（N1・N2）の是正。IADR-0433 が同じ台帳に足した書き込みも、本 IADR の規律に揃える。

- **適用の内訳は 1 回だけ記録する（原子的に）**: 従来の `RecordWatchlistApply` は読んでから書く形で、2 つの書き手（Bot のプロセスが 2 つ・
  別の DbContext）がどちらも「まだ記録が無い」と読むと、両方が true を返し、後の方が先の監査 JSON を上書きし得た。
  `WatchlistAppliedAt` を**同時実行のトークン**にし、保存は「まだ記録が無い」行だけを更新させる（`UPDATE … WHERE "Id" = @id AND
  "WatchlistAppliedAt" IS NULL`）。後の書き手は `DbUpdateConcurrencyException` になり、`SaveOrDetach` が行を切り離したうえで
  **上書きせず** false（エンドポイントは 409「記録済み」）を返す。衝突以外の失敗は従来どおり例外で上げる（「記録済み」に偽らない）。
- **一意制約（PeriodKey, ReportVersion）を採らない理由**: 競合は同じ行（同じ試行 ID）への 2 つの UPDATE であり、行をまたぐ一意制約では
  止まらない（どちらも同じ 1 行を書き換える）。同じ会話キー・版の案（Proposed）の試行が 2 行できる経路も無い（版は報告書の行の
  楽観排他で 1 つずつ進む）ため、行をまたぐ制約が守るものが無い。加えて InMemory は一意制約を強制しないが、同時実行のトークンは
  InMemory でも効くので試験で固定できる。
- **マイグレーション** `PolicyRevisionWatchlistApplyConcurrency` は**スキーマを変えない**（トークンは WHERE 句の問題で、列・索引は同じ）。
  モデルのスナップショットを進めるためだけに置く。
- **トークンの副作用**: 同じ行の他の書き込み（`Complete`・`MarkProposalConfirmed`）も WHERE に記録時刻の元の値を持つ。`Complete` は
  案が確定される前、`MarkProposalConfirmed` は確定の遷移の直後（適用の記録より前。best-effort で失敗は警告）に書くため、実運用では衝突しない。
  トークンは記録時刻の 1 列だけに限る（試験で固定）。
- **`SaveOrDetach` の統一**: `RecordWatchlistApply`・`MarkProposalConfirmed`・`Begin` も通す（台帳の書き込みはすべて）。
- **N1**: `EfReportStore.UpsertDraft` の改訂の保存は、**例外の種類を問わず**変更の追跡を消してから上げる。Npgsql は接続を開くときの失敗を
  `DbUpdateException` に包まず `NpgsqlException` のまま上げるため、型で絞るとその経路だけ、台帳の SaveFailed の保存が失敗した下書きを
  一緒に保存し得た（F2 と同じ食い違い）。
- **N2**: `TryBegin` の切り離し（防御的な経路）を試験で固定した。
- F3 と同じく、Postgres の実 DB では試験していない（トークンの SQL は EF が生成し、元の値が null なら `IS NULL` を出す）。
- 試験: T-10-1482〜T-10-1489（`PolicyRevisionLedgerTests`）。変異注入の実測は `docs/tests/FR-10_risk-controls-tests.md`。

## 残余リスク

- ~~同時に 2 つの `/policy` が走ると、数えてから書くまでの間に上限を 1 回超え得る~~ ［2026-09-26 追記 / PR #1026 の監査］
  監査が上限 1・同時 2 要求で LLM が 2 回呼ばれることを実測した（N 要求で N−1 回超える）。数えることと書くことを 1 つの排他区間
  （`TryBegin`。Postgres は JST の暦日を鍵にした `pg_advisory_xact_lock`、InMemory はプロセス内の錠）にしたため、**この残余は無くなった**。
  Postgres の勧告ロックは実 DB で試験していない（試験は InMemory の経路。錠の取り方は `ExecuteSql` の 1 行）。
- 月報 §7 の数（計上の件数）と台帳の試行の数は一致しない（応答が返らなかった試行は計上されない）。台帳は DB に残る。
  §7 の「無し」の表記は「当月の計上はありません」（呼び出しが無いとは言えない）。
- 見積りの単価は前提条件 §6.1 の値であり、単価の改定に追随しない（実績は月報で見る）。
