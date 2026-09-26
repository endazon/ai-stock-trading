---
title: IADR-0436 確定済みの報告書を KB へ入れ直す所有者専用の操作は、基盤に upsert が無いため、KB の一覧を先に引いて属性（project・periodKey・kind）で既存の写しを探し、無ければ作る・本文が無ければ本文を入れる形で冪等にする
type: impl-adr
status: Accepted
related_ids: [FR-08, FR-11, UC-03, ADR-0001, ADR-0003, IADR-0069, IADR-0071, IADR-0093, IADR-0274, IADR-0240, IADR-0316]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-08・受け入れ基準「確定した報告書の本文が RAG 検索でヒットする」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0001 (基盤の拡張・基盤無改修)
---

# IADR-0436: 確定済みの報告書を KB へ入れ直す（基盤に upsert が無い前提での冪等）

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（起票 [#1028](https://github.com/endazon/ai-stock-trading/issues/1028)。#346 の利用者判断 4〔2026-09-26〕の実装。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #1028（起点: #346 利用者判断 4・移行仕様書 §承認事項 4）
- 関連する実装仕様書: [20260926_1028_report-kb-reingest](../specs/20260926_1028_report-kb-reingest.md)
- 関連 IADR: [IADR-0069](IADR-0069_knowledge-base-rag-foundation.md)（KB 保存の境界）・IADR-0071 決定 3（確定時の保存は best-effort）・
  IADR-0093（MSP レルムの KB 専用クライアント）・IADR-0274（本文の送信・#565）・IADR-0240 決定 11（操作者の解決）・IADR-0316（ログ・理由の無害化）

## コンテキスト

基盤（MSP）は切替で文書 DB・索引を破棄する（MSP#457）。確定報告書の KB 上の写しは消えるが、正は AST の `reports`（本文つき）にある。
KB へ送る経路は確定時の 1 回だけで、入れ直す手段が無い。#565 の本文なしで入った写しも直せない。

基盤の文書 API（MSP の隣接クローンの `DocumentService` を読み取りのみで確認）:

- `POST /documents` は外部 ID・冪等キーを持たず、毎回新しい文書を作る。
- `GET /documents` は全件を返す（属性での絞り込み・ページングなし）。`DocumentDto` は本文の指紋を返さず、`HasBody` は本文なしで作った文書でも既定 `true`。
- `PUT /documents/{id}/body` は所有者の動的束縛（`owner == preferred_username`・拒否は 404）。作成時に `owner` を主体から入れ直すので、AST の KB 用クライアントが作った文書には書ける。
- `PUT /documents/{id}`（メタデータ）と `DELETE` は `AdminOnly` で、AST の KB 用クライアント（`platform-operator` のみ・microservices-platform の IADR-0075 が admin を却下）では使えない。

ADR-0001 により基盤は改修しない。

## 決定

1. **窓口は `POST /reports/knowledge-base/reingest`（OwnerOnly）だけ。** 範囲は期間キーの範囲（キーを期間へ直し、`from` の期間の初日 ≦ `PeriodStart` ≦ `to` の期間の末日・種別を問わない）か、
   明示の `all: true`（空の要求を全件と読まない）。確定済みの報告書だけ。同時に 1 本だけ（プロセス内のゲート・2 本目は 409）。
   Discord・管理画面の窓口は作らない（入れ直しの窓口の既存パターンが無い）。
2. **冪等は AST 側で作る。** KB の一覧を先に 1 回だけ引き、属性 `project=ai-stock-trading`・`periodKey`・`kind` の一致で既存の写しを探す
   （外部 ID の代わり。確定時の写像〔`ReportKnowledgeMapper`〕が既に付けている属性なので、過去に入った写しも見つかる）。
   無ければ本文つきで作る／本文が無ければ `PUT …/body`（文書は増えない＝#565 の修復）／本文があれば何もしない（`refreshExisting` のときだけ本文を入れ直す＝索引の作り直し）。
   一覧を引けなければ 1 件も書かない（既存の写しが見えないまま作ると重複する）。一致が複数なら本文のあるもの → 更新の新しいものを採り、件数を返す。
   基盤の口は保守用の新しいポート `IKnowledgeDocumentCatalog`（共有 KB クライアント）に置き、業務経路の保存ポート（`IKnowledgeBaseWriter`・結果を 1 値に潰す fail-safe）は変えない。
   宛先・資格は保存と同じ構成を読み、名前付きクライアントだけ分ける（タイムアウト 30 秒。全件の一覧と 1 MB までの本文を運ぶ）。
3. **原則 A: 結果を分ける。** 作成・本文の投入は 2xx＝成功／4xx＝失敗（理由に状態コードと応答の抜粋 200 字・無害化）／5xx・タイムアウト・送った後の切断・2xx なのに ID が無い＝**不明**／
   接続を張れなかった（名前解決・接続・TLS・プロキシ）＝失敗。不明は送信にも失敗にも数えない。不明のまま入っていた写しは次の実行が一覧で見つけるので重複しない。
   本文が空は送らない（`skippedEmptyBody`・#565 と同じ扱い）。本文が 1 MB 超は送らない（`skippedBodyTooLarge`。保存ポートのようにメタデータだけで作ると、検索できない写しが「在る」に見える）。
   所有者でない本文なしの写しは直せない（`failed`・管理者の削除を案内）。別の文書を作って重複させない。
4. **監査は新イベント `ReportKnowledgeReingested`**（報告書が発行 → 監査台帳。1 回の実行につき 1 件・中止も残す・400 / 409 は何もしていないので残さない）。
   操作者はトークンの主体（名前 → `client:<azp>` → `unknown`。代理の窓口を持たないので本文の名前は信じない）、範囲、結果ごとの件数、
   送らなかった／失敗／不明の内訳（期間キー・結果・理由・文書 ID。200 件まで・超過は件数）。行の型は `Operations` 名前空間（`EventTypeDiscovery` に数えない）。
   発行の失敗は実行を巻き戻さず、応答の `auditPublished=false` とエラーログで分かるようにする。
5. **応答**: 200＝実行した（個別の失敗・不明を含み得る）／503＝KB 未構成／502＝一覧を引けなかった（503・502 は 1 件も書いていない）／400＝範囲の不正／409＝実行中。
   本文は報告書ごとの行（`items[]`）と件数（`sent = created + bodyAttached + bodyRefreshed`）。

## 却下した案

| 案 | 却下の理由 |
| --- | --- |
| AST に KB の文書 ID を保存し、それで更新・置き換える | 基盤の切替で文書 DB が消えると ID は全部死ぬ（本件の主な用途で役に立たない）。置き換え（削除）は AST の資格では 403。タイムアウトで結果不明の作成は ID が分からず、次の実行で重複を作る。一覧で探す形はこの 3 つを全部吸収する |
| 新しい外部 ID 属性（`sourceKey` 等）を足す | 過去に入った写しは持っていないので結局 `periodKey`・`kind` で探すことになる。鍵が 2 つになる |
| 本文の投入を拒否されたら新しく作る | 本文なしの写しが残り重複する。#1028 の「重複を作らない」に反する。失敗として理由を返し、管理者の削除の後に入れ直せばよい |
| 基盤へ upsert・外部 ID の照会を足す | ADR-0001（基盤無改修）。必要性は MSP への issue の下書きとして残す（下記） |
| 確定時の保存（`ConfirmReport`）も新しいポートへ寄せる | 本件の範囲外。確定を壊さない best-effort の意味が変わる |

## 残余リスク

- 同時実行の排他はプロセス内だけ（report-service は 1 レプリカの前提。複数レプリカでは同時の 2 本が重複を作り得る）。
- 基盤の一覧は全文書（収集した記事を含む）を 1 回で返す。30 秒で引けない規模になったら基盤側の絞り込みの口が要る。
- 基盤が「保存したが索引の起動に失敗した」写しは一覧からは本文ありに見える（`alreadyPresent`）。`refreshExisting` で入れ直せる。
- 基盤の切替でタグ辞書が空になると、作成は未登録タグの 400 で `failed` になる（理由に基盤の応答が出る）。KB タグ辞書 Runbook の登録を先に行う。
- 所有者を持たない古い本文なしの写しは AST の資格では直せない。
- 実 KB（基盤）での疎通は未検証（試験は基盤の文書 API の模造と、名前付きクライアントの一次ハンドラの差し替えで行った）。

## 基盤（MSP）への要望の下書き（本 PR では起票しない）

- 文書の外部 ID（呼び出し側の自然キー）での照会と upsert（`PUT /documents/by-external-id/{key}` 等）。無いため、呼び出し側は全件の一覧を引いて属性で探している。
- `GET /documents` の属性での絞り込み（`?attr.project=…`）とページング。
- `DocumentDto` に本文の指紋（`ContentFingerprint`）を載せる（本文が最新かを呼び出し側が判定できない）。
- 機械クライアントが自分の作った文書を削除・メタデータ更新できる口（所有者の動的束縛。現状は `AdminOnly`）。
