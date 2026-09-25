---
title: Discord の自由文の指示から AI が方針の改訂案を作り、利用者が確定する（#1016）
type: spec
status: accepted
related_ids: [FR-07, FR-14, FR-04, FR-13, UC-03, UC-04, UC-05, ADR-0003, IADR-0431, IADR-0240, IADR-0115, IADR-0120, IADR-0116, IADR-0169, IADR-0420]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-07 対話で確定・FR-13 設定は画面から・FR-14 報告書の質疑・修正指示・確定／設定値の変更は参照のみ)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-03 基本フロー 4「利用者が対話で質疑・修正指示を行い、報告書サービスがドラフトを更新する」)
  - planning:projects/ai-stock-trading/04_workflows/03_reporting-cycle.md (対話的確定のシーケンス REP→GW・日報の内容「翌営業日の目標・監視銘柄・売買条件」)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (自然文の修正指示を対話へ中継・監視銘柄は Discord から参照のみ・確定は版番号付き冪等)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (方針の確定には利用者との対話を要する・追補 2026-08-10 注入対策 2 点)
---

# 仕様書: Discord の自由文の指示から AI が方針の改訂案を作り、利用者が確定する（#1016）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-07（対話を経て確定・確定前の方針は取引に適用しない）、FR-14（Discord から報告書の質疑・修正指示・確定）
- ユースケース（UC）: UC-03（基本フロー 4）、UC-04 / UC-05（UC-03 に準ずる）
- 画面（SC）: なし（Discord のみ）
- 関連 ADR: ADR-0003（方針の確定には利用者との対話を要する・AI の判断入力は確定済み日報の範囲）
- 関連 IADR: IADR-0431（本件の決定）、IADR-0240（Discord の報告書レビュー・版番号付き冪等確定・OnBehalfOf）、
  IADR-0115 決定 4（「LLM による方針提案は、Discord 経由の対話と合わせて別途設計する」＝本件がその設計）、
  IADR-0120（種別ごとの purpose）、IADR-0116（投稿本文の無害化は発行側）、IADR-0169（注入対策）、IADR-0420（送り手の本物の型の契約テスト）

## 計画の射程（着手時に確認した結果）

| 利用者要望の部分 | 計画の根拠 | 扱い |
| --- | --- | --- |
| Discord の自由文の指示で AI が報告書の方針（`PolicySummary`）を改訂する | FR-14「報告書の質疑・**修正指示**・確定」、07_discord-bot-design のコマンド体系「通常メッセージ … 報告書への質疑・修正指示（自然文）」、UC-03 基本フロー 4、03_reporting-cycle シーケンス「REP→GW: 対話文脈でドラフト更新」 | **実装する** |
| 改訂案を利用者が確定する | FR-07、ADR-0003「方針の確定には必ず利用者との対話を要する」、07 §二重実行防止（版番号付き冪等） | **既存の確定経路（確認ボタン→版番号付き確定・OnBehalfOf）をそのまま使う** |
| AI が監視銘柄の入れ替え案を作る | 03_reporting-cycle「日報＝翌営業日の目標・**監視銘柄**・売買条件」、04_report-templates「監視銘柄と売買条件」「監視銘柄の入れ替え: <追加/除外と理由>」 | **報告書の内容（案）として作り、提示し、改訂版の本文に記録する** |
| 監視銘柄の変更を Discord の確定で**適用する** | FR-14「設定値の変更は Discord からは参照のみとし、kill switch と一時停止/再開のみを例外とする」、07「設定値の変更（リスク上限・**監視銘柄**・取引ガード）は Discord からは参照のみ」、FR-13（画面から変更・理由必須・監査・楽観排他） | 🔴 **計画が禁じている。実装しない。** planning へ環流する（下書きは PR 本文と報告に添付。起票は利用者） |

自由文を「スラッシュコマンドの引数」で受けることは、07 が挙げる「ドラフト通知へのリプライ」と窓口の形が違うだけで、
中身（報告書への修正指示を対話文脈へ渡してドラフトを更新する）は同じである。07 §リスク・未決事項は
「スラッシュコマンドの登録・更新手順 … は実装時に確定する」としている。リプライ（MessageContent Intent を要する）は
IADR-0062 決定 2 が最小 Intents のため採っていない。

