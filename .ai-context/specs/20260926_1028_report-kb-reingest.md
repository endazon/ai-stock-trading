---
title: 確定済みの報告書を KB へ入れ直す所有者専用の操作（基盤の切替で消える写しの復旧・本文なしで入った写しの修復）（#1028）
type: spec
status: accepted
related_ids: [FR-08, FR-11, NFR, UC-03, ADR-0001, ADR-0003, IADR-0436, IADR-0069, IADR-0071, IADR-0093, IADR-0274, IADR-0240]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-08・受け入れ基準「確定した報告書の本文が RAG 検索でヒットする」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0001 (基盤の拡張・基盤無改修)
---

# 仕様書: 確定済みの報告書を KB へ入れ直す所有者専用の操作（#1028）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-08（確定報告書を KB へ保存し RAG で引ける。**本文が検索でヒットすること**が受け入れ基準）、FR-11（監査）
- ユースケース（UC）: UC-03（確定報告書の保存）
- 関連 ADR: ADR-0001（基盤無改修＝基盤に upsert が無ければ AST 側で設計する）、ADR-0003（所有者の操作）
- 関連 IADR: IADR-0436（本件）、IADR-0069 / IADR-0071 決定 3（KB 保存の境界・best-effort）、IADR-0093（MSP レルムの KB 専用クライアント）、
  IADR-0274（本文の送信・#565）、IADR-0240 決定 11（操作者の解決）
- 起点: #346 利用者判断 4（2026-09-26）。移行仕様書 §承認事項 4

## 背景

- 基盤（MSP）は切替で文書 DB・索引を破棄する（MSP#457 の裁定）。確定報告書の KB 上の写しも消える。正は AST の `reports`（本文つき）。
- KB へ送る経路は確定時の 1 回だけ（`ConfirmReport/Endpoint.cs`）。入れ直す手段が無い。#565 のように本文なしで入った写しも直せない。

## 基盤の文書 API で何ができるか（MSP の隣接クローン `src/knowledge/backend/Services/DocumentService` を読んだ結果・読み取りのみ）

| 口 | 認可 | AST の KB 用クライアント（`platform-operator` のみ）から |
| --- | --- | --- |
| `POST /documents` | admin / operator | 使える。**外部 ID・冪等キーは無い**（毎回新しい文書を作る） |
| `GET /documents` | 認証のみ | 使える。**全件を返す（属性での絞り込み・ページングなし）** |
| `GET /documents/{id}` | 認証のみ | 使える |
| `PUT /documents/{id}/body` | 所有者の動的束縛（`owner == preferred_username`）。拒否は 404 | **自分が作った文書**なら使える（作成時に `owner` を主体から入れ直すため） |
| `PUT /documents/{id}`（メタデータの全置換） | `AdminOnly` | **使えない**（403） |
| `DELETE /documents/{id}` | `AdminOnly` | **使えない**（403） |

- `DocumentDto` は `ContentFingerprint` を返さない。`HasBody` は本文なしで作った文書でも既定 `true` のまま。**本文の有無は `MarkdownUri` の有無で読む**
  （`CreateWithBody` / `SetMarkdownUri` だけが設定する）。
- 基盤に upsert・外部 ID での照会は**無い**。基盤は改修しない（ADR-0001）→ AST 側で「既存の文書を一覧から属性で探し、無ければ作る・本文が無ければ入れる」形にする。
  欠けている能力は MSP への issue の下書きとして報告に残す（本 PR では起票しない）。

## 決定する挙動

1. `POST /reports/knowledge-base/reingest`（**OwnerOnly**。既存の `owner` 群に登録）。
   要求: `{ all?: bool, fromPeriodKey?: string, toPeriodKey?: string, refreshExisting?: bool }`。
   - `all: true` と範囲の併用、どちらも無い要求は 400（**全件は明示させる**）。
   - 範囲は期間キーを期間へ直して読む: `daily-yyyy-MM-dd`＝その日、`weekly-yyyy-Www`＝ISO 週の月〜日、`monthly-yyyy-MM`＝その月。
     対象は `from` の期間の初日 ≦ `PeriodStart` ≦ `to` の期間の末日の**確定済み**報告書（種別を問わない）。片側だけも可。形の違い・逆順は 400。
