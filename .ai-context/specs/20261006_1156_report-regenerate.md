---
title: 入力が未供給のまま作られた報告書の下書きを、所有者の操作（/report regenerate）で作り直す（#1156 期待 4・planning#711）
type: spec
status: accepted
related_ids: [FR-06, FR-07, FR-14, FR-16, UC-03, UC-04, UC-05, ADR-0052, ADR-0042, ADR-0047, ADR-0003, IADR-0491, IADR-0480, IADR-0432, IADR-0352, IADR-0115, IADR-0240, IADR-0450]
author: claude (Claude Code)
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0052 (決定 1〜6)
  - planning:projects/ai-stock-trading/04_workflows/03_reporting-cycle (入力が未供給のまま作られた下書きの作り直し)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design (コマンド体系 `/report regenerate <periodKey>`)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates (月報 §7 の作り直しの行)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions (§6.1 作り直しの LLM 費用)
---

# 入力が未供給のまま作られた報告書の下書きを、所有者の操作（/report regenerate）で作り直す（#1156 期待 4）

## 背景

- 2026-10-02、全 Pod の同時起動の直後に Keycloak が未準備で、建玉などの入力が未供給のまま daily-2026-10-02 版 1・weekly-2026-W40 版 1 ができた（#1156）。
- PR #1158（IADR-0480）で、散文へ未供給と 0 を区別して渡し、中核の入力の一過性の欠落では長く見送るようにした。**縮退した下書きを作り直す経路**は計画外のため止め、環流した（IADR-0480 決定 4 → planning#711）。
- 計画の裁定（計画 ADR-0052・利用者裁定 2026-10-02・2026-10-03）: 所有者の操作 `/report regenerate <periodKey>` で作り直す。実装側の残作業は次の 4 つ。
  1. `/report regenerate <periodKey>` を実装する（所有者の門・別枠の回数上限・既定値と 1 回あたりの費用の見積りを IADR に残す）。
  2. 期間の時点に復元できない入力は「未供給」として渡す。中核の入力の取得に失敗したら断って理由を返す。
  3. 作り直した版に、作り直した旨・日時・なお未供給だった入力を記録し、監査に残す。
  4. 月報 §7 に作り直しの回数と費用の実績を載せる。

## 実測（origin/develop 58fe8c24）

- 窓口: Discord の `/report` は `action`（show / approve / request-changes）と `period` の 2 つの選択肢で、`BotCommandParser.ParseReport` が解析し `ReportCommandHandler` が処理する。報告書サービスへは REST（`HttpReportReviewController`・5 秒）か gRPC（`ReportOwnerWrite`。`Reports:Grpc` を宣言したとき）で呼ぶ。`/policy` は LLM を待つため別の抽象・90 秒のクライアントを持つ。
- 所有者の門: 報告書サービスの書き込みは OwnerOnly（REST）／`GrpcOwnerOnly`（所有者 ∧ `azp` がボット）。操作者は `ConfirmingActorResolver`（本文の `onBehalfOf` は信頼クライアントに限る）。
- 自動生成（`ReportAutoGenerator.GenerateAsync`）は期間の行が無いときだけ走り、入力を 14 の供給元から逐次に引く。期間を引数に取らない供給元は**建玉**（`IOpenPositionSource`）と**運用段階**（`IStageProgressSource`）の 2 つだけ（Stage 0 の見積り承認額は構成値で未供給として数えない）。
- `/policy` の回数上限は台帳 `policy_revision_attempts`（`EfPolicyRevisionLedger.TryBegin`・勧告ロック）。費用は計上の境界で `policy-revision` へ付け替え、月報 §7 に独立の行で載せる（IADR-0432）。
- 監査: 報告書サービスはイベント（`ReportConfirmed`・`ReportDraftPresented`・`ReportKnowledgeReingested`）をバスへ出し、監査サービスが記録する。全イベントに監査ハンドラがあることを `AuditConsumerCoverageTests` が要求する。

## 範囲

1. Discord `/report regenerate <periodKey>`・REST `POST /reports/{periodKey}/regenerate`・gRPC `ReportOwnerWrite/RegenerateReport`（IADR-0491 決定 1）。
2. 作り直しの本体 `ReportRegenerationService`（決定 3〜5）。入力の取得と本文の組み立ては `ReportAutoGenerator` から切り出して自動生成と共有する（`CollectInputsAsync`・`DraftFromInputsAsync`）。
3. 試行の台帳 `report_regeneration_attempts`（マイグレーション 1 本）と上限 `Reports:Regeneration:DailyLimit`（既定 5 回/日）。
4. 計上区分 `report-regeneration`（決定 2）と月報 §7 の行（決定 6）。
5. 監査イベント `ReportRegenerated` と監査サービスの記録（決定 5）。
6. IADR-0491・試験 T-10-2260〜T-10-2275（並行の PR #1170 が T-10-2240〜2252 を使うため 2260 からの帯を取った）・自己変異・文書（データ仕様書・east-west gRPC・テスト仕様書・実機確認の手順）。

