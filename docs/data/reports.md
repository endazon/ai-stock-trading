---
title: 報告書（reports）データ仕様書
type: data-spec
status: review
created: 2026-07-10
updated: 2026-10-06
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-06, FR-07, FR-08, FR-11, FR-14, FR-16, FR-17, UC-03, UC-04, UC-05]
adrs: [ADR-0001, ADR-0003, ADR-0042, ADR-0052]
iadrs: [IADR-0012, IADR-0024, IADR-0240, IADR-0352, IADR-0418, IADR-0431, IADR-0432, IADR-0433, IADR-0436, IADR-0480, IADR-0491, IADR-0492]
specs: [20260710_report-confirmation, 20260919_774_report-confirmed-actor-on-behalf-of, 20260919_840_report-transient-dependency-retry, 20260925_843_report-period-keys-projection, 20260926_1016_policy-revision-from-discord, 20260926_1024_policy-daily-limit, 20260926_1025_policy-watchlist-apply, 20260926_1028_report-kb-reingest, 20261006_1156_report-regenerate, 20261006_1172_report-us-session-window]
issues: [#14, #18, #19, #22, #63, #774, #840, #843, #1016, #1024, #1025, #1028, #1156, #1172, planning#711]
-->

# データ仕様書: 報告書（reports）

> 報告書サービス（`ReportService`）が所有する報告書（日報/週報/月報）の永続化。取引方針の階層管理と対話的確定、
> 報告書テンプレートによる集計、全体前提条件のバージョン記録を支える。設計は「報告書サービスが確定管理と確定済み
> 日報方針を所有し、確定はイベントで通知する」、版番号確定は「単一行 JSON ＋バージョン列で永続化し楽観的排他制御する」
> を踏襲する。作業仕様は 仕様書: 報告書サービス Slice A（確定管理・方針の実体）。

## 本書が受け持つ範囲

- 機能要求: 取引方針の階層管理、報告書の対話的確定（確定前の方針は不適用）、報告書テンプレートによる集計、全体前提条件のバージョン記録
- ユースケース: 日報・週報・月報の対話的確定
- 計画 ADR: 基盤採用（Database per Service）、生成AIの売買判断の拘束（確定は利用者のみ・確定前方針は不適用）

## ドメイン型（`TradingReport`）

| 属性 | 型 | 説明 |
| --- | --- | --- |
| PeriodKey | string | 自然キー（"daily-2026-07-10" / "weekly-2026-W28" / "monthly-2026-07"） |
| Kind | ReportKind | Daily / Weekly / Monthly |
| PeriodStart | DateOnly | 対象期間の開始日（最新の確定済み日報照会に使用） |
| State | ReportState | Draft / Confirmed（確定した方針のみ取引に適用） |
| BasedOn | string? | 参照した上位方針の PeriodKey（daily→週報 / weekly→月報 / monthly→前月報） |
| AssumptionsVersion | int | 適用した全体前提条件のバージョン（#19） |
| PolicySummary | string | 翌期間の方針（確定で有効化） |
| UnsuppliedInputs | ReportInput の列 | 供給が届かないまま生成された入力（その種別が使うものだけ）。空＝欠けた入力なし。提示通知の要約とレビュー照会（`GET /reports/{periodKey}/review`）が表示名で返し、確定の前に欠落へ気付けるようにする |
| ConfirmedAt | DateTimeOffset? | 確定日時 |

## エンティティ定義（`reports`・永続）

| 属性 | 型 | 説明 |
| --- | --- | --- |
| PeriodKey | string(64) (PK) | 自然キー |
| Kind / State | 列挙（int 格納） | 種別・状態 |
| PeriodStart | date | 期間開始日 |
| BasedOn | string(64)? | 上位方針参照 |
| AssumptionsVersion | int | 前提条件バージョン |
| PolicySummary | string(8192) | 方針テキスト |
| UnsuppliedInputs | string(1024)? | 供給が届かないまま生成された入力（入力名のカンマ区切り・宣言順）。NULL＝欠けた入力なし（列の追加前に作られた行も NULL）。**本文に従う**——本文を差し替えない改訂では残し、確定しても残る |
| ConfirmedAt | timestamptz? | 確定日時 |
| Version | int（並行トークン） | 版番号付き冪等確定の楽観排他 |

- インデックス: `(Kind, State, PeriodStart)`（最新の確定済み日報の照会）。

## 照会・操作

- `GET /reports`、`GET /reports/{periodKey}`、`GET /reports/daily-policy`（確定済み日報方針＝Date/Summary/AssumptionsVersion・未確定は 404）。
- `GET /reports/period-keys`（会話キーと開始日だけの軽い一覧＝`[{periodKey, periodStart}]`・開始日の降順、同日は会話キーの降順・ページングなし）。Discord の `/report` の入力補完が打鍵ごとに読むため、本文・要約・状態を載せない。OwnerOnly。`/{periodKey}` より優先される（`daily-policy` と同じくリテラル一致）。
- `PUT /reports/{periodKey}`（ドラフト upsert・楽観排他）、`POST /reports/{periodKey}/confirm`（版番号付き冪等確定）。すべて OwnerOnly。
- `POST /reports/policy-revisions`（方針の改訂案。OwnerOnly）: 要求は `{instruction, periodKey?, onBehalfOf?}`。利用者の自由文の指示（1000 文字まで）から
  AI が方針の改訂案を作り、対象の報告書へ**新しい版のドラフト**として保存して提示（承認待ち）する。**確定はしない**（確定は上の確定 API）。
  対象は `periodKey` 省略時に当日（JST）の日報。既存の未確定の報告書は改訂し、確定済みは 409。存在しない会話キーで新しく作れるのは当日の日報だけで、
  土台は直近の確定済み日報（方針・`BasedOn`・`AssumptionsVersion`）。確定済み日報が無い・より新しい確定済み日報がある場合は 409。
  **営業日にまだ自動生成されていない当日の日報は作らない**（409。作ると数値入りの自動生成の日報が作られなくなるため。自動生成の後に
  改訂する。休場日と自動生成が無効な構成では作れる。判定は `Reports:AutoGeneration` の境界・休場日・有効化）。
  改訂の記録（指示者・時刻・指示の原文・案・監視銘柄の入れ替え案・説明）は本文（Body）の末尾へ追記する。
  応答は 200＝`{periodKey, version, created, presented, message, policySummary, watchlistChanges[{action, symbol, reason}], rationale}`
  （文字列は投稿向けに無害化済み＝メンション構文とマスクリンクを幅ゼロ空白の挿入だけで崩す。方針は保存される方針と幅ゼロ空白を除いて同一）／400＝指示・会話キー・代理指定の不正／404＝対象なし／
  409＝確定済み・土台なし・確定しても効かない・営業日で当日の日報が未生成・並行更新（LLM を待つ間の更新）／502＝AI の案を作れなかった。
  **200 以外では何も保存しない。** 保存の後に提示だけ失敗したときは 200・`presented=false`。
  監視銘柄の入れ替え案は、Discord の `/policy` 専用の確認ボタンで確定できたときだけ市場監視サービスで適用される（`/report approve` では適用しない）。
  要求の `currentWatchlist`（`[{symbol, market}]`・market は `UnitedStates` / `Japan`）は Bot が照会した現在の監視銘柄で、米国の銘柄を AI へ渡し、
  試行の台帳に記録する（入れ替え案の適用の楽観排他の基準）。null は「照会できなかった」（その案の入れ替えは適用しない）。形式違反は 400。
- `GET /reports/policy-revisions/watchlist-proposal?periodKey=&version=`（OwnerOnly）: 確定した版を作った `/policy` の試行の案
  （`{attemptId, periodKey, reportVersion, changes[], snapshot[] | null, applyRecorded}`）。その版が `/policy` の案でなければ 404、
  報告書が**その版で確定されていなければ 409**（確定済みかつ現在の版＝要求の版＋1 のときだけ返す）。
- `POST /reports/policy-revisions/{attemptId}/watchlist-apply-result`（OwnerOnly）: 入れ替え案の適用の内訳の記録（`{outcome, items[], message, onBehalfOf}`）。
  **1 回だけ**（2 回目は 409）。確定された案の試行（Proposed かつ報告書がその版で確定済み）以外も 409。適用そのものは市場監視サービスが行う（`/policy` 専用の確認ボタンで確定できたときだけ）。
  改訂者は確定と同じ規則（信頼クライアントのトークンに限り `onBehalfOf`）。LLM の上限は `Reports:PolicyRevision:TimeoutSeconds`（既定 60 秒）。
  **1 日（JST の暦日）の回数上限**は `Reports:PolicyRevision:DailyLimit`（既定 10 回）。上限に達した要求は LLM を呼ばず **429** で断る。
  数えるのは LLM を呼んだ試行（失敗も含む）で、入力の検証・対象の決定で断った要求は数えない。
- `POST /reports/{periodKey}/regenerate`（**未確定の下書きを、その期間の入力で作り直す**。OwnerOnly。Discord の `/report regenerate <periodKey>`・gRPC `ReportOwnerWrite/RegenerateReport` も同じ処理）:
  入力が未供給のまま作られた下書き（起動直後に依存先が未準備だった等）を、所有者の操作で作り直す。**系が自分で下書きを書き換える経路は無い。**
  対象は未確定の下書きだけ（確定済みは 409・無い報告書は 404・下書きを新しく作らない）。要求は `{onBehalfOf?}`（作り直した利用者。確定と同じ規則で信頼クライアントのトークンに限り採る）。
  **方針（方針の要約と `/policy` の改訂の記録）は保ち、事実と散文の節だけを作り直して版を上げ、承認待ちへ再提示する。** 確定は従来どおり版番号つきの確定だけが行う。
  入力は自動生成と同じ供給元・同じ規則で引く。**期間がもう現在でない報告書では、「今」しか引けない入力（建玉・運用段階）を取りに行かず「未供給」として扱う**
  （今の値を期間の値として書かない。期間が現在＝自動生成がいま対象にしている期間か、今日〔JST〕を含む期間）。
  **中核の入力（約定・建玉・手動売買の取り込み）の取得に失敗したら作り直さず 422** で理由（入力の表示名）を返す（下書きはそのまま・回数は消費しない）。前文の「未供給として扱う」入力は取得の失敗に含めない。
  **1 日（JST の暦日）の回数上限**は `Reports:Regeneration:DailyLimit`（既定 5 回・`/policy` とは別の枠）。上限に達した要求は入力も LLM も呼ばず **429** で断る。
  数えるのは上限の門を通って散文を組み立てに行った試行（失敗も含む）で、上限・中核の入力で断った要求は数えない（台帳には残る）。
  散文の LLM 費用は用途キー（モデル割当）を変えずに計上区分 `report-regeneration` へ付け替える（月次 LLM 上限の対象外。月報 §7 に回数・費用・上限到達の日数・断った回数を載せる）。
  作り直した版の本文の末尾に「報告書の作り直しの記録（版 n）」（作り直した利用者・日時・作り直す前の版・なお未供給だった入力・復元できなかった入力）を足し、
  `UnsuppliedInputs` はこの版の記録に従う（方針の連鎖の未供給は前の版から引き継ぐ）。試行は `report_regeneration_attempts` 表に残し、作り直せたときは監査台帳へ `ReportRegenerated` を発行する。
  応答は 200＝`{periodKey, previousVersion, version, presented, message, unsuppliedInputs[], notRestorableInputs[]}`（入力は表示名）／400＝会話キー・代理指定の不正／404／409＝確定済み・期間の不整合・並行更新（散文を待つ間の改訂）／422／429。
  **200 以外では下書きを変えていない。**
- `POST /reports/knowledge-base/reingest`（**確定済みの報告書を KB へ入れ直す**。OwnerOnly）: 基盤の切替で消えた KB 上の写しの復旧と、
  本文なしで入った写しの修復に使う。要求は `{all?, fromPeriodKey?, toPeriodKey?, refreshExisting?}`。全件は `all: true` で明示する
  （範囲との併用・指定なしは 400）。範囲は期間キーを期間へ直して読む（日報＝その日・週報＝ISO 週の月〜日・月報＝その月。
  `from` の期間の初日 ≦ 開始日 ≦ `to` の期間の末日・種別を問わない・片側だけも可・逆順と形の違いは 400）。対象は確定済みだけ。
  基盤には外部 ID での照会・upsert が無いため、**KB の文書一覧を先に 1 回だけ引き**、期間キー・種別が一致し、`project=ai-stock-trading` を持つか、`project` を持たず表題が `確定報告書 <種別> <期間キー>` と完全に一致する文書（2026-09-03 より前の保存の形）を写しとみなす。**写しがあれば作らない。**
  無ければ本文つきで作る／本文が無ければ本文を入れる（文書は増えない）／本文があれば何もしない（`refreshExisting: true` のときだけ本文を入れ直す＝索引の作り直し）。
  **2 回実行しても KB の件数は変わらない。** 一覧を引けなければ 1 件も書かない。報告書ごとの結果（`items[].outcome`）は
  `Created`・`BodyAttached`・`BodyRefreshed`（送った）／`AlreadyPresent`／`SkippedEmptyBody`・`SkippedBodyTooLarge`（送らなかった。本文が空・1 MB 超）／
  `Failed`（拒否された・届かなかった。理由つき）／`Unknown`（タイムアウト等で結果が分からない。次の実行は一覧で見つければ作り直さない）／`NotAttempted`。
  本文なしの写しへの投入は `project` ありの写しから順に試し、基盤が 404（本システムの所有ではない旧い写し）なら次の写しへ進む。すべて 404 なら `Failed`（別の主体の所有。基盤の管理者が削除してから入れ直す。作らない）。同じ報告書の写しが複数あれば本文のあるほうを採り、行の `matchedCopies`（写しの数）と件数 `duplicatesInKb` で返す。年 9999 の期間キーは 400。
  応答は 200＝実行した（個別の失敗・不明を含み得る）／503＝KB が構成されていない／502＝KB の文書一覧を引けなかった（**503・502 では 1 件も書いていない**）／
  409＝実行中（同時に 1 本だけ）。200・503・502 は監査台帳へ `ReportKnowledgeReingested`（操作者・範囲・件数・内訳）を残す。
  宛先・資格は確定時の保存と同じ構成（`KnowledgeBase:Documents:BaseUrl`・`KnowledgeBase:Auth`）で、1 回の呼び出しのタイムアウトは 30 秒。
- **版番号付き冪等確定**: Draft→Confirmed の遷移時のみ `ConfirmedAt` 記録＋`ReportConfirmed` 発行（通知サービスが Discord 通知）。
  既に確定済みの再確定は冪等（状態変化なし・イベント重複なし）。版不一致は 409、確定済みの変更は 409、未認証 401/無権限 403。
  応答は報告書の項目に `transitioned`（この要求で確定したか）と `version`（確定後の版）を足したもの。確定は版を 1 進めるため、
  冪等な再確定では `version == expectedVersion + 1` のときだけ「その版で確定されている」（別の版で確定済みを取り違えない）。
- **確定者の解決**: 確定要求の本文は `expectedVersion` と任意の `onBehalfOf`（代理される利用者＝Keycloak 利用者名）。
  Discord Bot は機密クライアント（`client_credentials`）のトークンで確定を呼ぶため、トークンからは人を解決できない。
  `onBehalfOf` は **トークンの `azp` が構成 `Reports:DelegatedActor:TrustedClientIds`（カンマ区切り・既定は空＝誰も信じない）に
  一致するときだけ**確定者として採り、`ReportConfirmed` には `Actor`＝操作した利用者と `AuthorizedBy`＝認可の主体（クライアント ID）の
  両方を載せる。利用者本人のトークンや一覧外のクライアントが送った `onBehalfOf` は**無視**する（確定は通り、確定者はトークンの主体）。
  信頼クライアントが値域外（`[A-Za-z0-9._@+-]` の 1〜64 文字以外）の値を送ると 400 で確定しない。
  操作者が取れないとき（名前クレームの無いトークンで `onBehalfOf` も無い）は `client:<azp>` を確定者にする。

## 方針の改訂の試行（policy_revision_attempts）

`/policy` の試行の台帳。LLM を呼ぶ直前に 1 行書き、結果で閉じる。1 日の回数上限の判定と、案の監査記録を兼ねる。

| 列 | 型 | 説明 |
| --- | --- | --- |
| Id | uuid（主キー） | 試行の識別子 |
| AttemptedAt | timestamptz | 試行の時刻 |
| JstDate | date（索引） | 回数上限を数える JST の暦日 |
| Actor | varchar(128) | 指示者（代理の解決後） |
| PeriodKey | varchar(64) | 対象の会話キー |
| Outcome | int | 0＝Pending（呼び出し中・または途中で落ちた）／1＝Proposed／2＝AiFailed／3＝SaveFailed |
| ReportVersion | int? | 案を保存した報告書の版（Proposed のときだけ） |
| WatchlistChangesJson | text? | 案の監視銘柄の入れ替え（`[{action, symbol, reason}]`。Proposed のときだけ） |
| WatchlistSnapshotJson | text? | 案を作った時点の監視銘柄（`[{symbol, market}]`）。NULL＝照会できなかった（空の一覧は `[]`） |
| WatchlistApplyJson | text? | 入れ替え案の適用の内訳（`{outcome, recordedBy, items[], message}`）。適用を試みた後だけ |
| WatchlistAppliedAt | timestamptz? | 内訳を記録した時刻（記録は 1 回だけ） |
| ProposalConfirmedAt | timestamptz? | 報告書がこの試行の版で確定された時刻（確定の遷移で書く）。これがあって `WatchlistAppliedAt` が無い行は「確定されたが入れ替えの適用を試みていない」 |

- 数えることと書くことは 1 つの排他区間で行う（Postgres では JST の暦日を鍵にした勧告ロック `pg_advisory_xact_lock` を
  トランザクションで取る）。同時の要求で上限を超えない。
- JSON は日本語を逃がさずに書く（`\uXXXX` にしない）。列は長さの上限を持たない `text`。
- 保存の後に台帳の書き込みが失敗しても、保存済みのドラフトは 200 で返す（行は Pending のまま残り、上限には数えられる）。
- 索引: `JstDate`（回数上限）、`(PeriodKey, ReportVersion)`（確定した版の案を引く）。

## 整合性・制約ルール

- PeriodKey ごとに 1 行。確定済みは不変（`UpsertDraft` で変更不可）。版番号（Version）で楽観排他しロストアップデートを防ぐ。
- 確定は利用者のみ（OwnerOnly・アクター必須）。生成AI・自動処理は確定できない。

## 約定・手動売買の取り込みの集計範囲（セッションの窓）

報告書の期間（JST の営業日）を、そのまま取引台帳の取引日として照会しない。取引台帳の取引日は**約定した市場の現地取引日**
（米国＝米国東部時間・東証＝JST）であり、米国のセッション（ET 日 D）は JST の D 22:30〜D+1 05:00（冬時間は 23:30〜06:00）にある。
16:00 JST の生成時点で ET 日 D はまだ始まっていないため、同じ日付で引くと米国の約定はどの日報にも載らない。

- **規則**: 報告書は「前の営業日の日報の生成境界（`DailyAt`・既定 16:00 JST）の後〜期間の最終営業日の日報の生成境界まで」に
  **大引け**（米国 16:00 ET・東証 15:30 JST）を迎えたセッションを集計する。生成の時刻（日報 16:00・週報 16:30・月報 17:00 JST）は変えない。
- **日報**: 東証は同じ日付のセッション（従来どおり）、米国は前営業日〜前日の ET 取引日（例: 火曜の日報は月曜〔ET〕、月曜の日報は
  金曜〜日曜〔ET〕＝金曜のセッション）。休場日（構成）の日報は作られず、その日に閉場したセッションは次の営業日の日報が数える。
- **週報・月報**: その期間の日報の窓の和に等しい。最終営業日（金曜・月末）の米国のセッションは生成時点でまだ閉じていないため、
  **次の週報・月報**に入る（どの週報・月報にもちょうど 1 回）。
- **照会の形**: 取引台帳への照会の契約（市場の現地取引日の `[from, to]`、REST・gRPC とも）は変えない。全市場の取引日の外包で
  1 回引き、受け取った後に市場ごとの範囲で絞る。手動売買の取り込みも同じ窓で絞る（§2 と §2-b・在庫の畳み込みが同じセッションを見る）。
- **作り直し**（`POST /reports/{periodKey}/regenerate`）も同じ規則で引く。**確定済みの報告書は書き換えない**——規則の変更前に確定した
  日報に載らなかった米国の約定は、その日報には載らないまま残る。未確定の下書きは作り直しで新しい規則の窓へ引き直せる。
- OpenD の稼働率の日次（米国東部時間の取引日で記録される）も、同じ窓の米国の取引日で引く。
- 判断根拠・LLM 利用実績など、監査台帳を JST の暦日で引く入力は本規則の対象外である（区間は従来どおり）。

## 永続化方針

| 集約 | 永続化 | 実装 issue | 備考 |
| --- | --- | --- | --- |
| TradingReport（`reports`） | PostgreSQL 1 行/PeriodKey＋Version（専有 DB `report_svc`） | #14（PR） | 版番号付き冪等確定（単一行 JSON ＋バージョン列の方式を踏襲） |

## 対象外（後続）

- 損益・費用・税の集計列（報告書テンプレートの集計。取引台帳 #63・前提条件 #19 を参照するコード集計）。LLM ドラフト・対話的確定・ナレッジベース保存（#18）。
- 無応答時の既定動作・階層（月報→週報→日報）参照の強制・取引判断の `IDailyPolicyProvider` 結線。

## 関連仕様

- 作業仕様書: 仕様書: 報告書サービス Slice A（確定管理・方針の実体）
- 実装ADR: 報告書サービスが確定管理と確定済み日報方針を所有し、確定はイベントで通知する／リスク管理設定は単一行 JSON ＋バージョン列で永続化し楽観的排他制御する（踏襲）