## 母集合（着手時に引いた結果と除外。規則 1〜6・9）

- **追随先の走査**: `git grep -l -E "request-changes|/report approve|/drift adopt"`（コマンドを列挙する箇所）と
  `git grep -l -E "DiscordNetBotGateway\(|DiscordBotGatewayFactory.Create\("`（ゲートウェイの組み立て）、
  `git grep -n -E "report-review|/confirm|daily-policy|/reports/" -- docs`（報告書の操作を列挙する文書）。
  - 追随する: `BotCommand.cs` / `BotCommandParser.cs`（種別と解析）、`DiscordNetBotGateway.cs`（登録・分岐・補完）、
    `DiscordBotGatewayFactory.cs`（引数）、`Program.cs`（通知・報告書の両サービス）、`DiscordSettingsAreReadOnlyTests.cs`
    （否定形に新ハンドラを加える）、`DiscordBotGatewayFactoryTests.cs`、`docs/data/reports.md`（§照会・操作）、
    `docs/blocked-tasks.md`（A-7a の実機確認手順に項を足す）。
  - 除外: `docs/functional/FR-10_risk-controls.md` / `docs/tests/FR-10_risk-controls-tests.md`（`/drift adopt` の記述で一致。本件と無関係）、
    `RiskControlEndpoints.cs`（同）、`SecretRedactionTests.cs`（ゲートウェイの組み立ての一致だが、秘密の伏字の検査で引数列に依存しない——ビルドで確認）、
    `docs/api/openapi.yaml`（報告書のパスを載せていない。生成物であり手で足さない）、`docs/security/security.md`（`OwnerOrService` の表。新端点は `OwnerOnly` で表の対象外）、
    `.ai-context/specs/20260829_w11s6_*`（凍結記録）。
- **軸 2（誤りの側）**: 「監視銘柄を Discord から変える」経路が生えないこと。`git grep -n -i "watchlist" backend/Services/NotificationService` →
  既存は否定形テストだけ。本件でも通知サービスに監視銘柄の書き込みを持つ型を足さない（新しい否定形テストで固定）。

## 決定する挙動

### 報告書サービス（`POST /reports/policy-revisions`・OwnerOnly）

要求 `{ instruction, periodKey?, onBehalfOf? }`。応答は下表。**何を保存したか／していないか**を必ず区別して返す（原則 A）。

| 状況 | 応答 | 保存 |
| --- | --- | --- |
| 指示が空・1000 文字超 | 400 | しない |
| `periodKey` 書式外 | 400 | しない |
| 対象が確定済み | 409「確定済みの報告書は改訂できません」 | しない |
| 対象が無く、当日（JST）の日報でもない | 404「新しく作れるのは当日（JST）の日報だけ」 | しない |
| 対象が無く当日の日報で、**営業日**かつ自動生成が有効（その日の自動生成の日報がまだ無い） | 409「まだ自動生成されていない。いま作ると数値入りの自動生成の日報が作られなくなる。自動生成（16:00 JST）の後に /policy を実行すればそのドラフトを改訂する」 | しない（利用者裁定 2026-09-26） |
| 対象が無く当日の日報だが、確定済み日報が 1 件も無い | 409「土台の方針が無い」 | しない |
| 対象が無く当日の日報だが、それより新しい日付の確定済み日報がある | 409「確定しても方針に効かない」 | しない |
| LLM 未構成・失敗・拒否・空・タイムアウト・禁止モデル・出力が形式違反 | 502「AI の案を作れませんでした（理由）。方針は変わっていません」 | **しない** |
| 案を作れた | 200（会話キー・版・提示できたか・新規か・方針案・監視銘柄の入れ替え案・説明） | 新しい版のドラフトを保存し提示（承認待ち）。**確定はしない** |
| 保存の競合（LLM を待つ間に報告書が更新された） | 409 | 案は捨てる（読んだ時点の版で楽観排他） |
| 保存の後の提示が失敗（保存と提示の間の並行更新） | 200・`presented=false`（確認ボタンなし） | 保存済み・未提示（409 にしない＝「何も保存していない」と誤って伝えない） |

