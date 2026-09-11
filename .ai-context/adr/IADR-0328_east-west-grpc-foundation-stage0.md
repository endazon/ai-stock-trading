---
title: IADR-0328 east-west gRPC の土台（段 0）は基盤の実装ガイドへ逐語で揃え、h2c は専用ポート・s2s は既存のトークン供給を再利用する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-17
  - MSP:ADR-0029
  - MSP:ADR-0075
  - ADR-0001
  - IADR-0001
  - IADR-0013
  - IADR-0051
  - IADR-0058
  - IADR-0284
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0328: east-west gRPC の土台（段 0）は基盤の実装ガイドへ逐語で揃え、h2c は専用ポート・s2s は既存のトークン供給を再利用する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: **Accepted**
- 日付: 2026-09-11
- 決定者: Claude Code（実装）／ endazon

## 起点・関連

- 関連する計画書 ID: **`MSP/ADR-0029`**（同期通信の使い分け基準。🔴 本リポの裸の `ADR-0029` は資料再編であり別物）、
  **`MSP/ADR-0075`**（移行順序＝基盤先行の裁定）、`ADR-0001`（基盤再利用）、FR-17（全体前提条件の照会元）
- 関連する実装仕様書: [`.ai-context/specs/20260911_584_east-west-grpc-foundation.md`](../specs/20260911_584_east-west-grpc-foundation.md)
- 前提: [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md)（射程 22 本＋基盤待ち 4 本・段 0〜6 の切り方）、
  [IADR-0051](IADR-0051_service-to-service-auth.md)（s2s トークン）、[IADR-0013](IADR-0013_platform-foundation-testsupport-shim.md)（ランタイム基盤 shim）
- 関連 issue: #584（本件・`Refs`。閉じない）、#526（`.Client` 廃止の起点）

## コンテキストと課題

`MSP/ADR-0075` は「移行順序は**基盤先行**。MSP が proto の置き場・versioning 規約・h2c 用ポートの扱い・s2s トークンの
写し方の**現物**を作り、AST はそれに追随する」と裁定し、`MSP/ADR-0029` フォローアップ（実装ガイド化）の履行を
着手の**先行条件**（期限 2026-11-30）に置いた。IADR-0284 決定 3 はこれを受けて「今は実装に入らない」とし、
#584 を `blocked:env` に置いた。

**2026-09-11 の再測定で先行条件は履行済みと確認した**（実測は作業仕様書 §再検証）。

| 待ち先 | 実測（隣接クローン・読み取り専用） |
| --- | --- |
| 実装ガイド | `docs/api/east-west-grpc.md`（`status: completed` / `updated: 2026-09-10`）。§1 proto の置き場と所有・§2 versioning・§3 h2c ポート・§4 s2s トークン |
| `.proto` | 11 件（`Shared/*.Contracts/Protos/<unit>/<service>/v<N>/`） |
| `MapGrpcService` | 10 件（認可・LLM ゲートウェイ・通知・文書・グラフ・ダッシュボード・検索） |
| 共通部品 | `Platform.Shared.Infrastructure/Foundation/Grpc/`（h2c リスナ・s2s 付きチャネル） |

したがって決めるのは「**AST の土台を具体的にどう置くか**」だけである。射程・順序・一括移行の義務は
IADR-0284 決定 1・2 と `MSP/ADR-0075` が既に固定しており、本 ADR で緩めない。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A（採用）** | 基盤の現物（実装ガイド §1〜§4 と `Foundation/Grpc/`）へ**逐語で揃える**。命名だけ AST 側の慣行（`AddAiStockTrading*`）に合わせる | 追随の費用が最小。基盤が直した欠陥（h2c だけが全インタフェースへ開く等）を写し取れる |
| B | AST の事情（サービス数・Worker 構成）に合わせて独自に設計する | `IADR-0001`「規約は基盤に揃える」に反し、`MSP/ADR-0075` の「基盤先行 → AST 追随」の意味を失う |
| C | 段 0 を飛ばして段 1（Assumptions）と同時に入れる | 土台と最初の面が同じ PR に混ざり、退行時に「土台のせいか写像のせいか」を切り分けられない（IADR-0259 決定 9 の趣旨） |

## 決定

### 決定 1 — 基盤の `Foundation/Grpc/` を shim へ逐語で移植する（命名のみ AST 慣行）

`AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc` に 2 つ置く（IADR-0013 の shim ＝ AST のランタイム基盤）。

