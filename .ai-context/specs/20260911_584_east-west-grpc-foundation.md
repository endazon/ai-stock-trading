---
title: east-west gRPC の段 0（土台）—— 基盤先行の先行条件が履行されたので h2c リスナと s2s 付きチャネルを入れる
type: spec
status: done
related_ids: [NFR, FR-17, IADR-0284, IADR-0051, IADR-0013, MSP:ADR-0029, MSP:ADR-0075]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 仕様書: east-west gRPC 段 0（土台）（#584）

## 起点

issue #584（`blocked:env`）。[IADR-0284](../adr/IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 3 と計画側裁定
`MSP/ADR-0075` により、AST の east-west gRPC 化は **基盤（MSP）が `MSP/ADR-0029` のフォローアップ（proto の置き場・
versioning 規約・h2c ポート・s2s トークンの写し方を実装ガイドへ落とすこと）を履行するまで着手しない**とされていた
（期限 2026-11-30）。#584 の comment が定めた再測定手順に従い、隣接クローン（読み取り専用）で履行を実測する。

## 再検証（2026-09-11・隣接クローン `../microservices-platform` の作業ツリー）

| 待ち先の条件（#584 の再測定手順） | 実測 | 判定 |
| --- | --- | --- |
| `MSP/ADR-0029` フォローアップ＝実装ガイド | `docs/api/east-west-grpc.md`（`type: api-spec` / `status: completed` / `created: 2026-09-05` / `updated: 2026-09-10`）。§1 proto の置き場と所有・§2 versioning・§3 h2c ポート・§4 s2s トークンの 4 点をすべて規約表で持つ | **履行済み** |
| `.proto` の現物 | 11 件（`platform/authz/v1` 2・`platform/llmgateway/v1` 2・`platform/notification/v1` 1・`knowledge/*` 6）。置き場はユニットの共有契約プロジェクト `Shared/*.Contracts/Protos/<unit>/<service>/v<N>/` | **あり** |
| `MapGrpcService` | 10 件（AuthorizationService 2・LlmGateway 2・NotificationService 1・DocumentService 3・GraphService 1・DashboardService 1・RetrievalService 1） | **あり** |
| 共通部品 | `Platform.Shared.Infrastructure/Foundation/Grpc/`（`GrpcListenerExtensions`＝h2c 専用ポート、`GrpcClientExtensions`＝s2s CallCredentials 付き平文チャネル、`ClientCredentialsServiceTokenProvider`） | **あり** |
| helm | `services.<name>.grpcPort` を宣言したサービスにだけ `containerPort` / Service ポート（`name: grpc` / `appProtocol: grpc`）/ env `Grpc__Port` を描画 | **あり** |

**結論: `blocked:env` の待ち先は解消した。** IADR-0284 決定 5 の段 0（土台）から着手する。

なお AST→MSP の 4 本（IADR-0284 決定 2 の「基盤待ち」）は**一部だけ**解けた —— LlmGateway の
`POST /complete` ×2 は `platform/llmgateway/v1/completion.proto`（`Complete` unary ＋ `CompleteStream`
server-streaming）が公開されたが、DocumentService `POST /documents`（書き込み）と RetrievalService
`POST /search` の proto は無い（`knowledge/document/v1` は read / tag_write / tag_dictionary、
`knowledge/retrieval/v1` は attribute_values のみ）。本仕様書の射程外（段の追加は別 issue）。

## 対象範囲

- 対象: **段 0（土台）のみ**。IADR-0284 決定 5 の表の 1 行目。**消費者 0**（既存の REST 経路は 1 バイトも変えない）。
  - CPM に gRPC パッケージのバージョンを宣言する（基盤リポと同一版）
  - ランタイム基盤 shim（IADR-0013）に **h2c 専用ポートのリスナ**と **s2s トークン付き平文チャネル**を足す
  - helm に `grpcPort` の描画点を足す（宣言したサービスにだけ描く＝既定描画はバイト等価）
  - 段 1 以降が写す規約（proto の置き場・versioning・fail-safe 写像）を実装 ADR へ固定する
