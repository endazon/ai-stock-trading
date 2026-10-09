---
title: IADR-0526 承認待ちの報告書の写し（ドラフト）を露出の 3 属性を全部除外した組織文書として KB に 1 件だけ持ち、確定版を作った後に消す
type: impl-adr
status: Accepted
related_ids: [FR-08, FR-06, FR-09, UC-03, ADR-0001, ADR-0003, IADR-0436, IADR-0474, IADR-0240, IADR-0491, IADR-0431, IADR-0382, IADR-0069]
author: claude (Claude Code)
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-08)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-03 手順 3)
related_specs:
  - ../specs/20261010_1300_report-draft-knowledge-copy.md
---

# IADR-0526: 承認待ちの報告書の写し（ドラフト）を KB に閲覧専用で 1 件だけ持ち、確定で置き換える（#1300）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-10
- 決定者: 利用者裁定（2026-10-10・planning#784）。実装は Claude Code。

## 起点・関連

- 起票: [#1300](https://github.com/endazon/ai-stock-trading/issues/1300)。裁定: planning#784。基盤側: MSP#1886（`ccbc4a3b`。MSP の IADR-0529）
- 前提: IADR-0436（確定版の入れ直し・台帳のポート）・IADR-0474（目印 `coverage=market`）・IADR-0240 決定 4（`/report show` は本文を返さない）
- 仕様書: `.ai-context/specs/20261010_1300_report-draft-knowledge-copy.md`

## コンテキスト

承認待ちの報告書は、確定前に本文を読める経路が無い。Discord の提示通知は要約だけで、`/report show` は版と警告だけを返す（IADR-0240 決定 4）。
利用者は 2026-10-10 に裁定した: ドラフトの本文を MSP の知識ユニットの組織文書として保存し SC-03 で読めるようにする。
検索・RAG・グラフ・MCP の一覧・Wiki・取引判断の検索からは外す。確定したら確定版で置き換える。通知に閲覧リンクは足さない。

MSP#1886 で、露出の 3 属性（`search_exposure` / `graph_exposure` / `ai_input`）を 3 つとも `excluded` にした組織文書は、
索引・検索・RAG・グラフ・Wiki・MCP の一覧に載らなくなった。SC-03 の閲覧はオブジェクトストレージから直接読むので残る。

基盤の制約: 外部 ID での照会・upsert は無い。表題の変更は管理者だけ。属性の更新（PATCH metadata）は**全置換**。
機械クライアントは自分が owner の文書へ本文を入れられ（PUT body）、自分が owner の組織文書を消せる（DELETE）。

## 決定

### 決定 1: 写しの形

- 表題 `報告書ドラフト {kind} {periodKey}`。確定版の表題 `確定報告書 {kind} {periodKey}` と分ける（入れ直しの旧い写しの判定と MSP の写しの棚卸しが確定版の表題との完全一致を使う）。
- 属性は `periodKey`・`kind`・`reportState=draft`・露出 3 キー＝`excluded`（`ReportKnowledgeMapper.DraftAttributesOf` が唯一の組み立て点）。
  機密区分・owner・department・project は保存ポートと同じ規則で補完する（internal・`project=ai-stock-trading`）。
- `coverage=market` は付けない（取引判断の 2 本目の検索の目印。IADR-0474）。
- タグは確定版と同じ登録済みの語彙（`report`・種別）だけ。基盤のタグ辞書は未登録のタグを拒否する。
- 本文の先頭に「承認待ちの報告書（ドラフト・版 n）…確定すると確定版に置き換わる」を書く。本文が空（月報の初回・手の経路）なら方針を載せる。

### 決定 2: 承認待ちへ移すたびに 1 件を保つ（作成は初回だけ。以後は本文の差し替え）

- 呼ぶ経路は承認待ちへ移す 5 つすべて: 自動生成・月報の初回・作り直し・`/policy` の改訂・手の提示（遷移したときだけ）。差し戻しは呼ばない（本文は変わらない）。
- 文書 ID はプロセス内に覚える。覚えていなければ一覧から `project`・`periodKey`・`kind`・`reportState=draft` で探す。DB に列を足さない（一覧は承認待ちへ移すときと確定のときだけ引く）。
- 一覧を引けなければ作らない（既にある写しが見えないまま作ると 2 件になる）。作成の結果が不明なら覚えない（次の回が一覧で見つける）。
  写しが 2 件以上なら一番新しいものへ書き、残りを消す。
- **属性の更新（PATCH）は使わない。** 全置換のため、露出のキーを送り忘れると写しが黙って検索・RAG に出る。版の表示は本文が運ぶ。
  属性を変える必要が出たら、`DraftAttributesOf` から組み立てた全属性を送ること（呼び出しを 1 か所に集める）。

### 決定 3: 確定で、確定版を作れたら写しを消す

- 確定の処理（REST・gRPC 共通の `ConfirmReportEndpoint.HandleAsync`）が確定版を保存できた（`Saved=true`）ときだけ写しを消す。
- 確定版を作れなかったときは消さない。写しは索引されないので検索には出ず、確定した本文を SC-03 で読める写しが残る。入れ直しが確定版を作るときに消す（決定 4）。
- 台帳のポートに `DeleteAsync`（DELETE /documents/{id}）を足す。結果の分け方は作成・本文の投入と同じ（2xx＝成功・4xx＝失敗・5xx/タイムアウト＝不明）。404 は「既に無い」として扱う。

### 決定 4: 入れ直しは写しを確定版の写しに数えず、残った写しを消す

- `IsCopyOf` は `reportState=draft` か露出 3 キーが全部 `excluded` の文書を写しに数えない。数えると「本文つきの写しが在る」と読んで確定版を作らない。
- 確定版の写しが KB に在る（作った・在った・本文を入れた）ときだけ、同じ報告書の写しを消す。消した数を応答（`draftCopiesRemoved`）に載せる。監査の事象の契約は変えない。

### 決定 5: 機能の門と best-effort

- 構成 `ReportDraftKnowledge:Enabled`（既定 false。helm の既定も false）。無効なら一覧・作成・差し替え・削除のどれも送らない＝従来の挙動。
- 🔴 **MSP#1886（`ccbc4a3b` 以降）を配備してから有効にする。** それより前の基盤は Wiki 同期が露出を見ず、写しを Wiki.js へ載せる。
- ポートは例外を投げない（呼び出し元の取り消しだけは伝播する）。KB の失敗で報告書の生成・提示・確定を止めない。保存の後の段では取り消しを渡さない。

### 決定 6: 取引判断の KB 検索に防御の絞り込みを置く

- 3 本の検索の結果から、表題が `報告書ドラフト ` で始まる文書を落とす。基盤が索引しないので通常は来ないが、3 本目はフィルタなしで引くため、露出の門が働かない基盤への保険にする。

## 検討した選択肢

1. **組織文書＋露出 3 キー＝excluded、本文の差し替え（採用）** — 裁定と MSP#1886 の門にそのまま乗る。属性は作成のときの 1 回だけで、全置換の事故が起きない。
2. 版ごとに新しい文書を作り、古い版を消す — 削除の失敗で写しが増える。SC-03 の文書 ID が版ごとに変わる。
3. 属性 `version` を PATCH で進める — 全置換のため、露出キーの送り忘れで写しが検索に出る経路を作る。版は本文で足りる。
4. 写しの文書 ID を報告書の行（DB）に持つ — マイグレーションが要る。一覧からの探索で足り、覚えた ID は性能のためだけ。
5. 通知に閲覧リンクを足す — 裁定 4 が退けた。

## 結果

- 良い影響: 利用者は承認待ちの本文を確定前に SC-03 で読める。確定版は従来どおり検索・RAG の対象で、写しは確定で消える。
- 悪い影響 / トレードオフ: 承認待ちへ移すたび、覚えていなければ文書一覧（全件）を 1 回引く。確定のときも 1 回引く（有効時だけ）。
- 残余リスク:
  - MSP の写しの棚卸し（`AstStaleCopyRules.IsReport`）は kind・periodKey・project で報告書と判定するため、写しも報告書の写しに数える。確定の後の削除が失敗した期間は「同じ kind・periodKey が 2 件」に出る。棚卸しは削除しないので害は表示だけ。MSP 側の是正は別件。
  - 確定されないまま残る写しは消さない（期限による削除は置かない。planning#784 の「裁定を求める点」の実装の推奨）。
  - 写しの ID はプロセス内の記憶。複製が 2 つ以上あると同時に初回を作り得る。次の回の一覧で重複を 1 件に戻す。

## 試験

| 固定する振る舞い | 試験 |
| --- | --- |
| 写しの属性（露出 3 キー＝excluded・reportState・coverage なし）・表題が確定版と分かれる・本文の先頭 | `ReportDraftKnowledgeCopyTests` |
| 作成・差し替え・一覧からの探索・重複の整理・一覧失敗で作らない・不明の後・404 の後 | `ReportDraftKnowledgeCopyTests` |
| 確定で消す・確定版を作れなければ消さない・既定で何も送らない・例外を伝えない・手の提示 | `ReportDraftKnowledgeCopyTests` |
| 入れ直しが写しを数えず残りを消す・確定版を作れなければ消さない | `ReportDraftKnowledgeCopyTests` |
| 自動生成・作り直しの経路 | `ReportRegenerationServiceTests` |
| `/policy` の経路 | `ReportPolicyRevisionServiceTests` |
| 削除の HTTP の結果の分け方 | `HttpKnowledgeDocumentCatalogTests` |
| 取引判断の検索で写しを落とす | `KnowledgeBaseRetrievalContextProviderTests` |

## 関連

- Supersedes: なし
- Superseded by: なし