2. 手順（同時に 1 本だけ。実行中の 2 本目は 409）:
   1. KB の文書一覧を 1 回だけ引く（`GET /documents`）。**引けなければ 1 件も送らない**（未構成 503・失敗 502・タイムアウト 502〔不明と書く〕）。
   2. 報告書ごとに（`PeriodStart` の昇順）:

      | 状況 | 結果（outcome） | KB への書き込み |
      | --- | --- | --- |
      | 本文が空 | `skippedEmptyBody`（#565 と同じ扱い） | しない |
      | 本文が 1 MB（UTF-8）超 | `skippedBodyTooLarge` | しない |
      | 一致する文書（`project=ai-stock-trading`・`periodKey`・`kind` の属性が一致）が無い | `created` | `POST /documents`（本文つき・確定時と同じ写像） |
      | 一致する文書があり本文が無い | `bodyAttached` | `PUT /documents/{id}/body` |
      | 一致する文書があり本文もある | `alreadyPresent`（`refreshExisting` なら `bodyRefreshed`） | しない（`refreshExisting` なら `PUT …/body`＝索引の作り直し） |
      | 書き込みが 4xx で拒否された | `failed`（理由: 状態コード・応答の抜粋） | — |
      | 書き込みがタイムアウト・5xx・送信後の切断 | `unknown`（結果が分からない。次の実行が一覧で見つければ重複しない） | — |

      一致が複数ある文書は、本文のあるもの → 更新の新しいものを採り、件数を `duplicatesInKb` に数える（AST の資格では消せない）。
   3. 実行結果を監査に残す（`ReportKnowledgeReingested` を発行 → 監査台帳）。誰が（トークンの主体: 名前 → `client:<azp>` → `unknown`）・範囲・件数・
      送らなかった／失敗／不明の内訳（期間キー・理由。200 件まで・超過数）・中止の理由。
3. 応答: 200（実行した。個別の失敗を含み得る）／503・502（中止＝1 件も書いていない）。本文は同じ形
   （`status`・`abortReason`・件数〔`sent` = created + bodyAttached + bodyRefreshed〕・`items[]`・`auditPublished`）。
4. 確定時の保存（`ConfirmReport/Endpoint.cs`）は変えない。
5. Discord・管理画面の窓口は作らない（入れ直しの窓口の既存パターンが無い）。API と文書だけ。

## 受け入れ基準 → テスト（T-10-1490〜T-10-1509 を予約）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | 基盤の切替の後（KB が空）、1 回で確定済みの報告書が本文つきで KB に入る（検索できる）。ドラフトは入れない | `ReportKnowledgeReingestServiceTests`（T-10-1493） |
| 2 | 2 回実行しても KB の件数が変わらない | 同（T-10-1494） |
| 3 | 本文が空・上限超は送らず件数と理由を返す | 同（T-10-1495） |
| 4 | 本文なしで入った写しに本文を入れる（新しい文書を作らない）。入れられなければ失敗として理由を返し、作らない | 同（T-10-1496） |
| 5 | 原則 A: 送った／送らなかった（空）／失敗（理由）／不明（タイムアウト）を分け、不明は次の実行で重複しない | 同（T-10-1497） |
| 6 | 一覧を引けなければ 1 件も書かず、中止の理由を監査に残す | 同（T-10-1498） |
| 7 | 範囲（期間キー）・全件・不正な要求 | `ReportKnowledgeReingestScopeTests`（T-10-1499） |
| 8 | KB 上の重複は本文のあるほうを採り件数を返す | `ReportKnowledgeReingestServiceTests`（T-10-1500） |
| 9 | `refreshExisting` は本文を入れ直すが文書を増やさない | 同（T-10-1501） |
| 10 | OwnerOnly（未認証 401・サービス主体 403） | `ReportKnowledgeReingestWiringTests`（T-10-1502） |
| 11 | 本番の組み立て: KB 未構成は 503 で監査が発行される／構成済みでは確定済みを入れ、監査に操作者と件数 | 同（T-10-1503） |
| 12 | 同時の 2 本目は 409 | 同（T-10-1504） |
| 13 | 監査の写像（要約・相関・操作者不明） | `AuditEntryFactoryTests`（T-10-1505） |
| 14 | 監査台帳への記録（Wolverine の発見・1 周の標本） | `AuditCycleCompletenessTests` の標本（T-10-1506） |
| 15 | イベントの wire 名・往復 | `EventMessageTypeNameTests`・`ReportKnowledgeReingestedContractTests`（T-10-1507） |
| 16 | 監査の内訳の上限（200 件・超過数） | `ReportKnowledgeReingestServiceTests`（T-10-1508） |
| 17 | 基盤の文書 API の写像（一覧・作成・本文の投入の成功／失敗／不明） | `HttpKnowledgeDocumentCatalogTests`（T-10-1490〜1492） |

