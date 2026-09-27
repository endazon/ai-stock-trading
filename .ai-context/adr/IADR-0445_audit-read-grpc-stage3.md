---
title: IADR-0445 east-west gRPC 段 3 —— 監査台帳の種別 × 期間の読み取りは 1 rpc で REST と並走させ、窓・種別・記録の解釈と呼び出しの規則を REST と段 2 で共有する
type: impl-adr
status: Accepted
related_ids: [NFR, FR-06, FR-11, FR-10, FR-16, MSP:ADR-0029, MSP:ADR-0075, IADR-0199, IADR-0264, IADR-0284, IADR-0328, IADR-0331, IADR-0352, IADR-0420, IADR-0427]
author: endazon (with Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# IADR-0445: east-west gRPC 段 3（監査台帳の読み取り）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-27
- 決定者: Claude Code（実装）／ endazon（段の切り方は #753 の方針・段 2 の雛形）

## 起点・関連

- 関連する計画書 ID: **`MSP/ADR-0029`**（同期通信の使い分け基準。🔴 本リポの裸の `ADR-0029` は資料再編であり別物）、
  **`MSP/ADR-0075`**（移行順序＝基盤先行）。経路ごとの起点は FR-06（報告書）・FR-11（監査台帳）・FR-10・FR-16。
- 関連する実装仕様書: [`.ai-context/specs/20260927_1059_grpc-stage3-audit-read.md`](../specs/20260927_1059_grpc-stage3-audit-read.md)
- 前提: [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5（段の切り方）、
  [IADR-0328](IADR-0328_east-west-grpc-foundation-stage0.md)（土台）、
  [IADR-0331](IADR-0331_assumptions-grpc-transport-and-proto-contract-checks.md)（段 1）、
  [IADR-0427](IADR-0427_risk-read-grpc-stage2.md)（段 2 ＝本段の雛形）、
  [IADR-0199](IADR-0199_fx-status-supply-wiring.md)（監査台帳を期間の集計の権威源にする・半開区間・書き手と同じ直列化設定）、
  [IADR-0352](IADR-0352_report-defers-on-transient-dependency-failure.md)（報告書の依存先の門と観測）、
  [IADR-0420](IADR-0420_cross-service-read-contract-convention-and-guard.md)（送り手の型による契約テストの判定）
- 関連 issue: #1059（本件・段 3）、#753（段 2〜6 の受け皿・`Refs`）

## コンテキストと課題

段 2（Risk の読み取り・8 rpc・呼び出し元 3 サービス）の雛形を、監査台帳の読み取りへ広げる。段 2 と違うのは次の 4 点である。

1. **提供側のルートは 1 本、呼び出し元は報告書の 6 クラス**。6 クラスとも `GET /audit/events/by-type` を同じ形（期間・種別 → 記録の
   id・種別・本文）で呼び、違うのは**照会の窓**（解決結果だけ前後 1 日を含む）・**引く種別**・**記録の解釈**だけである。
2. **記録の本文はイベント全量の JSON（文字列）** であり、線上の型は 3 項目の文字列で足りる。段 2 のような列挙・金額の写しは無い。
3. **報告書には段 2 の輸送がすでに居る**。門・観測・deadline・再試行の規則（IADR-0427 決定 4・6）を 2 つ目の輸送に書き写すと、
   同じサービスの中で同じ規則が 2 つになる。
4. **Audit への書き込みは REST ではなく Wolverine の購読**であり、REST の OwnerOnly の 2 本にはサービス間の呼び出し元が無い。

## 検討した選択肢

| 論点 | 案 | 評価 |
| --- | --- | --- |
| rpc の形 | **A（採用）: `AuditEventsRead/GetEventsByType` 1 本（期間・種別 → 記録）** | REST の 1 ルートと 1 対 1。6 つの供給元が同じ rpc を使う |
| | B: 供給元ごとに rpc（`GetFxSourceEvents` 等） | 引く種別が提供側に移り、報告書の都合で提供側の契約が 6 本に増える。種別の追加が破壊的変更の面になる |
| 記録の欠落 | **A（採用）: 3 項目とも `optional`。どれかが欠けた記録は応答全体を未供給** | REST が非 nullable で受けていた項目（IADR-0427 決定 3 と同じ扱い） |
| | B: 欠けた記録だけ捨てる | 🔴 種別の欠けた記録を捨てると、1 件しか無い期間が「事象なし」に化ける（REST で実際にそうなる形） |
| 解釈の置き場 | **A（採用）: 各 REST アダプタの `Build`・`WantedTypes`・`Window` を `internal static` にし、gRPC は同じ受け皿 `AuditLedgerEntry` へ写してから呼ぶ** | 窓・種別・読み方が 1 箇所に残る（段 2 決定 5 と同じ） |
| | B: gRPC 実装に書き直す | 6 つの解釈が 2 つずつになり、片方だけ直る |
| 呼び出しの規則 | **A（採用）: 段 2 の輸送から `ReportGrpcCalls` へ切り出し、2 つの輸送が共有する** | 門・観測・deadline・再試行が報告書の中で 1 つ。段 2 の公開面は変えない |
| | B: 監査用の輸送に書き写す | 同じサービスの中で同じ規則が 2 つ（片方だけ直る） |
| | C: サービスを跨いで共通化 | `*.Client` を廃した裁定（IADR-0264 決定 1）に反する |

## 決定

### 決定 1 — 段 3 の射程は「決定 5 の 4 本 ＋ 表の後に追加された 2 本」。書き込みと OwnerOnly は入れない

IADR-0284 決定 5 の段 3 行（逐語）は「Audit `events/by-type`（Report 4 本）｜1 PR」である。着手時に母集合を引き直すと
（作業仕様書の軸 1〜6）、同じルートの呼び出し元は報告書に **6 クラス**ある —— 射程表の 4 本（為替の情報源・LLM 利用実績・
借株料・判断根拠）に、表の後に追加された `HttpStopLossMethodUsageSource`（#823。IADR-0284 の 2026-09-25 追記が段 3 と名指し）と
`HttpStopLossMethodResolutionSource`（#1002）である。後者は同じルート・同じクラス形であり、片方だけ REST に残すと 1 つの報告書の
中で輸送が割れるので段 3 に入れた。

- **書き込みは射程外**: Audit への書き込みは Wolverine（RabbitMQ）の購読であり、同期の east-west ではない（`MSP/ADR-0029` の境界基準の外）。
- **OwnerOnly の 2 本は射程外**: `GET /audit/events/{correlationId}`・`GET /audit/events?limit` にはサービス間の呼び出し元が無い。

### 決定 2 — 提供側は `AuditEventsRead` 1 つ。REST と同じストア・同じ種別の解析・同じ認可・同じ入力の検証

`AuditService/Features/AuditEvents/AuditEventsReadGrpcService.cs`。REST の by-type と**同じ** `IAuditEventStore.GetByTypesInPeriod` を呼び、
認可はクラス属性の `OwnerOrService`（REST の当該エンドポイントと同じ）。REST 面の振る舞いは変えていない。

- **種別の解析は REST から `GetAuditEventsByTypeEndpoint.ParseTypes` へ切り出して共有する。** gRPC の repeated は**カンマで連結して**
  同じ解析へ渡す —— 空・空白の要素を落とす・前後を削る・カンマを含む要素を割る、の 3 つが REST と同じになる（別々に書くと、
  カンマを含む要素の扱いだけが静かに違う。変異で実測）。
- 期間の欠落・書式違い（REST ではクエリの束縛失敗の 400）・種別なし・逆順・空区間は `INVALID_ARGUMENT`（REST の 400 と同じ文言を共有）。
- Program.cs は 2 行（`AddAiStockTradingGrpcListener` と `MapGrpcService`）。`Grpc:Port` 未設定なら h2c は立たない。

### 決定 3 — 線上は「報告書が読む 3 項目・すべて optional」。欠けた記録は応答全体を未供給にする

- proto: `aistocktrading/audit/v1/audit_events_read.proto`（package `aistocktrading.audit.v1`）。記録の message は `LedgerRecord`
  （id・種別・本文）。🔴 **送り手の型名（`AuditEntry`）にしない**（IADR-0420 決定 3 の ⑤ の判定を壊さない。IADR-0427 決定 3 と同じ理由）。
- 🔴 要求の種別のフィールド名を `types` にしない —— protoc の C# 生成は入れ子の型の置き場 `Types` と衝突させ、プロパティを `Types_` に
  改名する（実測でビルドが赤）。`event_types` とした。
- **提供側**は C# の null を**設定しない**（`AuditReadWireMapping`）。**受け手**（`AuditGrpcTransport.ToEntry`）は id の欠落・読めない id・
  種別の欠落／空・本文の欠落のどれかがあれば、**応答全体を未供給（null）**にする。REST では本文の欠落は例外経由で未供給、id の欠落は
  空の GUID、種別の欠落は「要求していない種別」として黙って捨てられ、1 件しか無い期間は「事象なし」に化けた。**空の応答は「事象なし」**
  であり未供給と区別する（報告書の描き方が違う）。本文が JSON として読めない 1 件は REST と同じ解釈（共有の `Build`）で扱う。

### 決定 4 — 窓・種別・記録の解釈は REST のアダプタと共有する

6 つの REST アダプタの `Build`（種別ごとの本文の復元・壊れた 1 件の扱い）・`WantedTypes`・`Window`（照会の窓）を `internal static` にし、
受け皿を共有の `AuditLedgerEntry`（以前は各アダプタが同じ形の private record を 1 つずつ持っていた）に揃えた。gRPC 実装
（`Grpc*Source`）は `AuditGrpcTransport.ReadAsync` に「窓・種別・`Build`」を渡すだけの薄い型である。REST の要求は本変更の前と
バイト等価（解決結果の `types=` も `string.Join(",", WantedTypes)` で同じ文字列）。

### 決定 5 — 呼び出し元の切替は段 2 と同じ規則。呼び出しの規則は段 2 と共有する

| 構成キー（報告書） | 既定 | 意味 |
| --- | --- | --- |
| `Audit:Grpc` | 未設定＝**REST** | gRPC の宛先。**宣言してあるのに使えない値（相対・`https`・scheme 無し）は起動時に落とす**。宣言があれば `Audit:BaseUrl` より優先 |
| `Audit:GrpcTimeoutSeconds` | 10 | **試行ごとの** deadline。REST の `audit-ledger` の `HttpClient.Timeout` と同値 |
| `Audit:GrpcMaxAttempts` | 1 | 試行回数。既定は再試行しない。再試行するのは `UNAVAILABLE` / `DEADLINE_EXCEEDED` だけ |

- 宣言があれば `AddAiStockTradingAuditGrpc` が輸送 `AuditGrpcTransport` を singleton で 1 つ登録し、6 つの工場が `GetService` で有無を見て
  `Grpc*` 実装を選ぶ。宣言が無ければ何も登録しない。**チャネルは輸送が所有し、`GrpcChannel` を DI へ裸で登録しない**（IADR-0427 決定 4）。
- **門・観測・deadline・再試行は `ReportGrpcCalls` に 1 つ**。段 2 の `RiskManagementGrpcTransport` はこれへ委譲するだけにした
  （公開面・構成キー・既定値・試験は不変。T-10-1058 / T-10-1059 は変更なしで緑）。依存先の名前は輸送ごとに REST と同じ
  （`risk-ledger` / `audit-ledger`）。宛先の解決と構成の読み方も同じ型に置いた。
- helm: 既定描画と values-local の描画は変えない（`services.audit.grpcPort` も `Audit__Grpc` も置かず、values.yaml に有効化の手順を
  コメントで書くだけ）。有効化は**提供側の `grpcPort` と呼び出し元の宛先を同じ変更で**揃える。

## 理由

- **並走の正が REST のまま動かない。** 既定の構成を 1 つも変えていない（helm の描画は変更前とバイト等価）。切り戻しは構成の削除。
- **静かに壊れる形を共有で止めた。** 窓・種別・解釈・呼び出しの規則のどれかを 2 箇所に書くと、片方だけが直り、試験は緑のまま結果だけが
  変わる。共有したので、段 3 で書き足した判定は 1 つもない（線上の写しと欠落の扱いを除く）。
- **契約の検査器（IADR-0420）の前提を壊さない**名前にした。

## 結果

- 良い影響: 段 4 以降も「proto を足す → 提供側に rpc → 報告書では `ReportGrpcCalls` に乗せる → 解釈は REST と共有」の反復になる。
- 悪い影響・トレードオフ:
  - REST のアダプタに `internal static` の窓・種別・解釈が増えた（6 クラス）。
  - 実配備での h2c 往復は未実測（段 1・段 2 と同じ）。稼働中の配備はそもそも `Audit__BaseUrl` も構成しておらず、6 つの供給元は未供給のまま。
  - 実効構成の自己申告（introspection）は REST の構成しか見ない（段 1・段 2 と同じ既存の欠落）。
- フォローアップ:
  1. 段 4（Report・MarketMonitor・CostControl）以降を #753 から切る。
  2. introspection の自己申告を輸送に追随させる（段 6 までに。IADR-0427 フォローアップ 3 と同じ）。

## 関連

- Supersedes: なし
- Superseded by: なし
