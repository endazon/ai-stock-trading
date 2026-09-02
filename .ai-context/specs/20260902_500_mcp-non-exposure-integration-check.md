---
title: MCP 非公開の結合確認（基盤 MCP 再実装後）と基盤側許可リストのドリフト検出
type: spec
status: approved
related_ids: [ADR-0012, FR-08, FR-14, NFR, IADR-0171, IADR-0166, IADR-0273]
author: endazon (with Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0012_mcp-exposure-policy.md
---

# 仕様書: MCP 非公開の結合確認とドリフト検出（#500）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-08（ナレッジベース保存）／FR-14（Discord からの参照）／非機能要件（発注機能へのアクセスは本人のみ）
- ユースケース（UC）: なし（統制の確認であり利用者操作の追加は無い）
- 画面（SC）: なし
- 関連 ADR: **ADR-0012**（Accepted・2026-07-23）／基盤 ADR-0024（MCP サーバー統合・既定非公開＝許可リスト方式）／基盤 ADR-0034 決定 9（サービスアカウントの個人資料一律除外）
- 計画書リンク: `project-planning/projects/ai-stock-trading/07_adr/ADR-0012_mcp-exposure-policy.md`（隣接クローン・読み取り専用）
- 起点 issue: [#500](https://github.com/endazon/ai-stock-trading/issues/500)（`docs/blocked-tasks.md` A-10 の受け皿）／[#348](https://github.com/endazon/ai-stock-trading/issues/348)（構造ガード・クローズ済み）／[IADR-0171](../adr/IADR-0171_mcp-non-exposure-structural-guard.md) 決定5

## 目的・背景

[IADR-0171](../adr/IADR-0171_mcp-non-exposure-structural-guard.md) 決定5 は、#348 が求めた 3 点のうち**結合確認だけ**を
「基盤 MCP が存在しないと書けない」として `docs/blocked-tasks.md` A-10 へ登録した。待ち先は MSP#445（基盤 MCP の再実装）であった。

**待ち先は解消している。** 基盤 `microservices-platform` に `src/platform/backend/Services/McpServer/` が実装され、
宣言的公開構成 `Configuration/mcp-publication.json`（6 ツール）を持ち、ローカル k3s の
`microservices-platform` namespace で `mcp-service` が稼働している（実測 2026-09-02）。

本作業は次の 2 つを行う。

1. **結合確認（実測）** —— MCP のツール一覧・検索・文書取得のいずれの経路でも本ユニットの報告書・判断根拠・
   収集情報が返らないことを、クラスタ内の使い捨て Pod から実際に測る。
2. **ドリフト検出（恒久）** —— IADR-0171 §結果 が「悪い影響」として自ら記録した限界
   （**本リポジトリのテストは基盤側の許可リストを見ていない**）を、テストで埋める。

## 対象範囲

- 対象:
  - 実環境（rancher-desktop k3s）に対する**読み取り専用の実測**と、その結果の記録。
  - `McpExposureNotDeclaredTests.cs` への**基盤公開許可リストのドリフト検査**の追加。
  - `docs/blocked-tasks.md` A-10 の更新（「最後に測った時点」を含む）。
- 対象外:
  - 実環境への書き込み（MCP クライアント登録・文書投入・構成変更）。**一切行わない。**
  - 基盤リポジトリの変更。**読み取り専用の参照に留める。**
  - 後述「計画書との差異」で挙げる粒度の不整合の是正（本リポジトリでは直せない）。
  - 有人（interactive）主体でのトークン取得（後述「未決事項」＝人間依頼）。

## 実測結果（2026-09-02・ローカル k3s / rancher-desktop）

測定手段: `microservices-platform` namespace に `curlimages/curl` の使い捨て Pod を起動（終了後削除）。
同 namespace は `istio-injection=enabled` かつ `PeerAuthentication` が **STRICT** のため、サイドカーの入る
同 namespace からのみ到達できる（`ai-stock-trading` namespace からの素の Pod は接続不能＝実測 000）。

### 主体

| 主体 | 取得方法 | 結果 |
| --- | --- | --- |
| 匿名（トークン無し） | — | 取得不要 |
| サービスアカウント | realm `platform` の `ai-stock-trading-kb-writer` で client_credentials | 取得成功（`azp=ai-stock-trading-kb-writer` / `iss=.../realms/platform`） |
| 有人（interactive） | — | **取得できず**（後述「未決事項」） |

### 3 経路 × 主体

| # | 経路 | 匿名 | サービスアカウント | 有人 |
| --- | --- | --- | --- | --- |
| ① | `initialize`（`POST /mcp`） | **HTTP 401** | HTTP 200（プロトコル応答のみ） | 未測定 |
| ① | **ツール一覧** `tools/list` | **HTTP 401** | HTTP 200・**`{"tools":[]}`（0 件）** | 未測定 |
| ② | **検索** `tools/call retrieval.search_documents`（`日報` / `取引判断` / `AAPL` / `Hold`） | 401 | **`isError: true`「MCP クライアントが登録されていないか、無効化されています。」** | 未測定 |
| ③ | **文書一覧** `tools/call document.list_documents` | 401 | 同上（`isError: true`） | 未測定 |
| ③ | **文書取得** `tools/call document.get_document` | 401 | 同上（`isError: true`） | 未測定 |

**本ユニットの文書は 1 件も返らなかった。**

### 肯定形の対照（「読めるものは読める」）

否定形だけでは「MCP が壊れているから何も返らない」と区別が付かない。同じサービスアカウントの
トークンで、**MCP ではない経路**を測った。

| 対照 | 結果 |
| --- | --- |
| `GET /documents`（DocumentService の REST・FR-08 の保存先そのもの） | **HTTP 200・基盤側の文書が返る**（実測 8 件） |
| `POST /search`（RetrievalService の REST） | HTTP 200・`{"results":[],"totalHits":0}`（索引が空） |

**同じ主体・同じデータに対し、REST では読めるが MCP では 0 件である。** 経路の差である。

### なぜ 0 件なのか（統制の階層・実測で切り分けた）

| 段 | 何が止めているか | 実測 |
| --- | --- | --- |
| 0 | **匿名は入口で止まる** | `POST /mcp` は `RequireAuthorization()`。匿名は 401 |
| 1 | **MCP クライアント登録簿が空** | `mcp_svc` の `Clients` テーブル **0 行**。`McpSubjectResolver` は毎回登録簿を引き、未登録は主体を解決できない → `tools/list` は空、`tools/call` は拒否 |
| 2 | **公開許可リストに本ユニットが無い** | `mcp-publication.json` の 6 ツールはすべて `document-service` / `retrieval-service` / `graph-service`。本ユニットのサービスは 1 つも無い |
| 3 | **本ユニットは自己申告の収集先ですらない** | `Mcp:Services` の既定は document / retrieval / graph の 3 つのみ。`mcp-service` の Deployment に上書きの env は無い（実測） |
| 4 | **下流のツール実行口が未実装** | `POST /internal/mcp/list_documents` / `get_document` / `search_documents` はいずれも **404**（自己申告 `/internal/mcp-tools` は 200 で返る）。仮に段 1〜3 を越えても、ツール実行は下流 404 で失敗する |
| 5 | **そもそも本ユニットの文書が基盤 KB に無い** | `document_svc.Documents` は実測 7〜8 行で**すべて基盤側の検証用文書**。本ユニット由来は 0 件 |

### 段 5 の理由（別件の不具合・本作業の範囲外）

本ユニットの KB 保存は**現在すべて失敗している**。

- `information-collection-service` のログ: `KB 保存: 0/3 件を platform 文書管理へ登録（未保存は fail-safe 縮退）`（繰り返し）
- 原因: AST 側の `KnowledgeBase__Auth__Authority` が `http://keycloak:8080/realms/microservices-platform` を指すが、
  **その realm は存在しない**（実測: `realms/platform` → 200、`realms/ai-stock-trading` → 200、
  `realms/microservices-platform` → **404**）。
- したがって「本ユニットの文書が MCP から返らない」ことの**一部は、文書がそもそも存在しないため**である。
  **これは統制ではない。** 別件として記録する（本作業では直さない。実環境は読み取り専用）。

## 設計（ドリフト検出）

`backend/Tests/AiStockTrading.Architecture.Tests/McpExposureNotDeclaredTests.cs` に
`McpPublicationAllowlistDriftTests` を**同一ファイル内へ**追加する。

- **同一ファイルに置く理由**: `McpExposureNotDeclaredTests` の走査除外は「本テスト自身のファイル名」1 件だけであり、
  新しいファイルを作れば**除外が 2 件に増える**（IADR-0171 決定2 が「育ったぶんだけ検査は弱くなる」と警告した箇所）。
- **見る対象**: 基盤の `src/platform/backend/Services/McpServer/Configuration/mcp-publication.json`。
  基盤の `ToolCatalog.Refresh` は**この構成を起点に**自己申告を探すため、構成に無いツールは申告があっても公開されない。
- **位置の決め方**: 環境変数 `MSP_MCP_PUBLICATION_PATH` ＞ 隣接クローン（リポジトリルートの祖先を遡って
  `microservices-platform/...` を探す。git worktree でも見つかる） ＞ 見つからない。
- **見つからない場合**: `Assert.Skip`（理由を出力・runner 上は Skipped であり Passed ではない）。
  **明示（環境変数）したのに存在しない場合は失敗**させる。
- **照合**: 本ユニットのサービス名を `backend/Services` の実ツリーから導き（ケバブケース＋小文字）、
  リポジトリ名（`ai-stock-trading` / `aistocktrading`）を加えた語の**粗い部分一致**。IADR-0171 決定2 と同じ理由である。

詳細と棄却した案は [IADR-0273](../adr/IADR-0273_msp-mcp-publication-allowlist-drift-detection.md) に記録した。

## 受け入れ基準

- [x] MCP ツール一覧・検索・文書取得の**いずれの経路でも**本ユニットの報告書・判断根拠・収集情報が返らないことを実測した
- [x] 匿名・サービスアカウントの 2 主体で測った（有人は取得手段が無く未測定＝人間依頼として明示）
- [x] 否定形だけで終わらせず、**同じ主体が REST では文書を読める**という肯定形の対照を取った
- [x] 基盤側の許可リストの変更を本リポジトリ側が検出できる検査を追加した（ADR-0012 §結果 のフォローアップ）
- [x] 検査は「ファイルが無ければ黙って緑」にならない（skip は理由つき・照合器は実ファイル非依存で常に検査される）
- [x] `docs/blocked-tasks.md` A-10 を更新し「最後に測った時点」を入れた
- [x] 実環境へ書き込んでいない（使い捨て Pod のみ・終了後削除）

## テスト方針

| テスト | 何を固定するか |
| --- | --- |
| `基盤のMCP公開許可リストに本ユニットのサービスが載っていない` | 実データ検査。読めた場合は 0 件でないこと（＝読めていないのに緑を作らない）も併せて固定する |
| `照合器は本ユニットのサービスの公開を検出する`（`[Theory]` 5 件） | **照合器が空を返すよう壊れても実データ検査は緑のまま**であるため、検出の陽性を別に固定する |
| `照合器は基盤側の公開ツールを違反としない` | 「常に違反」向きの壊れ方を排除する。2026-09-02 実測の 6 ツールを埋め込む |
| `照合対象は実ツリーのサービスから導かれている` | 母集合が痩せていないこと（下限 10・実測 11） |
| `明示された公開許可リストが存在しなければ失敗する` | 「指定したのに読めていない」を skip に倒さない |

**変異テスト（実測）**: `MSP_MCP_PUBLICATION_PATH` に `report-service` を 1 件足した細工済み構成を与えると、
実データ検査だけが **FAIL**（他 8 件は緑）。検査が効いていることを確認した。

## 計画書との差異

- 差異: **あり。**

  ADR-0012 §結果 のフォローアップは「MCP 公開構成（許可リスト）に本ユニットの**文書コレクション・retrieval スコープ**が
  含まれないことを確認する」と書いている。しかし**基盤の公開許可リストにその粒度は存在しない** ——
  `mcp-publication.json` のエントリは `{ name, service }` の 2 項目だけであり、文書コレクションや
  retrieval スコープを指定する場所が無い。

  🔴 **帰結**: 公開済みの `document.list_documents` / `document.get_document` / `retrieval.search_documents` は
  **基盤の共有ナレッジベース全体**を対象とする。本ユニットが FR-08 で保存する報告書・判断根拠・収集情報は
  **同じ document-service に入る**（実測: AST の `KnowledgeBase__Documents__BaseUrl` は
  `http://document-service.microservices-platform:8080`）。したがって、

  - 段 1（クライアント登録簿が空）と段 4（下流のツール実行口が 404）が解消され、
  - 段 5（AST の KB 保存が通るようになる）が解消された時点で、

  **本ユニットの文書は `document.*` 経由で外部エージェントから到達可能になる。**
  「許可リストに本ユニットを載せない」だけでは ADR-0012 の決定は守れない。

  対応: 本リポジトリでは直せない（基盤の公開構成の粒度と、基盤 KB の ABAC 設計の問題である）。
  #500 のコメントに実測とともに記録し、**計画側 / 基盤側への環流を人間の判断に委ねる**（後述）。

  なお本作業で追加したドリフト検査が担保するのは「**本ユニット自身のツールが公開されていないこと**」に限る。
  この限界は IADR-0273 §結果 に残余リスクとして記録した。

## 未決事項

| # | 内容 | なぜ AI にできないか |
| --- | --- | --- |
| 1 | **有人（interactive）主体からの結合確認** | realm `platform` に `directAccessGrantsEnabled` のクライアントが 1 つも無く（realm export の実測）、ROPC でトークンを取れない。ブラウザでの認可コードフローが要る。なお `McpSubjectResolver` は**主体種別を見る前に登録簿で弾く**ため、登録簿が空である限り結果は主体種別に依らない（構造上の推論であり実測ではない） |
| 2 | **ADR-0012 の粒度と基盤実装の粒度の不整合**（上記「計画書との差異」） | 計画・基盤双方に跨る設計判断であり、環流先（planning / MSP）と対応方針は利用者の裁定事項 |
| 3 | **AST の KB 保存が realm 不整合で全件失敗している**（段 5） | 実環境の構成変更（読み取り専用の制約）と、正しい realm の裁定が要る |