- `periodKey` 省略時は当日（JST）の日報 `daily-<yyyy-MM-dd>`（`ReportSchedule.JstOffset`）。
- 既存の未確定の報告書は「その方針」を土台に改訂する（版 +1・レビュー局面は Drafting→提示で PendingApproval）。新規は直近の確定済み日報の
  方針・`BasedOn`・`AssumptionsVersion` を土台にする（数値を発明しない）。
- 🔴 **営業日にまだ自動生成されていない当日の日報は作らない**（利用者裁定 2026-09-26。監査 #1020）。作ると自動生成は既存の行を
  踏まない規則（IADR-0115 決定 3）でスキップされ、その日の数値入りの日報が失われる。境界（16:00 JST）の前でも、境界を過ぎて
  まだ生成されていない（依存先の見送り中など）ときも同じ。**自動生成の後に `/policy` を実行すれば、そのドラフトを改訂する。**
  休場日（週末・構成の休場日）と、自動生成が無効な構成（止める生成が無い）では作ってよい。判定は自動生成と同じ構成
  （`Reports:AutoGeneration` の境界・休場日・有効化）を使う。
- 本文（Body）の末尾に「利用者の指示による方針の改訂（版 N）」節を追記する（指示者・UTC 時刻・指示の原文〔引用〕・改訂後の方針・監視銘柄の入れ替え案・説明）。
  既存の本文と未供給の入力の記録は残す。確定時にこの本文が KB へ保存され、誰がいつ何を指示したかの記録になる。
- 改訂者は確定と同じ `ConfirmingActorResolver`（信頼クライアントのトークンに限り `onBehalfOf`）。値域外は 400。

### LLM（出力は厳格な JSON）

- 輸送・purpose・費用計上・割当逸脱の通知は散文ドラフトと同じ（`ILlmCompletionTransport`・`report-daily` 等・`ILlmUsageReporter`・`ILlmGovernanceReporter`）。
  上限は `Reports:PolicyRevision:TimeoutSeconds`（既定 60 秒）。
- プロンプトはデータ／命令の構造分離（ADR-0003 追補 1）: 現在の方針・上位方針・**利用者の指示**を、フェンス内の 1 行 JSON 文字列で渡す（改行・見出しで節を偽装できない）。
- 出力スキーマ `{"policySummary": string, "watchlistChanges": [{"action": "add"|"remove", "symbol": string, "reason": string}], "rationale": string?}`。
  検証（純関数）: 方針は空不可・2000 文字以下／入れ替え案は追加 5 件・除外 5 件まで・銘柄は米国のティッカー書式 `^[A-Z]{1,5}([.-][A-Z]{1,2})?$`・重複不可・理由は空不可 200 文字以下／
  説明は 1000 文字以下／**1 つでも外れたら案全体を捨てる**（部分採用しない）。未知の項目は読まない（使わない）。
- 監視銘柄の入れ替え案は**提示と記録だけ**。適用は SC-02（設定画面）で行う（FR-13・FR-14）。

### 通知サービス（Discord `/policy`）

- `/policy instruction:<自由文・必須・1000 文字まで> period:<任意・入力補完>`。多層認証 → 解析（`/policy` / `/policy <periodKey>` のみ）→ 指示の検証 → 報告書サービス。
- 応答は複数の通に分ける: ①見出し ②**方針の全文**（切り詰めない。1 通 1800 文字に収まらなければ `【方針案 i/n】` の通に分割）
  ③入れ替え案〔「表示のみ。適用は設定画面から」〕と説明。確定ボタン（`ast-report-approve-<periodKey>-<version>`）は、提示できたときだけ
  **③にだけ**付け、①②を送り終えた後にしか出ない（ADR-0003: 確定される方針を利用者が全部見てから確定する。監査 #1020 BLOCKING 1）。
  切り詰めてよいのは確定されない表示（入れ替え案の理由を先に 60 文字へ、次に説明）だけ。