| 部品 | 役割 |
| --- | --- |
| `GrpcListenerExtensions.AddAiStockTradingGrpcListener()` | `Grpc:Port`（env `Grpc__Port`）へ `HttpProtocols.Http2` **だけ**の専用ポートを bind。未設定・空・0 は立てない。`AddGrpc()` は常に呼ぶ |
| `GrpcClientExtensions.CreateAiStockTradingChannel(address, tokenProvider)` | 平文 h2c チャネルに s2s の `CallCredentials`（`UnsafeUseInsecureChannelCallCredentials`）を付ける |

**独自の工夫を入れない。** 基盤の実装が持つ 2 つの 🔴（①Kestrel は `Listen*` を 1 つでも構成すると
ホスティング URL を捨てるため HTTP/1.1 側を再宣言する、②h2c の待受ホストは HTTP 側の意図に従う）は、
基盤が実測で直した欠陥であり、写さないと同じ事故を AST で繰り返す。

### 決定 2 — s2s のトークン基盤は新設せず `IServiceAccessTokenProvider`（IADR-0051）を再利用する

REST の `AddAiStockTradingServiceToken`（`ServiceTokenHandler`）と**同じ供給元・同じ縮退**にする。
トークンが得られないとき（`null`）は**メタデータを付けずに送る** → 提供側が `UNAUTHENTICATED` →
呼び出し元の既存 fail-safe。REST の「ヘッダ無し → 401 → 安全既定」と向きを揃える。

🔴 **利用者トークンはメタデータへ載せない**（基盤 §4 と同じ。載せると呼び出し先が「利用者が直接呼んだ」と
「サービスが利用者のために呼んだ」を区別できない ＝ confused deputy）。利用者の文脈が要る経路は
**本文で運ぶ**（REST の要求本文と同じ形。移行は本文を変えないトランスポートの差し替えになる）。

### 決定 3 — h2c は専用ポート（既定 8081）。helm は宣言したサービスにだけ描く

`services.<name>.grpcPort` を宣言したサービスにだけ `containerPort`（`name: grpc`）・Service ポート
（`name: grpc` / `appProtocol: grpc`）・env `Grpc__Port` を描画する。**宣言しないサービスの描画は 1 バイトも
変わらない**（実測: 既定描画・`values-local` 描画とも develop とバイト一致）。readiness は
**HTTP 側の `/health/ready`（8080）のまま** —— 1 プロセスが両ポートを起動時に bind するので、
8080 が ready なら h2c も bind 済みである（gRPC ヘルスプロトコルは入れない）。

有効化時にだけ評価される分岐なので、**`helm.yml` に ON にした派生の描画検査を足す**（IADR-0058 の核心＝
「既定描画だけでは、有効化した瞬間に壊れるテンプレートを捕まえられない」）。

### 決定 4 — proto の置き場と versioning は基盤の規約を採る（現物は段 1）

| 項目 | AST での規約 |
| --- | --- |
| 所有者 | **呼び出される側**のサービス（`MSP/ADR-0029`） |
| 置き場 | **ユニットの共有契約プロジェクト** `backend/Shared/AiStockTrading.Shared.Contracts/Protos/aistocktrading/<service>/v<N>/<name>.proto` |
| 生成 | `<Protobuf Include="Protos/**/*.proto" ProtoRoot="Protos" GrpcServices="Both" />`。**`*.Client` プロジェクトは作らない**（planning#180 の裁定と同じ） |
| 生成物 | `obj/` に落ち、**コミットしない** |
| package | `aistocktrading.<service>.v<N>`（小文字・パスと一致）／ `option csharp_namespace = "AiStockTrading.Shared.Contracts.Grpc.<Service>.V<N>";` |
| 互換 | フィールド番号は不変・削除は `reserved`・メジャー版は `v<N+1>` を並走させる（in-place で壊さない） |

🔴 **IADR-0284 決定 5 は「proto は提供側サービスの `Contracts/Proto/` へ置く」と書いていたが、同決定は
「基盤先行の裁定なら MSP の置き場へ揃える（本行は上書きされる）」と明記している。本 ADR がその上書きである。**

**proto 互換検査器（`buf breaking` 相当）は段 1 へ移す。** IADR-0284 決定 5 は段 0 に置いていたが、
検査対象が 0 件では「通った」が何も意味しない。基盤の `scripts/check-proto-contracts.js`（852 行）を写す
作業でもあり、**最初の proto と同じ PR で入れた方が誤りを実際に捕まえる**。

