---
title: /report の入力補完が読む一覧を、報告書サービスの会話キーだけの射影（GET /reports/period-keys）へ差し替え、#843 の残り（項目 1・項目 6 の再考）を片付ける
type: spec
status: accepted
related_ids: [FR-14, FR-07, FR-06, UC-03, UC-04, UC-05, IADR-0240, IADR-0418]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-14「Discord からの操作」/ FR-06・FR-07 報告書)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (認証・認可)
---

# 仕様書: 入力補完の軽い一覧（#843 の残り）

## 起点

- #843（#841 の監査で挙がった非ブロッキングの 6 件）、同 issue の 2026-09-23 のトリアージコメント。
- 前段の仕様書: `.ai-context/specs/20260925_843_autocomplete-audit-followups.md`（PR #975。凍結記録。本 PR では書き換えない）。
  同仕様書は項目 2〜6 を片付け、**項目 1 だけを「形（専用ルート／`fields=`／ページング）を決める根拠が揃わない」として
  見送り**、項目 6 の受容に「**項目 1 の実装時に再考する**」という条件を付けた。
- 決定の記録: IADR-0418（新規）と IADR-0240 決定 9 への日付つき追記。

## 🔴 6 件の現状の引き直し（`origin/develop` = `a0d600b7`（着手時。途中で `a1826edf` へ rebase）・`git rev-parse --is-shallow-repository` → `false`）

| # | 論点 | develop の現状 | 出典 | 本 PR |
| ---: | --- | --- | --- | --- |
| 1 | 一覧 API が本文つき全件を返す | **残**。`EfReportStore.List()` は `Select(r => ToReport(r))` で本文込みの全行。補完は `GET /reports` を読む | `ReportService/Infrastructure/Persistence/EfReportStore.cs`・`NotificationService/Infrastructure/ExternalServices/HttpReportReviewController.cs`（`GetAsync("/reports", …)`） | **片付ける**（下記 決定 1〜3） |
| 2 | 補完のタイムアウトが Discord の期限と噛み合わない | **済**（PR #975）。`ReportCommandHandler.SuggestionBudget` = 2.5 秒 | `git log --grep` → `09162415` | 対象外（済） |
| 3 | `periodStart` を文字列で受けている | **済**（PR #975）。`JsonElement?` で受け、解釈できない 1 件だけを末尾へ回す | 同上 | 対象外（済）。新しい射影も同じ受け口を通す |
| 4 | 台帳 A-7a に補完の実機確認が無い | **済**（PR #975）。`docs/blocked-tasks.md` A-7a の再測定手順 ④ | `grep -n "補完" docs/blocked-tasks.md` | 対象外（済）。④の文言（ログ文言・「一覧 API の遅延」）は本 PR 後も正しい（ログ文言は変えない） |
| 5 | `GuildIdOf` の 4 アーム | **済**（PR #975）。基底プロパティ 1 行へ畳んだ | `DiscordNetBotGateway.cs` | 対象外（済） |
| 6 | 候補に確定済みも含まれる | **受容済み**だが、再考の条件「項目 1 の実装時」が本 PR で成立する | `Domain/ReportPeriodSuggestions.cs`（`MaxChoices` のコメント） | **再考して受容を据え置き、再考の条件を更新する**（決定 4） |

### 前段が項目 1 を見送った理由の再検証（規則 10: 他人の判断を検証せず転記しない）

