---
title: Discord からの報告書の確定が 5 秒で時間切れになり、確定は通っているのに「結果は不明」と返る（#1385）
type: spec
status: done
related_ids: [FR-07, FR-14, FR-08, ADR-0003, ADR-0068, ADR-0065, IADR-0240, IADR-0433, IADR-0449, IADR-0450, IADR-0526, IADR-0548, IADR-0549, IADR-0556]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0068_daily-policy-reconfirm-by-adding-version.md（決定 6: 確定版の写しの置き換え → ドラフトの写しの削除の順序）
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md（方針の確定は利用者との対話を要する）
---

# 仕様書: 報告書の確定の時間上限を「外側＞内側」に揃え、時間切れでは確定の状態を照会して返す（#1385）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。基点は AST `origin/develop` `cac866f6`。
> 計画は project-planning `origin/main` `55e464a` で読んだ。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-07**（確定前の方針は取引に適用しない・確定は利用者の承認）、**FR-14**（Discord からの操作）。FR-08（KB への保存）。
- 計画 ADR: ADR-0003（確定は対話を要する）、ADR-0068 決定 6（確定版の写しを最新の確定版で置き換えてからドラフトの写しを消す。置き換えに失敗したら消さない）、ADR-0065 決定 2。
- 起点 issue: **#1385**（`Closes #1385`）。発端は AST#1374 / AST#1375（IADR-0548 / IADR-0549）で確定の後処理が重くなったこと。
- 新規 IADR: **IADR-0556**（develop の最大は IADR-0554。push 前の再確認で、並行する open PR #1386（#1383）が IADR-0555 と T-10-2717〜T-10-2721 を取っていたため、本件は IADR-0556・T-10-2726〜T-10-2734 へ改番した。欠番可・最大＋1）。

## 現状（コードで確かめた事実）

### 外側（通知サービス）

| 経路 | 上限 | 所在 |
| --- | --- | --- |
| REST `report-review`（照会・確定・差し戻し・会話キーの一覧で共用） | `HttpClient.Timeout` = 5 秒 | `NotificationService/Program.cs` |
| gRPC `ReportsGrpcTransport.Calls`（同上） | `Reports:GrpcTimeoutSeconds`、既定 `DefaultTimeout` = 5 秒 | `NotificationGrpcTransports.cs` |
| Discord の確認ボタン | `DeferAsync` の後の追送（期限 15 分） | `DiscordNetBotGateway.OnReportApproveButtonAsync` / `OnPolicyApproveButtonAsync` |

時間切れは `ReportConfirmResult(false, false, "報告書の確定がタイムアウトしました（結果は不明です）")`。`ReportCommandHandler.ApproveAsync` は版番号の予約を解放して失敗を返す。
`/policy` の確認ボタン（`PolicyApprovalCommandHandler`）も同じ `ReportCommandHandler` を通る。確定の呼び出し元は、この 2 つ（Discord の窓口）だけである（フロントエンドは確定しない）。

### 内側（報告書サービスの確定。`ConfirmReport/Endpoint.cs` の `HandleAsync`、REST と gRPC が共有）

| 段 | 上限（コードの値） | 備考 |
| --- | --- | --- |
| ① `svc.Confirm`（DB。衝突時は `MaxConfirmAttempts` まで再試行） | 明示の上限なし（DB のコマンドの既定） | 通常は数十 ms |
| ② `ReportConfirmed` の発行（Wolverine） | 明示の上限なし | 通常は ms |
| ③ KB: 初回の確定 = `IKnowledgeBaseWriter.SaveAsync`（documents クライアント 5 秒 ＋ トークン 10 秒）。**`http.RequestAborted` を渡す** | 1 呼び出し 5 秒（＋トークン 10 秒） | 外側が切れると取り消され、`OperationCanceledException` が catch を素通りして ⑥ を飛ばし 500 になる |
| ③' KB: 再確定 = 入れ直し（`ReportKnowledgeReingestService.RunAsync`。一覧 → 作成/本文差し替え → 残った承認待ちの写しの削除 → 監査の発行） | カタログのクライアント 1 呼び出し 30 秒 × 複数 | `CancellationToken.None` |
| ④ 失効の印（`MarkExpiredAsync`。日報が移ったときだけ） | カタログ 30 秒 × 複数 | `CancellationToken.None` |
| ⑤ ドラフトの写しの削除（`RemoveAsync`） | カタログ 30 秒 × 2（一覧・削除） | `CancellationToken.None` |
| ⑥ `/policy` の台帳に確定時刻（DB） | 明示の上限なし | best-effort |

**内側の上限は合計で 30 秒の倍数になり得て、外側の 5 秒より長い。** PoC の所要は約 4〜5 秒（③' の 3 呼び出し ＋ ⑤ の 404）で、境目に当たった。

### 照会（レビュー局面）

`GET /reports/{key}/review`・gRPC `GetReportReview` は `version`・未供給の入力・版 n+1 の表示（`confirmedVersion`・`noLongerEffective`）を返す。ボットは状態の enum を読まない（IADR-0240 決定 5）。
**「下書きの版 v が確定されたか」を、状態の enum に結合せずに判定する項目が無い。**