### 決定 5 — fail-safe の写像方針は文書で固定し、コードは段 1 まで置かない

| gRPC status | 呼び出し元の扱い |
| --- | --- |
| `Unavailable` / `DeadlineExceeded` / `Unauthenticated` / `PermissionDenied` / `Internal` | 各クライアントの**既存の安全側既定**（`null` / `[]` / 残枠 0 / `Normal` / last known good）。REST の 4xx/5xx・タイムアウトと同じ向き |
| `NotFound` | 既存の「未供給」 |
| deadline | `CallOptions.Deadline` へ（現行 `HttpClient.Timeout` 5〜10 秒を保つ） |

**共通のヘルパは書かない。** 安全側既定は経路ごとに違い（残枠 0 と `[]` と LKG は同じ型ではない）、
消費者が 0 の段で共通化すると「呼び出し元ごとの値で置く」という planning#180 の裁定に逆行する
（`*.Client` を廃した理由そのもの）。写像は段 1 以降、各 `Infrastructure/ExternalServices/` に置く。

## 理由

- **追随の費用が最小である。** 基盤が実測で直した欠陥（HTTP ポート消失・h2c の過剰な公開）を写し取れる。
  AST が独自に設計すると、同じ事故を独自に踏み直すことになる。
- **既存サービスを 1 バイトも変えない。** `Grpc:Port` 未設定・`grpcPort` 未宣言が既定であり、
  段 0 のマージで挙動が変わるサービスは無い（試験と helm 描画の双方で固定した）。
- **消費者 0 の抽象を書かない。** fail-safe ヘルパ・proto 互換検査器を段 1 へ送るのは、
  CLAUDE.md の「過剰な抽象化・起こり得ないケースへの防御的実装をしない」に沿う。

## 結果

- 良い影響: 段 1 以降は「proto を置く → 提供側に `MapGrpcService` → 消費側を差し替える」だけになる。
  h2c ポートと s2s の写し方で迷う余地が消えた。
- 悪い影響・トレードオフ: 段 0 の時点では **gRPC を実際に話す経路が 1 本も無い**（土台だけが先に入る）。
  IADR-0284 が「消費者 0 の 1 PR」と決めた形であり、退行の切り分けのために受け入れる。
- フォローアップ:
  1. **段 1（Configuration `Assumptions`）**を別 issue で起票する。proto 互換検査器・結合テスト
     （#584 併記の「呼び出し元ごとのタイムアウト・リトライが効くことの結合テスト」）は段 1 で満たす。
  2. **AST→MSP の LlmGateway 2 本は「基盤待ち」から外れた**（`platform/llmgateway/v1/completion.proto` が公開された）。
     段の追加を起票する。DocumentService `POST /documents`・RetrievalService `POST /search` は proto が
     まだ無く、基盤待ちのまま。
  3. #584 は `Refs`（閉じない）。段 6（REST 撤去）まで開けておく。

## ［2026-09-11 追記 / #746］フォローアップ 2 を履行した（LlmGateway の 2 本）

フォローアップ 2 の「段の追加を起票する」を #746 として起票し、実装した
（[IADR-0332](IADR-0332_llm-gateway-completion-grpc-transport.md)）。

- **決定 1〜5 は不動である。** 段 1′ は本 ADR の土台（`CreateAiStockTradingChannel` ＋
  `IServiceAccessTokenProvider`）をそのまま使い、`Foundation/Grpc/` に手を入れていない。
- 決定 4 の置き場規約（`Protos/<unit>/<service>/v<N>/`）を、**基盤が所有する proto の写し**にも当てはめた
  —— パスは所有者（platform / llmgateway / v1）に従い、`aistocktrading/` の下には置かない。
- 決定 5 の写像表のうち **`Unauthenticated` / `PermissionDenied` は「既存の安全側既定」で終わらせない**
  （記録の原因を分ける）。IADR-0332 決定 3 を参照。
- 決定 3 の「h2c は専用ポート」は**受け側**の話であり、本リポジトリはまだ gRPC を**提供**していない。
  段 1′ で使ったのは呼び出し側だけである（`grpcPort` を宣言したサービスは引き続き 0 件）。

## 関連

- Supersedes: なし（IADR-0284 決定 5 の「proto の置き場」1 行だけを、同決定の明示的な留保に従って上書きする）
- Superseded by: なし