範囲外:
- 基盤チャット UI からの作り直し（計画 ADR-0052 §結果。Discord から先に用意する）。
- 自動生成・見送り・`/policy` の挙動（変えない。切り出しは同じ順序・同じ判定で行う）。
- 稼働中のクラスタへの配備（本 PR では行わない）。

## 母集合（規則 9。誤りの側＝「作り直しを知らない列挙」から引く）

引き方（origin/develop 58fe8c24・本 PR の変更前）:
`git grep -n "request-changes" -- ':!.ai-context/specs' ':!CHANGELOG.md'`（`/report` の副コマンドの列挙）、
`git grep -n "IsPolicyRevision\|PolicyRevisionUsage\|LlmPurposes.PolicyRevision" -- backend ':!*Tests*'`（独立の計上区分の列挙）、
`git grep -n "GetOpenPositionsAsync\|GetCurrentStageAsync" -- backend/Services/ReportService/Features backend/Services/ReportService/Domain`（「今」しか引けない入力）、
`git grep -n "RecordWatchlistApplyResult" -- backend docs`（`ReportOwnerWrite` の rpc の列挙）。

| 箇所 | 種類 | 扱い |
| --- | --- | --- |
| `BotCommand.cs`・`BotCommandParser.cs`・`DiscordNetBotGateway.cs`（`/report` の選択肢・説明） | 副コマンドの列挙 | **足した** |
| `ReportCommandHandler.cs` | 副コマンドの分岐 | **足した** |
| `report_owner_write.proto`・`ReportOwnerWriteGrpcService.cs`・`docs/api/east-west-grpc.md` の書き込みの表・状態の表 | rpc の列挙 | **足した**（proto の基準は `check-proto-contracts.js --update`） |
| `LlmUsageRecord.cs`（`LlmUsageAggregator`）・`ReportRenderer.cs`（§7） | 独立の計上区分 | **足した**（`report-regeneration` を「その他」へ吸わせない） |
| `LlmPurposes.cs` | 計上区分の語彙 | **足した** |
| `ReportAutoGenerator.cs`（建玉・運用段階の取得） | 「今」しか引けない入力 | **復元できないなら取りに行かない分岐を足した**（`ReportInputs.IsPointInTime` が同じ 2 つを名指す） |
| `docs/data/reports.md`（端点の一覧） | REST の端点の列挙 | **足した** |
| `docs/blocked-tasks.md`（実機確認の手順） | Discord の操作の再測定 | **⑧ を足した** |
| `HttpReportReviewController.cs`・`GrpcReportReviewController.cs` | 照会・確定・差し戻し（5 秒） | **除外**: 作り直しは LLM を待つので別の抽象（`IReportRegenerationController`・300 秒）。レビューの抽象を広げると 5 秒の上限が作り直しにも効いてしまう |
| `.ai-context/adr/IADR-0071`・`IADR-0240`・`IADR-0284`・`IADR-0289`・`IADR-0420` の `request-changes` | 凍結記録 | **除外**: 当時の範囲の記録（本文を後から書き換えない） |
| 各サービスの試験（`BotCommandParserTests` ほか） | 既存の副コマンドの試験 | **除外**: 既存の副コマンドの挙動は変えていない（新しい副コマンドは新しい試験で固定） |
| `IADR-0480` 決定 4・同節の残余リスク「作り直しの経路は無い」 | 凍結記録 | **除外**（本文は変えない）。`docs/tests/FR-10_risk-controls-tests.md` の同じ残余の行は生きた文書なので追記で現状を示す |

規則 10（この変更で新たに誤りになる自分の記述）: east-west gRPC の「書き込み 13 本」は本 PR で 14 本になる → 追記で示した。`docs/tests` の 1156 節の残余「作り直す経路は無い」→ 追記で示した。

## 設計（IADR-0491 の要約）