T-10-1509 は予備（未使用なら欠番）。

## 母集合（規則 1〜6・9・10）

- **KB へ報告書を送る経路**: `git grep -n "ReportKnowledgeMapper\|IKnowledgeBaseWriter" -- backend`（テスト除外） →
  確定の口（`ConfirmReport/Endpoint.cs`）の 1 か所だけ。情報収集の `KnowledgeBaseWriterSink` は報告書ではない（除外）。
  `TradeDecisionService/.../RetrievalSourcePolicy.cs` は検索側の読み手で、写像を変えないので影響なし（除外）。
- **「入れ直す経路が無い」と書いている文書**（誤りの側から引く）: `git grep -n -e "入れ直す" -e "確定時の 1 回" -e "入れ直し" -e "再投入" -e "reingest"` →
  `docs/migration/20260903_cutover-and-retention.md` の 2 行（§現況の基盤の切替・§利用者が決めること 4）を追随させる。
  他の一致（`_error` キューの再投入・Pod の入れ直し・シードの再投入・realm の再投入）は別の意味（除外）。
  `.ai-context/specs/20260926_346_cutover-plan-decisions.md` の一致は凍結記録（書き換えない）。
- **イベントの一覧**: `docs/api/events-and-ports.md` のイベント表、`docs/data/audit-events.md` の記録の列挙に 1 行ずつ足す。
- **報告書の API の一覧**: `docs/data/reports.md` の API の箇条に足す。`docs/api/openapi.yaml` は paths が空の雛形（除外）。
- **運用の手順**: `docs/operations/operations.md`（障害対応表の近く）に切替の後の手順を足す。
  🔴 タグ辞書: 基盤の切替で文書 DB が消えるとタグ辞書も空になり得る。未登録タグは 400（#705）→ `failed` に理由が出る。
  手順は KB タグ辞書 Runbook の登録を先に行うと書く（Runbook 自体は変えない）。
- **新イベントを足すと追随が要る検査**: `git grep -n "StopLossMethodResolved" -- backend/Shared/*.Tests backend/Services/AuditService/Tests` →
  `event-schemas.baseline.json`（`UPDATE_EVENT_BASELINE=1` で再生成）・`EventMessageTypeNameTests`・`AuditCycleCompletenessTests` の標本・
  `AuditConsumerCoverageTests`（母集合はアセンブリから引く＝更新不要）。`deploy/helm/.../pipeline.json` は取引パイプラインだけ（除外）。
- **保全表（`RetentionScope`）**: 新しい表を作らない（除外。マイグレーションなし）。
- **並行作業との領域**: #1029 が触る `PolicyRevisionLedgers.cs`・`EfReportStore.cs`・通知の方針ハンドラには触らない
  （`IReportStore.List()` を読むだけで、ストアに口を足さない）。

## 残余リスク

- 同時実行の排他はプロセス内だけ（report-service は 1 レプリカの前提。複数レプリカでは同時の 2 本が重複を作り得る）。
- 基盤が「保存したが索引の起動に失敗した」文書は一覧からは本文ありに見える（`alreadyPresent`）。`refreshExisting` で入れ直せる。
- 基盤の一覧はページングが無く、全文書（収集した記事を含む）を 1 回で受け取る。30 秒で引けない規模になったら基盤の口が要る（MSP への下書き）。
- 所有者でない（`owner` を持たない古い文書を含む）本文なしの写しは AST の資格では直せない（`failed`・理由に管理者の削除を案内）。