## 案の比較

| 案 | 内容 | 長所 | 短所 |
| --- | --- | --- | --- |
| (a) 外側を内側より長く | 確定だけ専用の上限（REST の名前付きクライアント・gRPC の deadline）を置く | 変更が小さい。結果を待って確定的に返せる | 内側に上限が無いと「外側＞内側」が成り立たない（③' は 30 秒の倍数） |
| (b) KB の後処理を応答の後へ | 確定の応答を DB の遷移の直後に返し、KB は背景で行う | 応答が KB の遅さに左右されない | 要求のスコープ（入れ直しの scoped サービス・DbContext）を離れるため別のスコープと背景の実行器が要る。プロセスが止まると後処理が消える（今と同じ）。既存の試験の多くが「確定の応答の時点で写しがある」を前提にしており、待ち合わせへの書き換えが広い |
| (c) 時間切れで状態を照会 | 時間切れのとき、レビュー局面を照会して「この版で確定されたか」を返す | 外側の上限をどう置いても、利用者に「不明」を残さない（ネットワーク起因の時間切れにも効く） | 照会の項目が要る（状態 enum に結合しない形で） |

### ADR-0068 との整合（(b) を採る場合の確認）

- 決定 6 の拘束は**KB の中の順序**（確定版の写しの置き換え → ドラフトの写しの削除。置き換えに失敗したら消さない）であり、確定の応答と KB の同期性は定めていない。
- 確定の意味（効いている版が変わる）は DB の遷移（①）で決まる。KB は FR-08 / IADR-0071 決定 3 のとおり best-effort（確定を壊さない）。
- よって (b) も ADR-0068 に反しない。ただし (b) は上の短所のとおり変更の範囲と「後処理が消える」窓が広い。

### 選択: **(a) ＋ (c)**、内側には **KB の後処理の時間予算**（(b) の一部＝確定の可否と KB を切り離す）を置く

1. **内側に明示の上限を置く。** KB の後処理（③・③'・④・⑤）を 1 つの時間予算 `Reports:Confirm:KnowledgeBudgetSeconds`（既定 **20 秒**）で打ち切る。
   - 予算は要求の取り消し（`RequestAborted`）と**結ばない**（確定は済んでいる。外側が先に切れても後始末を飛ばさない）。③ が `RequestAborted` を渡していたのを改める。
   - 予算切れは警告を残して続行する（確定は 200 のまま）。置き換え（③/③'）が予算切れなら `savedToKb=false` ＝ドラフトの写しは消さない（ADR-0068 決定 6 の順序を保つ）。残りは次の入れ直しが揃える（既存の回復経路）。
   - ⑥（台帳の確定時刻）は KB の前へ移す（KB の遅さ・予算切れに左右させない。どちらも best-effort で順序の拘束は無い）。
2. **外側は確定だけ専用の上限を置く**: REST の名前付きクライアント `report-confirm`・gRPC の `ReportsGrpcTransport.ConfirmCalls`（`Reports:GrpcConfirmTimeoutSeconds`）、既定 **30 秒**（＝予算 20 秒 ＋ ①②⑥ の余白 10 秒）。照会・差し戻し・一覧の 5 秒は変えない（入力補完の予算 #843 と連なる）。Discord の追送の期限（15 分）に収まる。
3. **時間切れでは照会してから返す**（(c)）。レビュー局面に `latestConfirmedVersion`（その期間キーの最新の確定版の版番号。無ければ欠落）を足す（REST・gRPC とも非破壊の追加）。ボットは `latestConfirmedVersion == 押した版 + 1` で「この版で確定された」と判定する（`IsConfirmedAtDraftVersion`・`InterpretConfirmed` と同じ規則）。
   - 確定済み → 「確定しました（応答が時間切れになったため状態を照会して確かめました）」。予約は**残す**（再押下は窓口で吸収）。`/policy` の確認ボタンでは入れ替え案の適用へ進む（台帳の `ApplyRecorded` が二重適用を止める）。
   - 別の版で確定済み → 「既に別の版で確定済み」。予約を解放。
   - 未確定（欠落・それより古い版） → 「確定できていません（照会の時点で未確定）」。予約を解放。押し直しは層 2（版番号つき冪等）が二重確定を防ぐ。
   - 照会も失敗 → 従来の「結果は不明」に `/report show` での確認の案内を添える。予約を解放（層 2 が守る）。
   - 旧版の報告書サービス（項目なし）→ 照会が失敗したのと同じ扱い（配備順の窓）。

(b) を全面には採らない理由: 予算で内側の上限が決まれば外側＞内側を保てる。背景化はスコープの持ち出しと試験の待ち合わせの書き換えが広く、プロセス停止で後処理が消える窓を応答の後にも広げる。

## 冪等性（再押下・二重確定）

| 状況 | 層 1（窓口の予約） | 層 2（報告書サービス） | 結果 |
| --- | --- | --- | --- |
| 時間切れ → 照会で確定済み | 残す | — | 再押下は「確定済み」を返し確定 API を呼ばない |
| 時間切れ → 照会で未確定 | 解放 | 押し直しで版番号つき確定。先の要求が後から確定していれば冪等の 200（`version == 版+1`）→「この版で確定されています」 | 二重確定にならない |
| 時間切れ → 照会も失敗 | 解放 | 同上 | 同上 |

