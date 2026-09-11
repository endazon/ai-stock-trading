---
title: east-west gRPC（サービス間の同期呼び出し）通信仕様書
type: api-spec
status: draft
created: 2026-09-11
updated: 2026-09-11
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-17, UC-06, NFR]
adrs: [ADR-0001, MSP:ADR-0029, MSP:ADR-0075]
iadrs: [IADR-0013, IADR-0046, IADR-0051, IADR-0063, IADR-0264, IADR-0284, IADR-0328, IADR-0331]
specs: [20260911_584_east-west-grpc-foundation, 20260911_745_configuration-assumptions-grpc]
issues: [#526, #584, #745]
-->

# 通信仕様書: east-west gRPC（サービス間の同期呼び出し）

> 計画は「サービス間の同期呼び出し（east-west）は gRPC + Protobuf、BFF から画面・外部公開 API
> （north-south）は REST」と定め、移行の順序を**基盤先行**とした。本書は本リポジトリ側の面を書く。
> **規約の正本は基盤（microservices-platform）の実装ガイド** `docs/api/east-west-grpc.md` であり、
> 本リポジトリはそれへ**逐語で揃える**（判断の記録は trace ブロックの実装 ADR にある）。

## 概要

- **プロトコル**: gRPC（HTTP/2）+ Protobuf 3。メッシュ内は **h2c（TLS 無し HTTP/2）** で、mTLS はサイドカーが終端する。
- **対象**: メッシュ内のサービスどうしの**同期**呼び出し。外部 SaaS・IdP・非同期イベントは対象外。
- **状態**: gRPC 面を持つのは **1 経路**（全体前提条件の照会）。**並走中の正は REST** であり、
  gRPC は構成で opt-in する。残りの経路の移行は段ごとに別 issue で展開する。
- **既定は REST**: 呼び出し元の構成 `Configuration:Grpc` が無ければ 1 バイトも変わらない。
  提供側も `Grpc:Port` が無ければ h2c リスナを立てない。**切り戻しは構成を外すだけ**（コードを変えない）。

## 1. proto の置き場と所有

| 項目 | 規約 |
| --- | --- |
| 所有者 | **呼び出される側**のサービス |
| 置き場 | 共有プロジェクト `backend/Shared/AiStockTrading.Shared.Grpc/` |
| パス | `Protos/<unit>/<service>/v<N>/<name>.proto`（`<unit>` は `aistocktrading`） |
| 生成 | `<Protobuf Include="Protos/**/*.proto" ProtoRoot="Protos" GrpcServices="Both" />`。**`*.Client` プロジェクトは作らない** |
| 生成物 | `obj/` に落ち、コミットしない |

🔴 **サービスプロジェクトに proto を置いてはならない。** 呼び出し元が生成クライアントを参照できなくなる。
🔴 **`<unit>` にハイフンは書けない**（proto の package は識別子である）。ディレクトリ名も package と一致させる。
🔴 **共有の契約プロジェクト（`AiStockTrading.Shared.Contracts`）へも置けない。** あちらは Domain から
到達してよい共有物であり、**外部ライブラリ依存ゼロ**をアーキテクチャ検査が強制している。名前空間も
`…Shared.Contracts.*` にしない（Domain の許可接頭辞であり、生成型を Domain から使えてしまう）。
判断の記録は trace ブロックの実装 ADR にある。

## 2. versioning

| 項目 | 規約 |
| --- | --- |
| package | `<unit>.<service>.v<N>`（小文字。パスと一致） |
| C# 名前空間 | `option csharp_namespace = "AiStockTrading.Shared.Grpc.<Service>.V<N>";` |
| フィールド番号 | **不変**。削除するときは番号と名前を `reserved` に残す |
| 非破壊 | field / message / rpc / enum 値の**追加** |
| 破壊的 | 番号・型・ラベル・名前の変更、削除、rpc の要求／応答型の変更、package・名前空間の変更 |
| メジャー版の上げ方 | **`v<N+1>` のディレクトリと package を並走させる**（in-place で壊さない） |

機械検査は `node scripts/check-proto-contracts.js` が行う（配置と名前の一致・番号の一意・`reserved` の
再利用禁止・baseline との後方互換）。**非破壊の追加でも baseline と差分がある限り赤**になり、`--update` で
差分を PR に載せる。破壊的変更は `scripts/proto-breaking-allowlist.json` の承認エントリで通す ——
ただし**削除時の `reserved` 不在と `reserved` の再利用は承認でも通らない**。

🔴 **proto の破壊的変更はコンパイルで止まらない。** 番号を付け替えても両側が再生成されるのでビルドは
緑のまま通り、**古いピアだけが黙って別のフィールドを読む**。だから機械検査を CI に置く。

## 3. h2c ポート

| 項目 | 規約 |
| --- | --- |
| リスナ | 構成 `Grpc:Port`（環境変数 `Grpc__Port`）で**専用ポート**（既定 8081）に HTTP/2 **だけ**を bind。未設定・0 なら立てない |
| HTTP/1.1 | 8080（REST・`/health/*`・introspection）は**そのまま残す**。共通ヘルパが HTTP 側のポートを再宣言する |
| helm | `services.<name>.grpcPort` を宣言したサービスにだけ描画する。**宣言しないサービスは 1 バイトも変わらない** |
| readiness | **HTTP の `/health/ready`（8080）のまま**。1 プロセスが両ポートを起動時に bind する |

## 4. サービス間トークン（s2s）

| 項目 | 規約 |
| --- | --- |
| メタデータ | `authorization: Bearer <呼び出し側サービス自身の JWT>` |
| トークンの出所 | realm の confidential client の client credentials（`ServiceAuth:ClientId` / `ClientSecret`） |
| 呼び出し先の検証 | 既存の JwtBearer と同じ。読み取りの面には利用者またはサービスのロールを要求する |
| 拒否 | トークン無し → `UNAUTHENTICATED`、ロール不足 → `PERMISSION_DENIED` |
| 🔴 利用者トークン | **メタデータへ載せない**（載せると呼び出し先が「利用者が直接呼んだ」と「サービスが利用者のために呼んだ」を区別できない） |
| 利用者の文脈 | 必要な経路では**本文で運ぶ**（現在の 1 経路は利用者の文脈を取らない） |
| トークン取得失敗 | **メタデータを付けずに送る** → `UNAUTHENTICATED` → 呼び出し元の既存 fail-safe（REST の「ヘッダ無し → 401 → 安全既定」と同じ向き） |

タイムアウト・リトライ・キャッシュ・fail-safe は**呼び出し元**の `Infrastructure` に置く。

## 5. 面: 全体前提条件の照会（`aistocktrading.configuration.v1.Assumptions/Get`）

- 概要: 費用統制・取引判断の 2 サービスが、構成 `Configuration:Grpc`（例 `http://configuration-service:8081`）が
  あるときだけ gRPC で照会し、無ければ REST `GET /assumptions` で照会する。**並走中の正は REST。**
- 認証・認可: 読み取りの面と同じ（利用者またはサービス）。
- 評価器: REST と**同じ**サービス実装（`AssumptionsService.GetCurrent()`）を呼ぶ（評価器を 2 つにしない）。

| rpc | 形 | 対応する REST |
| --- | --- | --- |
| `Assumptions/Get` | unary | `GET /assumptions` |

リクエスト（`GetAssumptionsRequest`）: フィールドなし（REST と同じく引数を取らない）。

レスポンス（`GetAssumptionsResponse`）:

| 名前 | 型 | 説明 |
| --- | --- | --- |
| `assumptions` | TradingAssumptions | 税率・手数料体系・為替スプレッド・最小期待利益倍率・月次費用上限 |
| `version` | int32 | 現在の版。**実在する版は 1 から**。0 は未解決の番兵であり提供側は返さない |

エラー:

| gRPC status | 条件 | 呼び出し側の対応 |
| --- | --- | --- |
| `UNAUTHENTICATED` | サービストークン無し・検証失敗 | 安全側既定へ縮退（最後に取得した版 ＞ 既定値） |
| `PERMISSION_DENIED` | ロール不足 | 同上。**再試行しない** |
| `UNAVAILABLE` | 提供側へ届かない | 同上。**再試行の対象** |
| `DEADLINE_EXCEEDED` | 試行ごとの deadline 超過 | 同上。**再試行の対象** |

### 🔴 proto3 の「未指定」と 10 進数の写し

**proto3 に `decimal` も `null` も無い。** REST の DTO が持つ意味を、呼び出される側と呼び出す側の**両方で
明示的に写す**。写し漏れは例外にならず、意味が静かに変わる形で現れる。

| 契約 | REST | 線上 | 写し |
| --- | --- | --- | --- |
| 税率・手数料率・金額 | `decimal`（JSON 数値・桁を保つ） | `string` | 🔴 **不変文化の 10 進表記**。`double` にすると `0.20315` が 2 進浮動小数へ丸められ、**同じ版なのに REST と gRPC で採算判定・費用上限判定の結果が変わり得る** |
| 未指定の金額 | 項目が無い = 0 | `""` | `"" → 0` |
| 未解決の版 | `Version = 0` | `0` | 提供側は 0 を返さない。呼び出し元は 0 を「取得不可」として扱う |

### 呼び出し元の設定（呼び出し元ごとに置く）

| 構成キー | 既定 | 意味 |
| --- | --- | --- |
| `Configuration:Grpc` | 未設定（＝REST） | gRPC の宛先。**宣言してあるのに使えない値（相対・`https`・非 URL）は起動時に落とす** |
| `Configuration:GrpcTimeoutSeconds` | 5 | **試行ごとの** deadline。REST 実装の `HttpClient.Timeout` と同値 |
| `Configuration:GrpcMaxAttempts` | 1 | 試行回数。**既定は再試行しない**（REST と同じ振る舞い） |
| `Configuration:AssumptionsCacheTtlSeconds` | 300 | キャッシュ TTL（トランスポートに依らず共通） |

🔴 **再試行するのは `UNAVAILABLE` / `DEADLINE_EXCEEDED` だけ**である。権限や不在は待っても変わらず、
聞き直すぶんだけ**安全側既定へ倒れるまでの時間が延びる**（同期クリティカルパスである）。

🔴 **不正な宛先を黙って REST へ戻さない**のは、`Configuration:BaseUrl` の不正値が既定プロバイダへ倒れる
（凍結済みの安全既定）のとは**わざと非対称**にしている —— 新しい鍵で黙って戻ると「切り替えたつもりで
切り替わっていない」が綴り誤りと区別できないためである。

## シーケンス

```mermaid
sequenceDiagram
  participant C as 費用統制 / 取引判断（呼び出し側）
  participant K as Keycloak（realm）
  participant E as Envoy（サイドカー）
  participant S as 設定管理サービス（h2c :8081）
  C->>K: client_credentials
  K-->>C: サービス JWT（realm ロール付き）
  C->>E: gRPC Assumptions/Get（authorization: Bearer <サービス JWT>）
  Note over C,E: mTLS はサイドカーが終端する
  E->>S: 平文 h2c
  S->>S: JwtBearer 検証 → 認可 → AssumptionsService.GetCurrent()
  S-->>C: GetAssumptionsResponse（assumptions / version）
```

## 非機能・運用

- **並走の扱い**: 1 経路について REST と gRPC が並走する期間がある。**正は REST**。撤去は最終段の判断である。
- **観測**: gRPC の状態コードは呼び出し側の警告ログに出る。gRPC 専用の計装は展開の issue で扱う。
- **配備**: 既定では有効化しない（helm の既定描画は変えていない）。有効化するときは
  **提供側の `grpcPort` と呼び出し元の宛先を同じ変更で揃える**（片方だけだと常に安全側既定へ倒れる）。

## 未決事項

- 稼働クラスタでの h2c 往復は**未実測**（新イメージの配備を要するため）。
- gRPC ヘルスプロトコル（`grpc.health.v1`）の要否（今は HTTP の readiness で足りる）。

## 関連仕様

- データ仕様書: [全体前提条件](../data/trading-assumptions.md)
- 検査器: [`scripts/README.md`](../../scripts/README.md)