- 対象は未確定の下書きだけ（確定済み 409・無い 404・下書きを新しく作らない・会話キーと種別・開始日の不整合 409）。
- 上限は入力を引く前に一度見る（429）→ 入力を引く → 中核の入力の取得失敗なら 422 で断る（回数は消費しない）→ 排他区間で数えて書く（429）→ 散文（計上区分 `report-regeneration`）→ 方針の節を保った本文で版を上げて保存（読んだ版で楽観排他）→ 再提示 → 台帳を閉じ、監査を発行。
- 期間がもう現在でなければ、建玉・運用段階を取りに行かず未供給として扱う（取得の失敗に含めない）。期間が現在＝自動生成がいま対象にしている期間（Due）か、今日（JST）を含む期間。
- 本文＝この版の事実と散文（方針の節は保った方針で描く）＋前の本文の最初の記録の見出しから後ろ（`/policy` の改訂の記録・前の作り直しの記録）＋この作り直しの記録。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | 所有者だけが作り直せる（Bot の多層認証・報告書サービスの OwnerOnly / GrpcOwnerOnly。s2s は 403） | T-10-2271（REST・gRPC の門）・T-10-2274（Bot の多層認証） |
| 2 | `/policy` とは別枠の 1 日の上限（既定 5・構成値）。上限で断るときは入力も LLM も呼ばない | T-10-2262・T-10-2272 |
| 3 | 確定済み・無い報告書は作り直さない | T-10-2260・T-10-2261・T-10-2272 |
| 4 | 期間の時点に復元できない入力（期間が過ぎた日報の建玉）は今の値を使わず「未供給」として扱い、散文にもそう渡す。断らない | T-10-2264 |
| 5 | 中核の入力の取得に失敗したら作り直さず理由を返す。回数は消費しない。中核でない入力の欠落では断らない | T-10-2263・T-10-2272 |
| 6 | 方針の節（方針の要約・`/policy` の改訂の記録）は保ち、事実と散文だけを作り直して版を上げ再提示する。確定はしない | T-10-2265・T-10-2268 |
| 7 | 作り直した版に作り直した旨・日時・なお未供給だった入力を記録し、監査に残す | T-10-2265・T-10-2269・T-10-2273 |
| 8 | 費用は月次 LLM 上限に算入しない（計上区分 `report-regeneration`） | T-10-2265・T-10-2266 |
| 9 | 月報 §7 に回数と費用の実績（`<n 回 / N 円 / 上限到達 n 日 / 断り n 回>`）。0 と未供給を混ぜない | T-10-2266・T-10-2267・T-10-2270 |
| 10 | Bot は冪等でない作り直しを再試行せず、届いたか分からない結果を「不明」と伝える。REST と gRPC で同じ結果 | T-10-2275 |

## 自己変異の結果

各変異を 1 本ずつ当て、対象の試験（`ReportRegeneration*`・`ReportRegenerateCommandTests` ほか）を実行して元へ戻した。

| 変異 | 内容 | 赤になる試験（落ちた数） |
| --- | --- | --- |
| M1 | 🔴 所有者の門を外す（作り直しの端点を OwnerOrService の群へ） | T-10-2271（1） |
| M2 | Bot が解決した利用者ではなく固定の値を代理の利用者として送る | T-10-2274（1） |
| M3 | 🔴 入力を引く前の上限の確認を外す | T-10-2262（1） |
| M4 | 🔴 数えて書く排他区間（TryBegin）で上限を見ない | T-10-2262（1。入力を引く間に枠が埋まる場合） |
| M5 | 🔴 断った行（中核の入力）も回数に数える | T-10-2262・T-10-2263・T-10-2270（6） |
| M6 | 🔴 中核の入力の取得失敗で断らない | T-10-2263（4） |
| M7 | 🔴 復元できない入力も「取得の失敗」に数える | T-10-2264 ほか（5） |
| M8 | 🔴 期間が過ぎていても建玉を取りに行く | T-10-2264（4） |
| M9 | 前の本文の改訂の記録を保たない | T-10-2265（2） |
| M10 | 方針の要約を保たない | T-10-2265（1） |
| M11 | 散文の費用を付け替えない | T-10-2265（1） |
| M12 | 監査を発行しない | T-10-2265（1） |
| M13 | 作り直しの記録の利用者の行を本文へ足さない | T-10-2265・T-10-2271（2） |
| M14 | 楽観排他を外す（保存の直前に読んだ版で保存する） | T-10-2268（1） |
| M15 | 確定済みも作り直す | T-10-2260（1） |
| M16 | 作り直しの費用を「その他」へ吸わせる | T-10-2266（2。集計とゴールデン） |
| M17 | 上限到達の日を「上限で断った日」だけで数える | T-10-2270（1） |
| M18 | 上限の構成値の 0 を許す | T-10-2262（1） |
| M19 | 解析が版番号つきの regenerate も受ける | T-10-2274（1） |
| M20 | Bot が届いたか分からない結果を「作り直していない」と伝える | T-10-2275（1） |
| M21 | 月報の自動生成が台帳を引かない | T-10-2267（1） |

21 本すべて赤（生存 0）。**M4 は初回に生存した**——入力を引く前の確認が先に断るので、排他区間（TryBegin）の上限を見る試験が無かった。
入力を引く間に別の要求が枠を使い切る試験（T-10-2262 の 2 本目）を足して赤にした。

## 配備の注意

- report-service: 起動時のマイグレーションで表 `report_regeneration_attempts` を足す（既存の表は変えない）。構成は既定で足りる（`Reports:Regeneration:DailyLimit` 既定 5）。
- notification-service: Discord のスラッシュコマンドの再登録（起動時に `report` の選択肢へ `regenerate` が増える）。REST なら新しい名前付きクライアント（300 秒）、gRPC なら `Reports:GrpcRegenerationTimeoutSeconds`（既定 300）。
- audit-service: `ReportRegenerated` の購読（ハンドラの追加。キューは既存の規則で作られる）。
- 配備の順序: audit-service（購読。先に上げないと最初の作り直しの監査イベントの受け手が無い）→ report-service（端点・rpc・マイグレーション）→ notification-service（コマンド）。notification が先だと Bot が 404 / `UNIMPLEMENTED` を受ける（作り直していない＝安全側）。