- 対象外（記録だけ残す）:
  - **proto の現物・提供側 `MapGrpcService`・消費側の差し替え**（段 1 以降）
  - **proto 互換検査器**（IADR-0284 決定 5 は段 0 に置いていた）。**段 1 へ移す** —— 検査対象が 0 件では
    「通った」が何も意味せず、規約（配置・package・番号・`reserved`）は最初の proto と同じ PR で書いた方が
    誤りを実際に捕まえる。基盤の `scripts/check-proto-contracts.js`（852 行）を写す作業でもある
  - `Microsoft.Extensions.Http.Resilience` / `HybridCache` への置換（#584 併記。トランスポート変更と別 PR）
  - 結合テスト（#584 併記）。**段 1**（実際に gRPC を通す経路ができた時点）で満たす

## 設計

基盤の現物（`docs/api/east-west-grpc.md` §1〜§4 と `Platform.Shared.Infrastructure/Foundation/Grpc/`）へ**揃える**。
AST が先に決める余地は残さない（`IADR-0001` / `IADR-0259`「揃える先の現物を見て決める」）。

| 対象 | 変更 |
| --- | --- |
| `Directory.Packages.props` | `Grpc.AspNetCore` / `Grpc.Net.Client` / `Grpc.Tools` 2.83.0・`Google.Protobuf` 3.35.1（基盤 `src/Directory.Packages.props` と同版） |
| `…PlatformShim/Foundation/Grpc/GrpcListenerExtensions.cs`（新規） | `AddAiStockTradingGrpcListener()`。`Grpc:Port`（env `Grpc__Port`）に **`HttpProtocols.Http2` だけ**の専用ポートを bind。未設定・空・0 は立てない。**HTTP/1.1 側のポートを再宣言する**（Kestrel は `Listen*` を 1 つでも構成するとホスティング URL を捨てるため）。`AddGrpc()` は常に呼ぶ |
| `…PlatformShim/Foundation/Grpc/GrpcClientExtensions.cs`（新規） | `CreateServiceCallCredentials` / `CreateAiStockTradingChannel(address, tokenProvider)`。平文 h2c チャネルへ s2s の `CallCredentials`（`UnsafeUseInsecureChannelCallCredentials`）。トークン供給は**既存の `IServiceAccessTokenProvider`（IADR-0051）を再利用**する（新しいトークン基盤を作らない） |
| `…PlatformShim.csproj` | `Grpc.AspNetCore`（受け側）・`Grpc.Net.Client`（呼び出し側）を参照 |
| `deploy/helm/.../templates/{service,deployment}.yaml` | `$svc.grpcPort` を宣言したサービスにだけ `containerPort`（`name: grpc`）・Service ポート（`name: grpc` / `appProtocol: grpc`）・env `Grpc__Port` を描画。HTTP 側には `name: http` が付く |

**s2s のトークン欠落は例外にしない。** `IServiceAccessTokenProvider` は取得不能時に `null` を返す契約
（IADR-0051 決定 1）であり、`ServiceTokenHandler` は「ヘッダ無しで送る → 提供側 401 → 呼び出し側の fail-safe」
に倒す。gRPC 側も同じにする（メタデータを付けずに送る → `UNAUTHENTICATED` → 呼び出し元の安全側既定）。
**トランスポートを変えても縮退の向きを変えない**というのが段 1 以降の前提である。

**fail-safe 写像の共通方針**（段 1 以降が写す。本 PR ではコードを置かない —— 消費者が 0 の抽象は書かない）:
`Unavailable` / `DeadlineExceeded` / `Unauthenticated` / `PermissionDenied` / `Internal` → 各クライアントの
既存の安全側既定（`null` / `[]` / 残枠 0 / `Normal` / last known good）、`NotFound` → 既存の「未供給」、
deadline は `CallOptions.Deadline`（現行 `HttpClient.Timeout` 5〜10 秒を保つ）。詳細は IADR-0328。

## 走査した母集合（規則 2・9・10）

`git grep -ln -i "grpc"`（追跡下の全ファイル・`CHANGELOG.md` 除く）＝ 15 ファイル。内訳と扱い:

