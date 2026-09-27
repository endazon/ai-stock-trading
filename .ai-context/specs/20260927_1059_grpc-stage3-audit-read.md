---
title: east-west gRPC の段 3 —— Audit の events/by-type（報告書の 6 つの供給元）を生成クライアントへ寄せ、REST と並走させる
type: spec
status: done
related_ids: [NFR, FR-06, FR-10, FR-11, FR-16, IADR-0199, IADR-0284, IADR-0328, IADR-0331, IADR-0352, IADR-0420, IADR-0427, IADR-0445, MSP:ADR-0029, MSP:ADR-0075]
author: endazon (with Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# 仕様書: east-west gRPC 段 3（Audit の読み取り）（#1059）

## 起点

- issue #1059（段 3 専用。受け皿は #753・`Refs`）。#753 の「着手時に 1 段 1 issue へ切る」に従って切り出した。
- 雛形は段 2（#997 / PR #1003 / [IADR-0427](../adr/IADR-0427_risk-read-grpc-stage2.md)）。射程と段の切り方は
  [IADR-0284](../adr/IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5。本 PR の判断は
  [IADR-0445](../adr/IADR-0445_audit-read-grpc-stage3.md)。
- 計画 ID: NFR（トランスポートの差し替え。振る舞いは変えない）。経路ごとの起点は FR-06（報告書）・FR-11（監査台帳）・
  FR-10（損切りの実行機構・為替の情報源）・FR-16（判断根拠）。

## 射程の確定（IADR-0284 決定 5 の逐語と、引き直した母集合）

決定 5 の段 3 行（逐語）:

> | 3 | Audit `events/by-type`（Report 4 本） | 1 PR |

### 母集合の引き方（規則 1〜6・9・10。`origin/develop` `a0dd438c`）

| 軸 | 引いたもの | 件数 |
| --- | --- | --- |
| 1（呼び出し先の側） | `MSYS_NO_PATHCONV=1 git grep -n -F '/audit/' -- backend`（AuditService 自身・`/Tests/` を除く） | ReportService の `Infrastructure/ExternalServices/Http*.cs` **6 ファイル**（各 1 箇所の要求パス `"/audit/events/by-type"`）＋ Program.cs のコメント 2 行。他サービス 0 |
| 2（構成の側） | `git grep -n 'Audit:BaseUrl\|Audit__'`（全ファイル・拡張子で絞らない） | ReportService/Program.cs の 6 工場。**helm values・values-local・compose には 0 件**（稼働中の PoC は Audit の読み取りを構成しておらず、6 つとも Unsupplied のまま） |
| 3（登録の側） | `git grep -n 'audit-ledger'` | ReportService/Program.cs の `AddHttpClient("audit-ledger")`（10 秒・**依存先の門と観測つき**）1 件と 6 工場 |
| 4（提供側の側） | `AuditQueryEndpoints.cs` と `Features/AuditEvents/*/Endpoint.cs` | 3 ルート: `GET /audit/events/{correlationId}`（OwnerOnly）・`GET /audit/events?limit`（OwnerOnly）・`GET /audit/events/by-type`（**OwnerOrService**） |
| 5（書き込みの側） | AuditService の `Map*` の POST/PUT/DELETE と、他サービスからの `/audit` への書き込み | **0 本**。書き込みは Wolverine（RabbitMQ）の購読（`Infrastructure/Steps/AuditEventHandlers.cs`）だけ |
| 6（フロント） | `git grep -n -i '/audit' -- frontend` | 0 件 |

### 段 3 で移すもの（決定 5 の 4 本 ＋ 表の後に追加された 2 本）

| 射程表の行 | 呼び出し元 | クライアント | 引く種別 | rpc |
| --- | --- | --- | --- | --- |
| 6 | Report | `HttpFxSourceStatusSource` | 為替の情報源 5 種 | `GetEventsByType` |
| 7 | Report | `HttpLlmUsageRecordSource` | LLM 費用・フォールバック・判断スキップ | 〃 |
| 8 | Report | `HttpBorrowFeeRecordSource` | 借株料の計上・未計上 | 〃 |
| 9 | Report | `HttpTradeRationaleSource` | 取引判断 | 〃 |
| （#823 で追加） | Report | `HttpStopLossMethodUsageSource` | 承認 | 〃 |
| （#1002 で追加） | Report | `HttpStopLossMethodResolutionSource` | 損切り手法の解決結果 | 〃 |

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| Audit への書き込み | REST の書き込み口が無い。Wolverine の購読は非同期であり `MSP/ADR-0029` の同期 east-west の境界基準の外 |
| `GET /audit/events/{correlationId}`・`GET /audit/events?limit`（OwnerOnly） | サービス間の呼び出し元が 0 本（軸 1・6）。east-west ではない |
| テスト（`*/Tests/**`）内の `/audit/…` | 母集合はプロダクションコード。既存テストは REST の試験として残す |
| helm の既定値・values-local・compose | **既定は REST**（段 1・段 2 と同じ。既定描画を変えない）。values.yaml にはコメントで有効化の手順だけを書く |
| `.ai-context/adr/*`・`.ai-context/specs/*` の既存記録 | 凍結記録（書き換えない）。IADR-0284 には日付つき追記を足す |

## 設計

詳細な判断と棄却案は IADR-0445。要点のみ。

1. **proto**: `backend/Shared/AiStockTrading.Shared.Grpc/Protos/aistocktrading/audit/v1/audit_events_read.proto`。
   package `aistocktrading.audit.v1`・`csharp_namespace = "AiStockTrading.Shared.Grpc.Audit.V1"`。service `AuditEventsRead` に
   rpc 1 本 `GetEventsByType`（`from`・`to` は往復書式の文字列、`types` は repeated）。応答は `LedgerRecord`（id・種別・本文。
   **すべて `optional`**）。message 名は送り手の型名（`AuditEntry`）にしない（IADR-0420 の判定を壊さない）。
2. **提供側**: AuditService に `AuditEventsReadGrpcService`（新規ファイル）。REST と**同じストア**（`IAuditEventStore.GetByTypesInPeriod`）・
   **同じ種別の解析**（REST のエンドポイントから `internal static` に切り出す。gRPC は repeated をカンマで連結して同じ解析へ渡す）・
   **同じ認可**（`OwnerOrService`）。期間の欠落・書式違い（REST の 400）・種別なし・逆順は `INVALID_ARGUMENT`。
   Program.cs は 2 行（`AddAiStockTradingGrpcListener` と `MapGrpcService`）・csproj は参照 1 行。REST 面の振る舞いは不変。
3. **消費側**: `Audit:Grpc`（宛先）を宣言したときだけ輸送 `AuditGrpcTransport` を singleton で登録し、6 つの工場が `GetService` で
   有無を見て `Grpc*` 実装を選ぶ（BaseUrl より優先）。宣言してあるのに使えない値は**起動時に落とす**。deadline の既定は
   `audit-ledger` の `HttpClient.Timeout`（10 秒）、試行回数の既定 1。
4. **解釈の単一化**: 各 REST アダプタの `Build`（種別ごとの本文の復元・壊れた 1 件の扱い）と引く種別を `internal static` へ
   切り出し、gRPC は `LedgerRecord` を REST と同じ `AuditLedgerEntry`（id・種別・本文）へ写してから同じ `Build` を呼ぶ。
5. **原則 A**: id・種別・本文のどれかが欠けた記録は既定値で作らず、**応答全体を未供給（null）**にする（REST では本文の欠落が
   例外経由で未供給、id の欠落は `Guid.Empty`・種別の欠落は「要求していない種別」として捨てられていた —— gRPC は一律に
   「契約の食い違い」として未供給へ倒す。IADR-0427 決定 3 の「REST が非 nullable で受けていた項目」と同じ扱い）。
6. **報告書の依存先の門と観測（#840）**: 段 2 の輸送が持つ呼び出しの規則（門・観測・deadline・再試行）を報告書の中で共有する
   （`ReportGrpcCalls`）。依存先の名前は REST と同じ `audit-ledger`。段 2 の輸送はこれへ委譲するだけで公開面は変えない。

## 受け入れ基準（#1059）

- [x] proto と生成クライアント・サーバ実装。AuditService に `MapGrpcService` 1 件。REST 面は不変
- [x] 同値: 本物の Program.cs で REST と gRPC が同じ記録を返し、入力の誤りも同じ扱い
- [x] 否定形: 資格情報なし `UNAUTHENTICATED`・ロール不足 `PERMISSION_DENIED`
- [x] ReportService の 6 つの供給元が `Audit:Grpc` の有無で切り替え、既定は REST。**本番の Program.cs から**解決して実際に呼ぶ
- [x] 原則 A: 欠けた記録で応答全体が未供給・取得失敗も未供給
- [x] 門と観測が gRPC でも働く
- [x] timeout / retry の陽性・陰性対照（実 Kestrel h2c・127.0.0.1）
- [x] 変異注入の実測
- [x] proto 互換検査器の baseline 更新と陰性対照
- [x] helm の既定描画と values-local の描画が不変（`helm template` の差分）
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` と文書系検査器が通る

## テスト方針（テスト ID は T-10-1670〜T-10-1677。develop の最大 T-10-1652 から間を空けた新しい区画）

| ID | 置き場 | 観点 |
| --- | --- | --- |
| T-10-1670 | AuditService.Tests | 同値: gRPC が REST と同じ記録（件数・順序・id・種別・本文）を返す。種別の空白・重複・カンマの扱いも同じ |
| T-10-1671 | 〃 | 否定形: 認可（未認証 `UNAUTHENTICATED`・ロール不足 `PERMISSION_DENIED`・利用者とサービスは読める）と入力の検証（REST の 400 ⇔ `INVALID_ARGUMENT`） |
| T-10-1672 | ReportService.Tests | 原則 A: id・種別・本文の欠落・読めない id で応答全体が未供給。空の応答は「事象なし」（未供給ではない） |
| T-10-1673 | 〃 | 契約: 送り手の本物の型（`AuditEntryFactory` の記録）→ 提供側の写し → 実 h2c → 6 つの供給元が同じ値で読む |
| T-10-1674 | 〃 | 配線: 本番の Program.cs で宣言あり → 6 つとも `Grpc*` で実際に呼ぶ、無し → 従来、不正 → 起動時に例外 |
| T-10-1675 | 〃（結合） | 実 Kestrel h2c で deadline・再試行（陽性・陰性） |
| T-10-1676 | 〃 | 門と観測: トークンが取れなければ送らず未供給、失敗の一過性／恒常の分類（依存先 `audit-ledger`） |
| T-10-1677 | 〃 | 期間と種別の運び方: JST の半開区間の往復書式・引く種別が REST と同じ |

## 計画書との差異

- 差異: なし（射程・順序・一括移行の義務は IADR-0284 決定 1・2 と `MSP/ADR-0075` のまま）。
  決定 5 の段 3 行の「Report 4 本」を 6 本と読むこと（表の後に追加された 2 本）は実装側の解釈であり、IADR-0445 決定 1 と
  IADR-0284 の日付つき追記に残す。

## 未決事項

- 稼働クラスタでの h2c 往復は未実測（段 1・段 2 と同じ。既定を変えないため）。そもそも稼働中の配備は `Audit__BaseUrl` も
  構成していない（軸 2）。
- 実効構成の自己申告（introspection）は REST の構成しか見ない（段 1・段 2 と同じ既存の欠落。段 6 までに別 issue）。

## 着手後に判明した制約（規則 10）

| 判明したこと | 実測 | 扱い |
| --- | --- | --- |
| 要求の種別のフィールドを `types` と名付けると、protoc の C# 生成が入れ子の型の置き場 `Types` と衝突させ、プロパティを `Types_` に改名する | 初回のビルドが `GetEventsByTypeRequest に 'Types' の定義が含まれておらず` で赤 | `event_types` に改名し、proto にコメントで残した（IADR-0445 決定 3） |
| 稼働中の配備（helm の values.yaml・values-local.yaml）は REST の宛先 `Audit__BaseUrl` も構成していない | 軸 2 の走査で 0 件 | 6 つの供給元は稼働中も未供給のまま。本 PR は既定を変えない方針なので構成を足さない（values.yaml にコメントで有効化の手順だけを書いた） |
| 段 2 の helm の変更は無かった（`RiskManagement__Grpc` も `grpcPort` も既定では置いていない） | `gh pr diff 1003` に `deploy/` が無い | 段 3 も同じく既定描画を変えない。`helm template` の既定・values-local の描画が変更前とバイト等価であることを確かめた（下記） |
| 報告書では段 2 の輸送が門・観測・deadline・再試行の規則を持っていた | `RiskManagementGrpcTransport.CallAsync` | `ReportGrpcCalls` へ切り出して 2 つの輸送で共有（段 2 の公開面・試験は不変）。IADR-0445 決定 5 |
| 解決結果の供給元だけ照会の窓が前後 1 日広い | `HttpStopLossMethodResolutionSource`（#1002） | 窓を各アダプタの `Window` として共有（T-10-1677 で REST の要求と突き合わせ、窓を取り違える変異で赤） |

## 検証の記録（2026-09-27）

- `dotnet build backend/backend.slnx`: 0 エラー（警告は既存の NotificationService.Tests の CS0108 1 件のみ）。
- `dotnet test`: AuditService.Tests 233 合格・ReportService.Tests 1438 合格・Architecture.Tests 188 合格（件数と他のプロジェクトは PR 本文）。
- `dotnet format backend/backend.slnx --verify-no-changes`: exit 0。
- `node scripts/check-proto-contracts.js`: `--update` 後 OK（4 ファイル）。陰性対照（`LedgerRecord.detail` の番号を 3 → 9）で
  `[breaking] 番号が変わった` の NG（書き戻し後 OK）。`--self-test` 42 件 OK。
- 変異注入 10 件（1 つずつ入れて実行し、コミット済みの版を取り出して書き戻した）はテスト仕様書の本節の表に載せた。
- helm: `helm template ast deploy/helm/ai-stock-trading`（既定）と `-f values-local.yaml` の描画は変更前後で sha256 が一致
  （差分 0 行）。陽性対照として `--set services.audit.grpcPort=8081` を与えると Service の `grpc` ポート・`containerPort: 8081`・
  env `Grpc__Port` だけが増える。稼働中のクラスタ・OpenD には触れていない（描画はローカルのみ）。