## 検証（2026-09-26）

- `dotnet test`: ReportService 1376 件・AuditService 216 件・Shared.Contracts 487 件・Shared.KnowledgeBase 59 件がすべて緑（新規 45＋3＋3＋17 件を含む）。
  `dotnet format backend/backend.slnx --verify-no-changes` は差分なし。
- 変異注入 23 件（サービス 17・台帳のアダプタ 5・監査の要約 1）を 1 つずつ入れて対象の試験クラスを実行し、23 件とも赤になった
  （内訳はテスト仕様書の該当節）。
- 文書の検査: `check-trace-blocks`・`check-doc-links`・`check-test-traceability`（採番の最大 T-10-1508）・`gen-knowledge-graph --check` が OK。

## ［2026-09-26 追記 / PR #1038 の監査］是正

| 所見 | 是正 | 試験 |
| --- | --- | --- |
| 1（中〜高）旧い写しを見失い隣に作る: #665（2026-09-03）より前の保存は `project` を持たず、#665 より前に本文なしで入った写しもこの形（以降の手動確定の本文なしの写しは project を持つ） | 写しの判定に「`project` なし・表題が `確定報告書 {kind} {periodKey}`（`ReportKnowledgeMapper.TitleOf`。#169 から不変）と完全一致」を足した。写しがあれば作らない。本文の投入が 404（AST の所有ではない旧い写し）なら次の写しを試し、すべて 404 なら `Failed`（別の主体の所有・管理者の削除を案内） | T-10-1509・T-10-1496・T-10-1503 |
| 2（低）重複がどの報告書か分からない・本文なしの間の選び方 | 行に `matchedCopies`、監査に `DuplicatePeriodKeys`。本文なしの写しは project あり → 新しい順に試し、404 なら次へ（AST が所有する写しを採る）・404 以外は止まる | T-10-1500 |
| 3（低）`monthly-9999-12` が 500 | 年 9999 の期間キーは形の違いとして 400 | T-10-1499 |
| 4（低）イベントの注記に Cancelled が無い・打ち切りで `Targeted` が件数の合計と合わない | 注記を直し、`NotAttempted` を足した（要約は「途中で打ち切り（未試行 N 件）」） | T-10-1498・T-10-1505・T-10-1507 |
| 5（情報）不明の直後の再実行・プロセス内の排他 | 運用仕様書に「不明の後は 1 分待つ」と、排他は 1 プロセスの中だけ・更新の最中は実行しない、を書いた | —（文書） |
| 6 PR の衝突 | develop を merge commit で取り込み、frontmatter の配列は和集合、本文の節は両方を残した | — |

- 母集合（規則 10: この是正で誤りになる自分の記述）: `git grep -n "project=ai-stock-trading\`・\`periodKey\`・\`kind\`\|属性（\`project\`" -- docs .ai-context/adr` → IADR-0436 決定 2・索引行・`docs/data/reports.md`・`docs/operations/operations.md` を直した
  （索引行は原文を残して追記）。`docs/api/events-and-ports.md`・`docs/data/audit-events.md` はイベントの項目の追加に追随した。
- 変異注入（追加分 9 件）: 旧い形の一致を外す・所有しないとき作る・404 で次を試さない・project ありを先にしない・表題を見ない・行の写しの数を載せない・監査の未試行を 0・年 9999 の検査を外す・別のプロジェクトの値も写しとみなす —— 9 件とも赤。

［2026-09-26 追記 / PR #1038 の差分監査（GO）］生き残った変異 2 件を試験で殺した: 本文の投入が不明でも次の写しへ進む（T-10-1497 に写し 1 件・2 件の Theory を足した）・
入れ直しの 404 で段を跨いで本文なしの写しへ入れる（T-10-1501 に試験と、段を跨がない理由のコードの注記を足した）。運用仕様書・IADR-0436・コードの注記の
「本文なしの写しはすべて project なしの形」を、2026-09-03 以降の手動確定の本文なしの写しは project を持つ、へ直した。ID は追加していない（T-10-1509 まで使用済み）。