| 前段の理由 | 実測 | 判定 |
| --- | --- | --- |
| 「軽い射影の前例（`?fields=` やキー専用ルート）がどのサービスにも無い」 | `grep -n "MapGet\|MapPost" backend/Services/ReportService/Features/Reports/*/Endpoint.cs` → **`/reports` の直下に `/{periodKey}` と並ぶリテラルのルートが既に 3 本ある**（`GET /daily-policy`・`GET /monthly-bootstrap`・`POST /pnl-summary`）。`GetConfirmedDailyPolicy/Endpoint.cs` のコメントは「/{periodKey} より優先される（リテラル一致）」と明記している | **崩れる**——案 a（専用ルート）には同じ集約・同じ階層の前例がある。`?fields=` / ページングの前例が無いのは事実で、それは案 b・c を退ける理由になる |
| 「`keys` を会話キーとして取れなくなる（規約が無い）」 | 上の 3 本と同じ性質（`daily-policy` も会話キーとしては取れない）。会話キーは `daily-YYYY-MM-DD` / `weekly-YYYY-Www` / `monthly-YYYY-MM` で生成され（`ReportPeriod.ExpectedKey`）、`period-keys` は生成されない | 実害なし。既存と同型 |
| 「OpenAPI・通信仕様書に `/reports` が載っていない」 | 事実（`docs/api/openapi.yaml` に無し）。ただし報告書の照会・操作の一覧は `docs/data/reports.md` §照会・操作 にある | 新ルートは同節へ足す（仕様の置き場はある） |
| 「配備順（新しい通知サービスが古い報告書サービスへ当たったとき）を決める必要がある」 | IADR-0240 決定 11 に配備順を考慮した前例（旧版 Bot を 400 にしない）がある。古い報告書サービスでは `/reports/period-keys` は `/{periodKey}` に当たり 404 を返す | 決定 3（404 のときだけ従来の一覧へ退避）で閉じる。規則 11 の表で形を比べた |
| 「`limit` の既定値を決める根拠が要る」（案 c） | 絞り込み（前方一致 → 部分一致）は通知サービス側の `ReportPeriodSuggestions.Filter` が全キーに対して行う。**サーバ側で件数を切ると、打った文字に一致する古いキーが候補から消える** | ページングは入れない（決定 2）。1 行は会話キーと日付だけ（数十バイト）で、本文の転送が消えることが主な改善である |

→ 項目 1 は**実装の設計判断（IADR）で閉じられる**。計画書の変更・利用者裁定は要らない（報告書サービスと通知サービスの間の内部 API の形であり、計画の FR/UC/画面の振る舞いを変えない）。

## 規則 9〜11（着手前に引いた母集合）

### 規則 9: 誤りの側の文字列で全文書を走査

`git grep -n "射影もページング\|本文を含む全件\|843 項目1\|843 項目 1\|軽い一覧\|項目1 の実装時\|形（専用ルート\|GET /reports[^/]"`（`.ai-context/specs/` を除く）:

| ヒット | 扱い |
| --- | --- |
| `HttpReportReviewController.cs`（「一覧 API は射影もページングも持たず、本文を含む全件を返す」「#843 項目1 に残している」） | 是正（新ルートを読む形へ書き直す） |
| `ReportPeriodSuggestions.cs`（「再考の条件: 軽い一覧（#843 項目1）の実装時、または押し出しの報告」） | 是正（決定 4。条件の前半は本 PR で消化） |
| `IADR-0240` 決定 9 本文（「一覧 API（`GET /reports`・OwnerOnly）から採り」）と 2026-09-25 追記（「軽い一覧は見送り」） | **本文は書き換えず**、日付つき追記を足す（決定の記録の作法） |
| `.ai-context/adr/README.md` の IADR-0240 行（「一覧 API（`GET /reports`）」「軽い一覧は形が未決のため見送り」） | 行末へ追記を足す（先行する文言は消さない＝`check-adr-index-addendum-loss`） |
| `docs/data/reports.md` §照会・操作（`GET /reports`、`GET /reports/{periodKey}`、`GET /reports/daily-policy`） | 新ルートを足す |
| `.ai-context/specs/20260710_*`・`20260918_834_*`・`20260925_843_autocomplete-*` | **凍結記録のため書き換えない**（本仕様書が後継の記録） |

### 規則 10: この変更で新たに誤りになる自分の記述

