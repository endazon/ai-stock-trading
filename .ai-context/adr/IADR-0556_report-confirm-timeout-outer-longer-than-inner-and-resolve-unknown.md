---
title: IADR-0556 報告書の確定の時間上限を「外側＞内側」に揃える。内側は KB の後処理を 1 つの時間予算（既定 20 秒）で打ち切り、外側は確定だけ専用の上限（既定 30 秒）を置き、時間切れでは最新の確定版の版番号を照会して「この版で確定されたか」を返す
type: impl-adr
status: Accepted
related_ids: [FR-07, FR-14, FR-08, ADR-0003, ADR-0068, ADR-0065, IADR-0240, IADR-0433, IADR-0449, IADR-0450, IADR-0526, IADR-0548, IADR-0549]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0068_daily-policy-reconfirm-by-adding-version.md（決定 6: 置き換え → ドラフトの写しの削除の順序）
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
related_specs:
  - ../specs/20261017_1385_report-confirm-timeout.md
---

# IADR-0556: 報告書の確定の時間上限を「外側＞内側」に揃え、時間切れでは確定の状態を照会して返す（#1385）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-17
- 決定者: claude が起案（起点 [#1385](https://github.com/endazon/ai-stock-trading/issues/1385)。レビューは PR で受ける）

## 起点・関連

- 計画: FR-07（確定前の方針は取引に適用しない）、FR-14（Discord からの操作）、FR-08（KB への保存）。ADR-0003（確定は対話を要する）、ADR-0068 決定 6・ADR-0065 決定 2（確定版の写しを置き換えてからドラフトの写しを消す。置き換えに失敗したら消さない）。
  計画は確定の時間上限を定めていない。
- 前提として覆さない IADR: [IADR-0240](IADR-0240_discord-report-review-window-and-idempotent-confirm.md)（窓口の版番号の予約＝層 1、報告書サービスの版番号つき冪等＝層 2。ボットは状態 enum を読まない＝決定 5）、
  [IADR-0433](IADR-0433_policy-watchlist-proposal-apply-from-discord.md) 決定 7（`version == 押した版 + 1` で「この版で確定された」と読む）、
  [IADR-0449](IADR-0449_bot-read-grpc-stage5.md) / [IADR-0450](IADR-0450_bot-write-grpc-stage5.md) 決定 4（書き込みは再試行しない・時間切れは「結果は不明」）、
  [IADR-0526](IADR-0526_report-draft-knowledge-copy.md) / [IADR-0548](IADR-0548_daily-policy-confirmed-version-history.md) / [IADR-0549](IADR-0549_daily-policy-reconfirm-by-adding-version.md)（確定の後の KB の写しの扱い）。
- 仕様書: `.ai-context/specs/20261017_1385_report-confirm-timeout.md`

## コンテキスト

- PoC 2026-10-17（AST rev 49）で、Discord の確認ボタンからの再確定（daily-2026-10-17・版 4）が、通知サービスの 5 秒（REST `report-review`・gRPC `Reports:GrpcTimeoutSeconds` の既定）で時間切れになった。報告書サービスは確定を終え、KB の置き換えとドラフトの写しの削除まで進んでいた。利用者には「失敗（結果は不明）」と返り、窓口は版番号の予約を解放した。
- 確定の所要の内訳（コードの上限）: DB の遷移（明示の上限なし）、`ReportConfirmed` の発行、KB の後処理（初回の確定＝文書の保存 5 秒〔＋トークン 10 秒〕／再確定＝入れ直しの一覧・本文差し替え・残った写しの削除、カタログ 1 呼び出し 30 秒 × 複数／失効の印・ドラフトの写しの削除、各 30 秒）、`/policy` の台帳への確定時刻。**内側の上限は合計で 30 秒の倍数になり得て、外側の 5 秒より長かった。**
- 初回の確定の保存だけ `RequestAborted` を渡していた。外側が先に切れると保存が取り消され、`OperationCanceledException` が catch を素通りして台帳の確定時刻を飛ばし、応答を 500 にしていた。
- ボットは「下書きの版 v が確定されたか」を状態 enum に結合せずに照会する手段を持たなかった。

## 検討した選択肢

| | (a) 外側を内側より長く | (b) KB の後処理を応答の後へ | (c) 時間切れで状態を照会 |
| --- | --- | --- | --- |
| 効き | 結果を待って確定的に返せる | 応答が KB に左右されない | 外側の上限をどう置いても「不明」で終わらない（ネットワーク起因にも効く） |
| 前提 | **内側に上限が要る**（今は 30 秒の倍数） | 要求のスコープ（入れ直しの scoped サービス・DbContext）の外で動く実行器と別スコープ。既存の試験の多くが「応答の時点で写しがある」を前提にしており、待ち合わせへの書き換えが広い | 照会の項目（状態 enum に結合しない形） |
| ADR-0068 | 影響なし | 決定 6 の拘束は KB の中の順序であり、応答との同期性は定めていない（確定の意味は DB の遷移で決まり、KB は FR-08 / IADR-0071 決定 3 の best-effort）。**反しない** | 影響なし |
| 失敗の窓 | 予算切れの後始末は次の入れ直し | プロセスが止まると後処理が消える窓が応答の後にも広がる | 照会も失敗すれば従来の「不明」（案内つき） |

**(a) ＋ (c) を採り、内側の上限には (b) の考え方（確定の可否を KB と切り離す）を「時間予算」として入れる。** (b) を全面には採らない（上の前提と失敗の窓）。

## 決定

1. **内側: KB の後処理を 1 つの時間予算で打ち切る。** 構成 `Reports:Confirm:KnowledgeBudgetSeconds`（秒・小数可。既定 **20 秒**＝`ReportConfirmKnowledgeBudget.DefaultKnowledgeBudgetSeconds`。未設定・非数値・非正値・無限は既定）。
   - 予算は確定版の写しの保存／置き換え・失効の印・ドラフトの写しの削除に同じトークンで渡す。**要求の取り消し（`RequestAborted`）とは結ばない**（確定は済んでいる。外側が先に切れても後始末を飛ばさない）。
   - 予算切れは警告を残して続行し、確定は 200 のまま返す。**置き換えが予算内に終わらなければドラフトの写しは消さない**（ADR-0068 決定 6 の順序）。残りは入れ直しが最新の確定版に揃える（既存の回復経路）。
   - `/policy` の台帳への確定時刻（best-effort）は KB の後処理の**前**へ移す（KB の遅さに左右させない。両者に順序の拘束は無い）。
2. **外側: 確定だけ専用の上限を置く。** REST の名前付き HttpClient `report-confirm`・gRPC `ReportsGrpcTransport.ConfirmCalls`（`Reports:GrpcConfirmTimeoutSeconds`）、既定 **30 秒**（＝予算 20 秒 ＋ DB の遷移・通知の発行・台帳の余白 10 秒。`ReportsGrpcTransport.DefaultConfirmTimeout`）。照会・差し戻し・会話キーの一覧の 5 秒は変えない（入力補完の予算 #843 と連なる）。Discord の応答は Defer の後の追送で、期限（15 分）に収まる。確定の呼び手は Discord の窓口（`/report approve` と `/policy` の確認ボタン。どちらも `ReportCommandHandler`）だけである。
3. **時間切れでは照会してから返す。** 確定の結果に「確定されたか分からない」の印（`ReportConfirmResult.OutcomeUnknown`。REST は HttpClient の時間切れ、gRPC は `DEADLINE_EXCEEDED`）を付け、`ReportCommandHandler` がレビュー局面を照会する。
   レビュー局面に **`latestConfirmedVersion`**（その期間キーの最新の確定版の版番号。行が確定済みならその版、下書きなら確定版の写しの最新。確定版が無ければ欠落）を足す（REST・gRPC `latest_confirmed_version = 5` とも非破壊の追加）。
   - `latestConfirmedVersion == 押した版 + 1` → この版で確定済み。確定として返し（`ConfirmedNow`。`/policy` の確認ボタンは入れ替え案の適用へ進み、台帳の `ApplyRecorded` が二重適用を止める）、**版番号の予約を残す**（再押下は窓口で吸収し確定 API を呼ばない）。
   - 最新の確定版が押した版より後、または行のいまの版が確定版 → 別の版で確定済み。予約を解放。
   - 行が押した版のまま（確定すれば版は 1 進む）、または項目を読めてこの版の確定が無い → 未確定。予約を解放し「もう一度確定してください（二重には確定されません）」。
   - 照会も失敗、または項目の無い旧版の報告書サービスで行の版が進んでいる（確定か改訂か見分けられない） → 従来の「結果は不明」に `/report show` での確認の案内を添え、予約を解放。
   - **二重確定は層 2（版番号つき冪等）が防ぐ。** 予約を解放した後の押し直しは、先の要求が後から確定していれば冪等の 200（`version == 版 + 1`）で「この版で確定されています」になる。

## 結果

- 外側（30 秒）＞内側（KB の予算 20 秒 ＋ 余白）を REST・gRPC の両経路で試験で固定した（T-10-2729。送り手の定数を直接参照する）。
- KB が返らなくても確定は予算の後に 200 で返り、ドラフトの写しは消えない（T-10-2726）。時間切れの後の 4 つの分岐と再押下の冪等（T-10-2730〜T-10-2733）、アダプタの印と新しい項目（T-10-2734）を固定した。
- 変異で確かめた: 時間切れの照会を外す → T-10-2730〜T-10-2733 が赤。gRPC の確定を照会の deadline へ戻す → T-10-2734 が赤。予算を 20 秒に固定する → T-10-2726 が赤。ドラフトの写しの削除の条件（保存できたとき）を外す → T-10-2726（再確定）が赤。

## 残余リスク

- 内側の DB の遷移・通知の発行・台帳の書き込みには明示の上限が無い。余白 10 秒を超えると外側が先に切れるが、(c) が照会で確かめる。
- 予算と外側の上限は別のサービスの構成で、片方だけを変えると大小が逆転し得る（起動時の検査は無い）。既定の配備はどちらも置かない。値を変えるときは両方を揃える（values.yaml の notification のコメント・`docs/api/east-west-grpc.md` の表に記した）。
- 予算切れの後の KB の後始末は、入れ直し（`POST /reports/knowledge-base/reingest`）を誰かが走らせるまで残る（今の「KB の失敗」と同じ扱い）。