| ファイル | 扱い |
| --- | --- |
| `docs/blocked-tasks.md` B-4 の #584 行 | **追随**（「最後に測った時点 2026-09-03」「待ち先＝MSP の履行」が本 PR で偽になる。規則 10） |
| `.ai-context/adr/IADR-0284_*.md` | **日付つき追記**（決定 3「今は実装に入らない」の前提が消えた。決定 1・2・5 は不動） |
| `.ai-context/adr/README.md` | **追随**（IADR-0328 の行を追加・IADR-0284 の行へ追記を 1 文） |
| `.ai-context/specs/20260903_584_*.md` 2 件 | **書き換えない**（確定済みの凍結記録。当時の実測として正しい） |
| `.ai-context/adr/IADR-0259`(決定 9) / `IADR-0264`(結果 1) | 据え置き（「gRPC 化は別 issue」は本 PR で成立したままである） |
| `.ai-context/specs/20260803_353` / `20260829_w11s4b` / `20260910_724` | 据え置き（凍結記録・OTLP の話） |
| `docs/observability/observability.md`・`infra/README.md`・`infra/otel/otel-collector-config.yaml`・`ObservabilityExtensions.cs` | 据え置き（**OTLP の gRPC** であり east-west とは別物） |
| `docs/templates/api_spec_template.md` | 据え置き（雛形の例示） |

`git grep -n "Grpc__Port\|AddGrpc\|h2c\|\.proto"` ＝ 0 件（新規である）。

## 受け入れ基準

- [x] #584 の再測定手順（1〜3）を実測し、`blocked:env` の待ち先が解消したことを証跡つきで示す
- [x] `Grpc:Port` 未設定なら gRPC リスナを立てない（既存 11 サービスの起動時の振る舞いが変わらない）
- [x] `Grpc:Port` 設定時、HTTP/1.1 側のホスティング URL が**消えない**（readiness が落ちない）
- [x] 構成誤り（負数・非数・範囲外・https）は起動時に落ちる（黙って立てないことをしない）
- [x] 平文 h2c チャネルに s2s トークンが載る／トークン欠落時はメタデータを付けずに送る
- [x] helm: `grpcPort` 未宣言なら描画は追加前とバイト等価。宣言時のみ 3 点（containerPort・Service ポート・env）が出る
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` と文書系検査器が通る

## テスト方針

`AiStockTrading.TestSupport.PlatformShim.Tests` に対照つきで置く（xUnit v3 ＋ AwesomeAssertions）。

| 観点 | 正 | 負（対照） |
| --- | --- | --- |
| ポート解決 | `"8081"` → 8081 | 未設定・空・`"0"` → `null`（立てない）／`"-1"` `"70000"` `"abc"` → 例外 |
| HTTP アドレス解決 | `urls` → そのまま／`http_ports` → `http://*:<port>`／両方無し → Kestrel 既定 | `urls` が優先されること（`http_ports` を無視） |
| gRPC の待受ホスト | 単一ホストなら踏襲（`127.0.0.1` はループバックのまま） | ホストが割れたら `*`（広い側へ倒す） |
| リスナ登録 | `Grpc:Port` 有りで HTTP と h2c の 2 ポートを bind し、`AddGrpc()` が効いている | `Grpc:Port` 無しでも `AddGrpc()` は効く（`MapGrpcService` が落ちない） |
| scheme | `http://` は通る | `https://` は例外（メッシュ内の TLS はサイドカーが終端する） |
| s2s 資格情報 | トークンありでメタデータへ `Authorization: Bearer <token>` | `null`／空文字ならメタデータを追加しない |

**変異による確認**（証跡は PR 本文）: (a) `ResolveGrpcPort` の `port == 0 ? null : port` を `port` に変えると
「0 は立てない」の試験が落ちる、(b) `AddPlatformGrpcListener` 相当から HTTP アドレスの再宣言を外すと
「HTTP ポートが消えない」の試験が落ちる、(c) `CreateServiceCallCredentials` の null ガードを外すと
「トークン欠落でヘッダを付けない」の試験が落ちる。

## 計画書との差異

- 差異: あり（IADR-0284 決定 5 の段 0 のうち **proto 互換検査器を段 1 へ移す**）。理由は §対象範囲。
  射程・順序・一括移行の義務（決定 1・2 と `MSP/ADR-0075`）は変えない。

## 未決事項

- AST の proto の置き場は基盤の規約（ユニットの共有契約プロジェクト）へ揃えると
  `backend/Shared/AiStockTrading.Shared.Contracts/Protos/aistocktrading/<service>/v1/` になる。
  **IADR-0328 で決めるが、現物（最初の proto）は段 1 で置く。**
- AST→MSP の LlmGateway 2 本は proto が公開されたため「基盤待ち」から外れた。段の追加は別 issue で起票する。