- `HttpReportReviewController.ListPeriodKeysAsync` の既存テスト「一覧は_reports_へ_GET_し…」は要求先 `/reports` を表明している → 新ルートの表明へ改め、従来の `/reports` は退避の試験で表明する。
- `IReportStore` にメソッドを足すと、実装 2 つ（EF・インメモリ）とテストの委譲 fake 3 つ（`RejectingPresentStore` ×2・`ThrowingUpsertStore`）がコンパイルで追随を要する（`grep -rn ": IReportStore"` → 5 件）。
- A-7a ④の「前者なら一覧 API の遅延、後者なら owner クライアントの設定を疑う」は、ログ文言を変えないため引き続き正しい。
- 項目 6 のコメントの再考の条件（「軽い一覧の実装時」）は本 PR で成立して消化されるため、残すと**既に過ぎた条件**になる → 条件を書き直す。

### 規則 11: 窓（配備順の時間差）

新ルートの導入は「報告書サービスと通知サービスの配備が揃うまで」の窓を作る。**増える側**＝通知サービスだけが新しい（新ルートを呼ぶが古い報告書サービスには無い）、**減る側**＝報告書サービスだけが新しい（古い通知サービスは従来の `/reports` を呼ぶ——新ルートは足すだけで `/reports` は変えないため、この側は形に依らず無事）。加えて**報告書サービスが障害中**の場合を比べる（退避が負荷を倍にしないか）。

| 形 | P1: 古い報告書サービス（新ルートは 404） | P2: 新しい報告書サービス（正常） | P3: 新しい報告書サービスが 500 | 判定 |
| --- | --- | --- | --- | --- |
| A. 新ルートだけを呼ぶ（退避なし） | ✗ 窓のあいだ補完が黙って「候補なし」（変異で実測: 退避の分岐を消すと P1 の試験が落ちる） | ✓ | ✓ 候補なし・要求 1 回 | 不採用: 窓で補完が黙って死ぬ（#843 項目 3 と同じ「黙って死ぬ」形） |
| B. **404 のときだけ従来の `/reports` へ退避** | ✓ 従来どおりの候補（単体で実測） | ✓ 新ルートの 1 回だけ（単体で実測） | ✓ 候補なし・要求 1 回（単体で実測） | **採用** |
| C. 失敗なら何でも `/reports` へ退避 | ✓ | ✓ | ✗ 障害中の報告書サービスへ**より重い**全件照会を重ねる（変異で実測: 退避の条件を「非成功」へ広げると P3 の「要求 1 回」の表明が落ちる） | 不採用 |

B の残余: 退避は 2.5 秒の補完予算（`ReportCommandHandler.SuggestionBudget`）の内側で起きる（同じトークンを渡す）。P1 の窓では 2 往復になるが、1 回目は 404 の即答である。

## 決定

1. **報告書サービスに `GET /reports/period-keys` を足す**（OwnerOnly・`/reports` の一覧と同じ認可）。応答は
   `[{ "periodKey": string, "periodStart": "YYYY-MM-DD" }]`、並びは開始日の降順・同日は会話キーの降順。
   **本文・要約・状態は載せない**（IADR-0240 決定 4/5 と同じ射影）。
2. **ストアに `ListPeriodKeys()` を足し、EF 実装は SELECT で 2 列だけを射影する**（本文の列を読まない）。
   ページング・件数上限は入れない（絞り込みは通知サービス側で全キーに対して行うため。上表）。
3. **通知サービスのアダプタは新ルートを読み、404 のときだけ従来の `/reports` へ退避する**（規則 11 の形 B）。
   応答の受け口（`ReportListItem`・`periodStart` を `JsonElement?` で受ける）は両ルートで共用する。退避したことは
   Information ログで 1 行残す（配備の揃い漏れに気付けるように）。
4. **項目 6 は再考した上で受容を据え置く。** 新しい射影に状態を載せれば「レビュー待ちを先に出す」ことはできるが、
   ① 候補の並び（新しい順）を変えるのは補完の見え方の変更であり、押し出しは観測されていない、② 状態を載せるなら
   表現非依存の形（真偽値など）を別途決める必要がある。**再考の条件を「押し出しの報告、または報告書の件数が
   数百件に近づいたとき」へ書き直す**（前の条件「軽い一覧の実装時」は本 PR で消化）。
