---
title: 承認待ちの報告書（ドラフト）を知識ユニットへ閲覧専用で保存し、確定で置き換える（#1300）
type: spec
status: accepted
related_ids: [FR-08, FR-06, FR-09, UC-03, ADR-0001, ADR-0003, IADR-0526, IADR-0436, IADR-0474, IADR-0240, IADR-0491, IADR-0431, IADR-0382]
author: claude (Claude Code)
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-08)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-03 手順 3)
  - planning:projects/ai-stock-trading/04_workflows/03_reporting-cycle.md
---

# 承認待ちの報告書（ドラフト）を知識ユニットへ閲覧専用で保存し、確定で置き換える（#1300）

## 起点

- [#1300](https://github.com/endazon/ai-stock-trading/issues/1300)。planning#784 の利用者裁定（2026-10-10）:
  1. 承認待ちの報告書（ドラフト）の本文を MSP の知識ユニットの**組織文書**として保存し、SC-03 で閲覧できるようにする（機密区分 internal・ABAC）。
  2. ドラフトは検索・RAG・グラフ・MCP の一覧・Wiki・AST の取引判断の知識検索から外す。確定したら確定版で置き換える。
  3. Discord の提示通知に閲覧リンクは足さない。通知は今の要約のままとする。
- MSP 側は MSP#1886（`ccbc4a3b`。MSP の IADR-0529）で入った。露出の 3 属性（`search_exposure` / `graph_exposure` / `ai_input`）を 3 つとも `excluded` にした組織文書は、索引・検索・RAG・グラフ・Wiki・MCP の一覧に載らない。

## 計画の確認

- FR-08 は「確定した報告書…を保存」と書く。ドラフトの保存は裁定で足された段であり、計画への反映は planning#784 が扱う（本 PR では起票しない。同件の issue が既にある）。
- UC-03 手順 3・`04_workflows/03_reporting-cycle.md` の「ドラフト提示（要約＋閲覧リンク）」の閲覧リンクは、裁定 4 で「通知には付けない」に改まる（同じく planning#784）。
- ADR-0001（基盤無改修）: MSP 側の変更（Wiki の門と試験）は MSP#1886 が済ませた。本 PR は AST の側だけを変える。
- ADR-0003: 確定は利用者だけが行う。本 PR は確定の経路を変えない（写しの後始末を足すだけ）。

## 現況（origin/develop 09c1c42c）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 承認待ちへ移す経路は 5 つ: 自動生成・月報の初回・作り直し・`/policy` の改訂・手の提示（`POST /reports/{periodKey}/present`） | `git grep -n "ReviewAction.Present" -- backend/Services/ReportService ':!*Tests*'` |
| 2 | 差し戻しは状態だけを変える（本文・版は変わらない） | `RequestReportChanges/Endpoint.cs` |
| 3 | 確定は確定版を KB へ 1 回作る（best-effort） | `ConfirmReport/Endpoint.cs`・`ReportKnowledgeMapper.ToDocument` |
| 4 | 台帳のポートは一覧・作成・本文の投入だけ | `IKnowledgeDocumentCatalog` |
| 5 | 入れ直しは periodKey・kind・project（または確定版の表題）で写しを判定し、本文のある写しを優先する | `ReportKnowledgeReingestService.IsCopyOf` |
| 6 | 取引判断の KB 検索の 3 本目（補充）はフィルタなし | `KnowledgeBaseRetrievalContextProvider` |
| 7 | MSP の属性の更新（PATCH metadata）は全置換。表題の変更（PUT /documents/{id}）は管理者だけ | MSP `UpdateMetadata` / `Update` |

## やること

1. 承認待ちへ移すたびに、報告書 1 件につき写し（ドラフト）を KB に 1 件だけ持つ（`IReportDraftKnowledgeCopy`・`CatalogReportDraftKnowledgeCopy`）。
   - 表題 `報告書ドラフト {kind} {periodKey}`。属性は periodKey・kind・`reportState=draft`・project（補完）・露出 3 キー＝`excluded`。`coverage=market` は付けない。
   - 初回は作成、以後は本文の差し替え。文書 ID はプロセス内に覚え、無ければ一覧から属性で探す。一覧を引けなければ作らない。
   - **属性の更新（PATCH）は使わない。** 版は本文の先頭に書く。
2. 確定で、確定版を作れたら写しを消す（`IKnowledgeDocumentCatalog.DeleteAsync` を足す）。
3. 入れ直しは写しを確定版の写しに数えず、確定版が KB に在れば残った写しを消す。
4. 構成 `ReportDraftKnowledge:Enabled`（既定 false）。helm の既定も false。
5. KB の失敗で報告書の生成・提示・確定を止めない。
6. 取引判断の KB 検索に、写しの表題を落とす防御の絞り込みを足す。
7. IADR-0526・データ仕様書・通信仕様（ポート表）・運用仕様書。

## 母集合（規則 9・10）

### 承認待ちへ移す経路（写しを作る・差し替える呼び出しの置き場所）

走査: `git grep -n "ReviewAction.Present" -- backend/Services/ReportService ':!*Tests*'`（状態機械の行を除く）。

| 箇所 | 扱い |
| --- | --- |
| `ReportAutoGenerator.GenerateAsync`（自動生成） | **是正**（提示できたら写しへ。通知の前） |
| `ReportAppService.StartMonthlyBootstrapAsync`（月報の初回） | **是正**（本文が空なので写しには方針を載せる） |
| `ReportRegenerationService.RegenerateAsync`（作り直し） | **是正** |
| `ReportPolicyRevisionService.ReviseAsync`（`/policy`） | **是正**（提示の通知は出さないが承認待ちの版は変わる） |
| `PresentReport/Endpoint.cs`（手の提示） | **是正**（依頼の列挙に無かったが同じ遷移。遷移したときだけ。冪等な再提示は送らない） |
| `ReportReviewStateMachine`（状態機械） | **除外**（純関数。副作用を置かない） |
| `RequestReportChanges/Endpoint.cs`（差し戻し） | **据え置き**（本文は変わらない。写しはそのまま） |

### 台帳のポートの実装（`DeleteAsync` を足す先）

走査: `git grep -n ": IKnowledgeDocumentCatalog" -- backend`。

| 箇所 | 扱い |
| --- | --- |
| `HttpKnowledgeDocumentCatalog` | **是正**（DELETE /documents/{id}。結果の分け方は作成と同じ） |
| `NotConfiguredKnowledgeDocumentCatalog` | **是正**（NotConfigured） |
| 試験の `FakeKnowledgeCatalog` | **是正** |
| gRPC の台帳 | **該当なし**（台帳は HTTP だけ） |

### 「確定版の写し」の判定と確定版の表題に依る読み手

走査: `git grep -n "IsCopyOf\|TitleOf\|確定報告書 " -- backend docs`、MSP の `AstStaleCopyRules`。

| 箇所 | 扱い |
| --- | --- |
| `ReportKnowledgeReingestService.IsCopyOf` | **是正**（写しを数えない） |
| 取引判断の補充の検索（表題 `確定報告書 …` の文書を通す試験） | **是正**（写しの表題を落とす。確定版は通す） |
| MSP `AstStaleCopyRules.IsReport`（kind・periodKey・project で報告書と判定） | **据え置き（残余リスク）**。写しも「報告書の写し」に数えられ、確定の後の削除が失敗した期間は重複の組に出る。MSP の棚卸しは削除しない判定なので害は表示だけ。MSP 側の是正は別件 |
| `docs/data/reports.md`（入れ直しの写しの判定）・`docs/operations/operations.md`（入れ直しの手順） | **是正**（写しを数えないこと・消すことを書く） |
| `docs/api/events-and-ports.md`（ポート表） | **是正**（DeleteAsync・写しのポート） |
| `docs/migration/20260903_cutover-and-retention.md` | **据え置き**（確定版の入れ直しの経緯。写しは基盤の切替で消えても次の提示で作り直される） |

### 露出の 3 キーを組み立てる箇所（PATCH の全置換で落とさないため）

走査: `git grep -n "search_exposure\|graph_exposure\|ai_input" -- backend`。

| 箇所 | 扱い |
| --- | --- |
| `KnowledgeExposureAttributes`（共有の定数） | **新設** |
| `ReportKnowledgeMapper.DraftAttributesOf` | **新設**（唯一の組み立て点。作成のときだけ送る） |
| PATCH /documents/{id}/metadata の呼び出し | **無し**（使わない。試験で全作成の要求が 3 キーを持つことを固定） |

## 受け入れ基準

- [x] 写しの属性は露出の 3 キーを全部 `excluded` で持ち、`reportState=draft` を持ち、`coverage=market` を持たない。確定版の写像は従来どおり。
- [x] 写しの表題は確定版の表題と分かれ、取引判断の目印で見分けられる。
- [x] 初回は作り、次の版は同じ文書の本文を差し替える。再起動の後は一覧から探す。重複は 1 件にする。一覧を引けなければ作らない。作成の結果が不明なら次の回は一覧で見つける。
- [x] 自動生成・作り直し・`/policy`・月報の初回・手の提示で承認待ちにした版の本文が写しへ渡る。承認待ちにできなかった版・断った操作は渡さない。
- [x] 確定で、確定版を作れたら写しを消す。作れなければ消さない。
- [x] 構成なし（既定）では承認待ちにしても確定しても台帳へ何も送らない。
- [x] KB の例外・失敗は呼び出し元へ伝えない。
- [x] 入れ直しは写しを確定版の写しに数えず、確定版が在れば残った写しを消す。確定版を作れなければ消さない。
- [x] 取引判断の KB 検索は、どの検索に混じった写しも判断文脈へ渡さない。
- [x] `dotnet build`（警告 0）・ReportService / Shared.KnowledgeBase / TradeDecisionService / Architecture の試験・`dotnet format --verify-no-changes`・node 検査が通る。

## 範囲外

- Discord の通知（裁定 4: 閲覧リンクを足さない。要約は今のまま）。
- 期限による写しの削除（planning#784 の「裁定を求める点」。実装の推奨どおり置かない＝確定・新しい版で置き換えるまで残す）。
- MSP の `AstStaleCopyRules` の是正（上の残余リスク）。
- 計画書（FR-08・UC-03・業務フロー）の反映（planning#784）。

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet test`（ReportService.Tests 1840＋・Shared.KnowledgeBase.Tests・TradeDecisionService.Tests・Architecture.Tests）・`dotnet format backend/backend.slnx --verify-no-changes`。
- node 検査（`check-trace-blocks`・`gen-knowledge-graph --check`・`check-commit-messages`・`check-test-traceability`・`check-adr-index-sync`・`check-cross-repo-refs`・`check-plan-id-qualification`）。
- 変異 M1（`DraftAttributesOf` から `ai_input` を外す）→ `ReportDraftKnowledgeCopyTests` の 5 件が赤。M2（`IsCopyOf` から写しの除外を外す）→ 同 3 件が赤。いずれも戻して緑。
