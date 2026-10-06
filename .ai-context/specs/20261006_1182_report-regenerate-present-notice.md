---
title: /report regenerate で作り直した版の要約が利用者に届かない（ReportDraftPresented を出さない）を、初版と同じ要約で提示の通知を出して直す（#1182）
type: spec
status: accepted
related_ids: [FR-06, FR-09, FR-14, FR-16, UC-03, UC-04, UC-05, ADR-0003, ADR-0052, IADR-0491, IADR-0116, IADR-0240, IADR-0352, IADR-0470]
author: claude (Claude Code)
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0052 (決定 5「作り直した結果は版を上げて再提示する」「提示の要約の未供給の警告は、作り直した版の記録に従う」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003 (確定は利用者が内容を確認して行う)
  - planning:projects/ai-stock-trading/04_workflows/03_reporting-cycle (ドラフト提示＝要約＋閲覧リンク)
---

# 作り直した版を、初版と同じ要約で提示の通知に載せる（#1182）

## 背景（issue の観測）

- PoC（2026-10-06 23:20 JST・AST ec572e1b）で所有者が `/report regenerate daily-2026-10-06` を実行した。
  Bot の返答は「版 2 として承認待ちにしました…確定は /report approve で」と未供給の警告だけだった。
- report-service は `ReportRegenerated`（監査）と LLM の計上だけを出し、**`ReportDraftPresented` を出していない**。
  notification-service に版 2 の「報告書ドラフト（承認待ち）」の通知は無い。
- `/report show` は本文・要約を取りに行かない（IADR-0240 決定 4「要約は ReportDraftPresented 通知がサニタイズ済みで届ける」）。
- 結果: **作り直した版の中身を利用者が見る経路が無いまま、確定を求めている**（ADR-0003・ADR-0052 決定 5「再提示する」に反する）。

## 実測（origin/develop ec572e1b）

- `ReportRegenerationService.RegenerateAsync` は保存 → 台帳を閉じる → `ApplyReview(Present)` → `ReportRegenerated` の発行 → 応答の順。提示の通知の発行口（`IReportDraftPresentedNotifier`）を持っていない。
- 初版（`ReportAutoGenerator.GenerateAsync`）は提示が受理されたときだけ `ReportSummary.Build(kind, ReportPeriod.Label(kind, start), draft.Pnl, draft.Narrative, unsuppliedInputs, PolicyTakeProfitCheck.WarningFor(kind, policy))` を組み、`PresentedReportNotice` を通知する（best-effort。失敗は生成を巻き戻さない）。
- `IReportDraftPresentedNotifier` は Program.cs で singleton（`NotifyOnDraftPresented` 既定 true なら `MessageBusReportDraftPresentedNotifier`、false なら no-op）。ホストの区別なく登録されているので、作り直しの経路からも同じ実装を引ける。
- 通知サービス: `ReportDraftPresentedNotificationHandler` は受けたイベントを `NotificationFormatter.From(ReportDraftPresented)` で整形してそのまま送る。
  **会話キーや版で重複を抑止する仕組みは無い**（`NotificationService` 配下・`Shared` に dedup/idempotency の鍵を持つ処理が無いことを grep で確認。
  Wolverine の受信の冪等は封筒の ID 単位で、別の発行は別の封筒）。本文は既に「（{PeriodKey}・版 {Version}）」を含み、重大度の上げ方（未供給の印・利確の書式の印 → Warning）は要約の印で決まる。
  → **通知サービス側のコード変更は要らない**。版 1 の後に版 2 が届いても抑止されないことを試験で固定する。
- 監査サービスは `ReportDraftPresented` を封筒の ID で記録する（`report:{periodKey}` 相関）。版 2 の提示は別の行として残る（作り直しの経緯が監査で辿れるようになる）。
- Bot の `/report regenerate` の返答は報告書サービスの `ReportRegenerationResult.Message` をそのまま出す（`ReportCommandHandler.RegenerateAsync`）。

## 計画の確認

- 計画 ADR-0052 決定 5: 「作り直した結果は**版を上げて再提示する**」「**提示の要約の未供給の警告は、作り直した版の記録に従う**」。提示の要約が届くことを前提にしている。
- ADR-0052 が案 B を退けた理由「通知が 2 度出る」は**系が自動で書き換える**案に対するもの。利用者が打った作り直しの再提示に通知を出すことを禁じていない。
- → 計画に反しない。計画への環流は不要。**IADR-0491 決定 5 の最終項（「提示の通知は発行しない」）が誤り**であり、追記で改める（新規 IADR は作らない）。

## 決定

1. `ReportRegenerationService` に `IReportDraftPresentedNotifier` を注入する（必須の引数。黙って通知しない構成を作らない。no-op は Program.cs の構成だけが選ぶ）。
2. 作り直して**承認待ちにできたとき（presented）だけ**、初版と同じ組み立て（`ReportSummary.Build`・`ReportPeriod.Label`・`PolicyTakeProfitCheck.WarningFor(kind, 保った方針)`・この版の `UnsuppliedInputs`）で要約を作り、`PresentedReportNotice(periodKey, kind, label, summary, 新しい版)` を通知する。
   承認待ちにできなかった版は通知しない（初版と同じ。IADR-0116 決定 2）。
3. 通知は best-effort（保存済みの下書きを失敗と伝えない。IADR-0432 監査 1 の規律）。失敗は警告ログに残し、応答に「提示の通知を発行できませんでした」と載せる（黙って捨てない）。
4. 応答（Bot の返答）は、承認待ちにできて通知を発行できたとき「版 N の要約は提示の通知（報告書ドラフト（承認待ち））で届きます」を足す。通知に失敗したときはその旨を足す。それ以外の文は変えない。
5. 通知サービスは変えない（重複の抑止が無く、版を本文に含み、重大度は要約の印で初版と同じに上がる）。試験で固定する。
6. 契約（`ReportDraftPresented`）の形は変えない。発行元の説明のコメントだけ「自動生成と作り直し」に直す。

## 母集合（規則 9・10。誤りの側の文字列で引く）

| 走査 | 結果 | 扱い |
| --- | --- | --- |
| `grep -rn "通知を重ねない\|通知が 2 度\|提示の通知.*発行しない\|ReportDraftPresented.*発行しない\|発行しない.*ReportDraftPresented"`（.git/bin/obj を除く全ファイル） | 4 件: IADR-0491 L88（決定 5 最終項）・ReportService Program.cs L609（no-op の説明。正しい）・OrderExecution の試験 2 件（無関係の保護ストップの通知） | IADR-0491 を追記で改める。他 3 件は別の意味で正しい |
| 索引 `.ai-context/adr/README.md` の IADR-0491 行（「提示の通知は重ねない」） | 1 件 | 行へ日付つき追記を足す（check-adr-index-sync） |
| `grep -rln "regenerate\|作り直し" docs` | 9 ファイル | 作り直しの振る舞いを記述するのは `docs/data/reports.md`（REST の節）と `docs/tests/FR-10_risk-controls-tests.md`（作り直しの節）の 2 つ。残り 7 つ（FR-10 機能仕様の画面の作り直し・migration・runbook・east-west-grpc〔窓口の写しだけ〕・blocked-tasks・FR-15 tests・operations〔クラスタの作り直し〕）は通知に触れない／別の意味の「作り直し」で除外 |
| `grep -rln "報告書ドラフト（承認待ち）\|提示の通知\|確定依頼"`（`.ai-context/specs` を除く） | 22 ファイル | コード: 発行元の説明を「自動生成」に限る記述は契約 `ReportDraftPresented.cs` と `IReportDraftPresentedNotifier.cs`・`NotificationHandlers.cs` → コメントを直す。IADR-0116/0125/0470/0480 は初版の提示の決定で、作り直しに触れない → 除外。試験・appsettings・Options・Hosted は初版の経路 → 除外 |
| `.ai-context/specs/20261006_1156_report-regenerate.md` | 凍結記録 | 書き換えない（point-in-time） |

## 試験（受け入れ基準の写像）

- **T-10-2279**（ReportService）: 作り直して承認待ちにできたら、`ReportDraftPresented` 相当の通知が**ちょうど 1 件**・**新しい版**で出る。要約は初版と同じ組み立て（`ReportSummary.Build` に同じ入力を渡した値と一致）で、未供給の警告と利確の書式の警告を含む。断った作り直し（上限・中核の入力・確定済み）と、提示できなかった・保存に失敗した作り直しは通知しない。通知の失敗は作り直しを失敗にせず、応答に失敗を載せる。
- **T-10-2280**（NotificationService）: 同じ会話キーの版 1 と版 2 の `ReportDraftPresented` をハンドラへ順に渡すと 2 件とも送られ（抑止されない）、版 2 の本文は「版 2」を含み、未供給の印があれば Warning（初版と同じ規則）。
- 変異: 発行を外す → T-10-2279 が赤。通知の版を前の版にする → 赤。要約から利確の警告を落とす → 赤。

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・ReportService / NotificationService / Shared.Contracts の試験・`dotnet format --verify-no-changes`。
- node 検査: check-trace-blocks・gen-knowledge-graph --check・check-commit-messages・check-test-traceability・check-adr-index-sync・check-adr-index-addendum-loss・check-cross-repo-refs・check-plan-id-qualification・check-reading-budget。gitleaks。

## 残余リスク

- `NotifyOnDraftPresented=false` の構成では初版と同じく通知は出ない（no-op は失敗ではないため、応答は「届きます」と書く）。この構成では初版の要約も届かない。
- 通知の発行に失敗した版は、要約を見る経路がやはり無い（応答で失敗を伝える。もう一度作り直すか、通知の復旧を待つ）。
- 監査台帳には版 2 以降の提示の行が足される（作り直し 1 回につき `ReportRegenerated` と `ReportDraftPresented` の 2 行）。