5. 記録: IADR-0418（新規。形の比較と配備順）、IADR-0240 決定 9 への日付つき追記、`.ai-context/adr/README.md` の索引、
   `docs/data/reports.md` §照会・操作。

## 影響範囲

| ファイル | 変更 |
| --- | --- |
| `ReportService/Features/Reports/ReportPeriodKeyItem.cs`（新規） | 射影の型 |
| `ReportService/Features/Reports/IReportStore.cs` / `ReportAppService.cs` | `ListPeriodKeys()` |
| `ReportService/Infrastructure/Persistence/EfReportStore.cs` / `InMemoryReportStore.cs` | 射影の実装 |
| `ReportService/Features/Reports/ListReportPeriodKeys/Endpoint.cs`（新規）/ `ReportEndpoints.cs` | ルートと登録 |
| `ReportService/Tests/...`（fake 3 つの委譲、エンドポイント・EF の試験） | 下記 |
| `NotificationService/Infrastructure/ExternalServices/HttpReportReviewController.cs` | 新ルート＋404 退避・コメント是正 |
| `NotificationService/Domain/ReportPeriodSuggestions.cs` | 項目 6 の再考の条件 |
| `NotificationService/Tests/.../HttpReportReviewControllerTests.cs` | 下記 |
| `docs/data/reports.md` | §照会・操作・trace ブロック |
| `NotificationService/Tests/.../OperationReadContractTests.cs` / `ReportService/Tests/.../ReadContractWireFormatTests.cs` | 送り手の型による契約テスト（#980 で入った同型の節に並べる。T-10-970・971） |
| `docs/tests/FR-10_risk-controls-tests.md` | T-10-970〜972（割り当て帯 T-10-970〜979 の中。`git grep` で origin/* 全ブランチに未使用を確認） |
| `.ai-context/adr/IADR-0418_*.md`（新規）/ `IADR-0240_*.md` / `README.md` | 記録 |

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | `GET /reports/period-keys` は会話キーと開始日だけを新しい順に返し、本文・要約・状態を含まない | 単体（WebApplicationFactory） |
| 2 | `GET /reports/period-keys` は OwnerOnly（未認証 401・サービスロール 403） | 単体（WebApplicationFactory） |
| 3 | リテラルのルートが `/{periodKey}` より優先される（既存の報告書 1 件照会を壊さない） | 単体（同上。1 件照会も併せて通す） |
| 4 | EF 実装の射影が別コンテキストからも読め、並びが開始日の降順・同日は会話キーの降順 | 単体（EF InMemory） |
| 5 | 補完のアダプタは `/reports/period-keys` を読み、候補を返す | 単体（HTTP fake） |
| 6 | 新ルートが 404 なら `/reports` へ退避して従来どおりの候補を返す（P1） | 単体（ルート別 fake） |
| 7 | 新ルートが 500 なら退避せず候補なし・要求 1 回（P3） | 単体（ルート別 fake） |
| 8 | 既存の補完・予算・認可の試験が通る | 既存単体 |
| 9 | 送り手の本物の型 `ReportPeriodKeyItem` を報告書の設定で直列化した応答から、通知が会話キーを読める（T-10-970） | 契約（通知） |
| 10 | 報告書の本物の Program.cs の本文が応答型の直列化と一致し、項目は 2 つだけ（T-10-971） | 結合（報告書） |
| 11 | 上の 2 つが送り手の改名・本文つき一覧への退行で赤になる（T-10-972） | 変異注入（手動実測） |

## 未検証・保留

- 実 Discord・実 PostgreSQL での疎通は未検証（A-7a の実機窓の ④ で補完の疎通を測る。手順は PR #975 で追加済み）。
- 項目 6 は受容を据え置き（決定 4）。本 PR で #843 の 6 件はすべて片付く（済 4・実装 1・受容 1）。