- 報告書サービスの応答の方針・理由・説明は、メンション構文と Discord のマスクリンク `[表示](URL)` を**幅ゼロ空白の挿入だけ**で崩して返す。
  素の URL は URL のまま見えるため崩さない。🔴 **表示される方針は、保存される方針と幅ゼロ空白を除いて同一**（空行を畳まない・文字を
  落とさない・切り詰めない）。正規化（制御文字・CRLF・前後の空白）は検証の 1 回だけで行い、その結果を保存する。収集情報の境界語を
  含む出力は検証が案全体を捨てる。
- 途中の通の送信に失敗したらボタンを出さず、承認待ちの版と確定（/report approve）・やり直し（/policy）の方法を知らせる。
  承認待ちにできなかった案は報告書サービスの案内を見せる。
- `/policy` の period が書式外なら「許可されていない」ではなく形式の案内を返す（呼ばない）。
- 報告書サービスの 4xx/5xx は本文の `error` を表示する。**タイムアウトは「結果は不明（`/report show` で確認）」**（保存された可能性を「失敗」と言わない）。
- 呼び出しの上限は 90 秒（専用の名前付き HttpClient `report-policy-revision`。既存の 5 秒のクライアントは変えない）。
- 指示の原文はログに出さない（長さだけ）。

## 受け入れ基準 → テスト（T-10-1300〜T-10-1349 を予約。使用は T-10-1300〜1355）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | 出力スキーマの検証（正常・各違反で案全体を捨てる・フェンス付き JSON を読む・境界語は捨てる・空行を畳まない） | `PolicyRevisionProposalParserTests`（T-10-1300〜1305・1353・1354） |
| 2 | プロンプトが指示を 1 行 JSON で渡し、節を偽装できない | `LlmReportPolicyReviserTests`（T-10-1306。プロンプトの組み立て `PolicyRevisionPromptBuilder` は送信要求で観測する） |
| 3 | LLM の各失敗で案なし（理由を区別）・費用を計上・禁止モデルは捨てる | `LlmReportPolicyReviserTests`（T-10-1307〜1310） |
| 4 | 改訂の表（既存の改訂・新規・確定済み・土台なし・効かない・LLM 失敗で保存しない・本文の追記・提示・**営業日の自動生成前は作らない**・**LLM を待つ間の更新で保存しない**・**保存後の提示の失敗は保存済み未提示**） | `ReportPolicyRevisionServiceTests`（T-10-1311〜1318・1336〜1340・1355〔JST の日付の境界〕） |
| 5 | 本番の Program.cs を通した組み立て（LLM 未構成で 502・保存しない／確定で方針になる／代理の改訂者／**サービス主体は 403**／**メンションとマスクリンクを崩す**／**方針を切り詰めない**） | `PolicyRevisionWiringTests`（報告書。T-10-1319〜1321・1341〜1343） |
| 6 | Discord ハンドラ（認可・解析・指示の検証・確認ボタンの版・失敗の表示・**書式外の period**・**ボタンが出るとき方針の全文が届いている**） | `PolicyRevisionCommandHandlerTests`（T-10-1322〜1328・1344〜1347） |
| 6b | 送り順・ボタンは最後の通だけ・途中失敗でボタンなしと承認待ちの版の知らせ | `PolicyRevisionReplySenderTests`（T-10-1348〜1351） |
| 7 | 越境の契約（送り手の本物の型で要求・応答を直列化）と失敗・不明の区別・**表示と保存の一致（分割をまたいで）** | `HttpPolicyRevisionControllerTests`（通知・extern alias `ReportWorker`。T-10-1329〜1333・1352） |
| 8 | 設定変更の試みで新ハンドラも何も呼ばない・通知サービスに監視銘柄の書き込み口が無い | `DiscordSettingsAreReadOnlyTests` 追補（T-10-1334・1335） |

## やらないこと

- 監視銘柄の適用（計画外）。案の構造化した保存（列の追加）。週報・月報の新規作成（既存の未確定の週報・月報の改訂はできる）。
- 改訂回数の上限（費用の統制は残余リスクとして IADR-0431 と環流の下書きに記録する）。