## 母集合（規則 9・10）

- 走査: `grep -rn "report-review\|ReportsGrpcTransport\|DefaultTimeout\|TimedOutMessage\|ReportConfirmResult(" backend --include=*.cs`（bin/obj 除く）。
  - 外側の確定の経路: `HttpReportReviewController.ConfirmAsync`・`GrpcReportReviewController.ConfirmAsync`（2 つ）。呼び手は `ReportCommandHandler.ApproveAsync`（`PolicyApprovalCommandHandler` も経由）だけ。
  - `ReportConfirmResult` の生成: 上の 2 つ ＋ 試験の偽物（任意の追加項目なので無改修で通る）。
  - レビュー局面の型: 送り手 `ReportReviewView`・`ReportOwnerReadWireMapping.ToProto`・proto `GetReportReviewResponse`、受け手 `ReviewView`（REST）・`GrpcReportReviewController.GetReviewAsync`。
- 構成・文書: `grep -rn "GrpcTimeoutSeconds\|report-review"` で `docs/api/east-west-grpc.md`（構成キーの表）・`deploy/helm/ai-stock-trading/values.yaml`（notification のコメント）・`docs/data/reports.md`（確定の KB の記述）。凍結の IADR（0062・0240・0418・0449・0450 ほか）は書き換えない。
- 内側の上限: `grep -rn "Timeout" Shared/AiStockTrading.Shared.KnowledgeBase`（documents/search 5 秒・カタログ 30 秒・トークン 10 秒）。
- 計画: ADR-0068・ADR-0065・07_discord-bot-design に確定の時間上限の記述は無い ⇒ 環流 issue は起票しない。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 | KB の後処理が予算を超えても確定は 200 で返り、台帳に確定時刻が残り、ドラフトの写しは消さない | T-10-2726 |
| AC2 | 予算の構成（未設定・不正値は既定 20 秒） | T-10-2727 |
| AC3 | レビュー局面が最新の確定版の版番号を返す（REST・gRPC の写し） | T-10-2728 |
| AC4 | 外側の確定の上限（REST・gRPC の既定 30 秒）＞ 内側（報告書サービスの予算の既定）＋余白。照会の 5 秒は変えない | T-10-2729 |
| AC5 | 時間切れ → 照会で確定済み → 「確定しました」・予約を残す（再押下は確定 API を呼ばない） | T-10-2730 |
| AC6 | 時間切れ → 照会で未確定 → 「確定できていません」・予約を解放 | T-10-2731 |
| AC7 | 時間切れ → 照会も失敗 → 「不明」＋確認の案内・予約を解放／別の版で確定済み → 「確定していない」 | T-10-2732・T-10-2733 |
| AC8 | REST・gRPC のアダプタが時間切れを「不明」の印つきで返し、照会の新しい項目を読む | T-10-2734 |

## 検証（2026-10-17 実施）

- `dotnet test backend/Services/ReportService/Tests` → 2012 件成功。`dotnet test backend/Services/NotificationService/Tests` → 918 件成功。
- `dotnet format backend/backend.slnx --verify-no-changes --include <変更した .cs>` → 差分なし。
- `node scripts/check-proto-contracts.js --update`（非破壊のフィールド追加 `GetReportReviewResponse.latest_confirmed_version = 5` を baseline へ）→ 以後 OK。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 554/554。`check-trace-blocks`・`check-doc-links`・`check-test-traceability`・`check-cross-repo-refs`・
  `check-plan-id-qualification`・`check-wall-clock-timeout-tests`・`gen-knowledge-graph --check` → OK。
- helm は手元に無い。values.yaml はコメント 2 行の追加だけで描画は変わらない（CI の helm ジョブで描画を確かめる）。
- 変異（1 本ずつ当てて戻した）: 時間切れの照会を外す → T-10-2730〜T-10-2733 の 8 件が赤。gRPC の確定を照会の deadline へ戻す → T-10-2734 が赤。
  予算を 20 秒に固定 → T-10-2726 の 2 件が赤。ドラフトの写しの削除の条件を外す → T-10-2726（再確定）が赤。

## PoC での確認手順

1. 配備（helm upgrade。既定の構成で可）後、Discord で `/policy` → 確認ボタンで確定する。
2. 通知サービスのログに「報告書の確定がタイムアウトしました」が出ないこと（KB の後処理は 20 秒の予算内）。出た場合は続けて
   「照会でこの版での確定を確かめました」または「照会の時点で未確定でした」が出て、Discord の応答が「確定しました（…照会して確かめました）」／「確定できていません」になること。
3. 報告書サービスのログで、KB が遅いときは「KB の後処理が時間予算（20 秒）内に終わりませんでした（確定は継続）」が出ても確定の応答は 200 であること。
4. `GET /reports/<期間キー>/review` の応答に `latestConfirmedVersion` が載ること（確定後は確定版の版番号）。
