---
title: east-west gRPC の段 1 —— 全体前提条件（Assumptions）の同期照会を生成クライアントへ寄せ、proto 互換検査器と結合試験を入れる
type: spec
status: done
related_ids: [NFR, FR-17, UC-06, IADR-0284, IADR-0328, IADR-0331, IADR-0063, IADR-0051, IADR-0256, IADR-0264, MSP:ADR-0029, MSP:ADR-0075]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 仕様書: east-west gRPC 段 1（Configuration `Assumptions` の同期照会）（#745）

## 起点

issue #745（傘は #584・`Refs`）。段 0（土台）は PR #744 / [IADR-0328](../adr/IADR-0328_east-west-grpc-foundation-stage0.md) で着地した。
本 issue は [IADR-0284](../adr/IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5 の**段 1** であり、
射程は #745 本文の 3 行と受け入れ基準 4 点に限る。

- 段 0 から**先送りした 2 点**をここで持つ（IADR-0328 決定 4・5 の明示）:
  **proto 互換検査器**（0 proto では「通った」が何も意味しないため段 1 へ移した）と
  **「呼び出し元ごとの timeout / retry が実際に効く」結合試験**（#584 併記の積み残し）。

## 対象範囲

- 対象:
  - `aistocktrading.configuration.v1.Assumptions/Get` の **proto** と生成クライアント・サーバ実装、`MapGrpcService` **1 件**
  - 呼び出し元 **2 サービス**（費用統制・取引判断）の切替（`Configuration:Grpc` の有無。**既定は REST**）
  - **proto 互換検査器**の移植（基盤 `scripts/check-proto-contracts.js` から。`--self-test` つき）と CI への配線
  - **timeout / retry の結合試験**（実 Kestrel h2c を立てて実際に往復させる。陽性・陰性対照つき）
- 対象外（記録だけ残す）:
  - **REST 面の撤去**（段 6。並走中の正は REST。`MSP/ADR-0029` 2026-08-04 追記）
  - 段 2 以降の経路（Risk 読み取り・Audit・Report/MarketMonitor/CostControl・Notification 書き込み）
  - `Microsoft.Extensions.Http.Resilience` / `HybridCache` への置換（#584 併記。**振る舞いの変更**であり別 PR）
  - **helm / compose の既定値変更**。`Configuration:Grpc` も `services.configuration-service.grpcPort` も
    **既定では宣言しない** —— 既定描画を develop とバイト一致に保つ（#745 受け入れ基準「既定は REST（バイト等価）」）
  - AST→MSP の LlmGateway 2 本（#746 で別途）

## 設計

### 1. proto の置き場・package（IADR-0328 決定 4 の履行）

| 項目 | 値 |
| --- | --- |
| パス | `backend/Shared/AiStockTrading.Shared.Grpc/Protos/aistocktrading/configuration/v1/assumptions.proto` |
| package | `aistocktrading.configuration.v1` |
| C# 名前空間 | `AiStockTrading.Shared.Grpc.Configuration.V1` |
| service / rpc | `Assumptions` / `Get`（unary）。REST の `GET /assumptions` と 1 対 1 |
| 生成 | `<Protobuf Include="Protos/**/*.proto" ProtoRoot="Protos" GrpcServices="Both" />`。生成物は `obj/`・コミットしない |

🔴 **ユニット名のセグメントは `aistocktrading`（ハイフン無し）である。** proto の package は識別子であり
`ai-stock-trading` は書けない。IADR-0328 決定 4 が既に `aistocktrading` と定めており、段 0 の試験
（`GrpcClientExtensionsTests`）も `aistocktrading.configuration.v1.Assumptions/Get` を先取りしている。

🔴 **置き場は `AiStockTrading.Shared.Contracts` ではなく新設の `AiStockTrading.Shared.Grpc` である**
（着手後の実測で判明した本リポ固有の制約。IADR-0331 決定 7）。IADR-0328 決定 4 は基盤の実装ガイドどおり
「ユニットの共有契約プロジェクト」と書いていたが、本リポの `Shared.Contracts` は **Domain から到達してよい
共有物**であり、`AiStockTrading.Architecture.Tests.SharedProjectDependencyTests` が**外部ライブラリ依存ゼロ**を
強制している（platform ADR-0030 / IADR-0256）。**実際に `Shared.Contracts` へ置いて検査が違反 2 件で赤くなった。**
名前空間も `…Shared.Contracts.*` にしない（`DomainSourceScan` の許可接頭辞であり、生成型を Domain から
`using` できてしまう）。基盤にこの制約は無く、乖離として受容する。

🔴 **金額・率は `string`（不変文化の丸め無し表現）で運ぶ。** proto3 に `decimal` は無く、`double` へ落とすと
`0.20315`（譲渡益税率）や手数料率が 2 進浮動小数へ丸められる。REST は `System.Text.Json` が `decimal` を
**桁を保ったまま** JSON 数値へ書いており、`double` にすると**同じ版・同じ値なのに REST と gRPC で結果が違う**。
論拠と棄却案は IADR-0331 決定 2。

### 2. 提供側（ConfigurationService）

- `Features/Assumptions/GetAssumptions/GrpcEndpoint.cs` に `AssumptionsGrpcService : Assumptions.AssumptionsBase`。
  **REST と同じ `AssumptionsService.GetCurrent()` を呼ぶ**（評価器を 2 つにしない。基盤の参照実装と同じ作法）。
- 認可は REST の読み取りと**同じ** `OwnerOrService`（IADR-0063 決定 2）。`[Authorize(Policy = ...)]` をクラスへ。
- `Program.cs`: `builder.AddAiStockTradingGrpcListener();` と `app.MapGrpcService<AssumptionsGrpcService>();`。
  **`Grpc:Port` 未設定なら h2c リスナは立たない**（IADR-0328 決定 3）ので、既定配備の挙動は変わらない。

### 3. 呼び出し元（費用統制・取引判断。**2 サービスに複製**）

`Infrastructure/ExternalServices/GrpcAssumptionsClient.cs`（新規・`IAssumptionsSource` の 2 つ目の実装）。

| 関心 | 実装 |
| --- | --- |
| チャネル | `GrpcClientExtensions.CreateAiStockTradingChannel(address, tokenProvider)`（段 0・IADR-0328 決定 1/2 のまま） |
| timeout | **試行ごとの** `CallOptions.Deadline`。`Configuration:GrpcTimeoutSeconds`（既定 **5**＝REST の `HttpClient.Timeout` と同値） |
| retry | **呼び出し元の明示ループ**。`Configuration:GrpcMaxAttempts`（既定 **1**＝REST と同じく再試行しない） |
| 再試行する status | `Unavailable` / `DeadlineExceeded` のみ |
| 再試行しない status | `Unauthenticated` / `PermissionDenied` / `NotFound` / `InvalidArgument` / `Internal` ほか（**即座に安全側既定へ**） |
| fail-safe | すべて `null` を返す（＝取得不可）。何へ倒すかは `CachedAssumptionsProvider`（LKG ＞ 既定値）が持つ（IADR-0063 決定 5） |

🔴 **既定を「再試行 1 回（＝しない）」にするのは、トランスポートの差し替えで振る舞いを変えないためである。**
リトライは `Configuration:GrpcMaxAttempts` を 2 以上にしたときだけ起きる（#584 が求めた「呼び出し元ごとの設定が実際に効く」の実体）。

**切替**（`AssumptionsClientExtensions.AddAiStockTradingAssumptions`）:

1. `Configuration:Grpc` が**空でなければ** gRPC（`ResolveGrpcAddress` が絶対 URI・`http` を検証し、**違反は起動時に落とす**）
2. そうでなければ従来どおり `Configuration:BaseUrl` の REST（未設定・不正 URI は `DefaultAssumptionsProvider`）

🔴 **既定は REST。** `Configuration:Grpc` を書かない限り 1 バイトも変わらない。
🔴 **キャッシュ・TTL・無効化・fail-safe は差し替えない** —— 交換するのは `IAssumptionsSource` だけである。
🔴 **不正な `Configuration:Grpc` は fail-loud**（`Configuration:BaseUrl` の安全既定と非対称）。理由は IADR-0331 決定 4。

### 4. proto 互換検査器（`scripts/check-proto-contracts.js`）

基盤（microservices-platform）の同名スクリプト（852 行）から**必要最小**を移植する。

| 移植する | 落とす |
| --- | --- |
| proto3 部分集合のパーサ・R1〜R4 の規約・baseline 比較（破壊的／非破壊）・allowlist・`--self-test` | `lib/excluded-units.js`（本リポにユニット除外は無い）・`src/` 前提のパス正規表現（本リポは `backend/Shared/<Project>/Protos/…`） |

**足したのは 1 件だけ**（自己試験 41 件目）: 🔴 **走査の基点を取り違えると「0 件で全件合格」になる**ため、
段 1 の proto を名指しで拾う陽性対照を置いた（基点そのものが基盤と違う移植であるから）。

- baseline: `scripts/proto-contract-baseline.json`（`--update` で生成）／allowlist: `scripts/proto-breaking-allowlist.json`（空）
- CI: `ci.yml` の `static-checks` へ `--self-test` と本走の 2 ステップ（既存の作法と同形）

### 5. テスト（xUnit v3 ＋ AwesomeAssertions。すべて陽性・陰性の対照つき）

| 置き場 | 観点 | 陽性 | 陰性（対照） |
| --- | --- | --- | --- |
| `ConfigurationService.Tests` | 写像 | `decimal` の桁が往復で保たれる（`0.20315` ほか） | `double` 経由なら壊れる値を `[Theory]` で並べる |
| 〃 | 同一評価器 | gRPC の応答が REST の `GET /assumptions` と**同値** | —— |
| 〃 | 認可 | `trading-service` ロールで通る | ロール無し → `PermissionDenied`／未認証 → `Unauthenticated` |
| `CostControlService.Tests` / `TradeDecisionService.Tests` | 切替 | `Configuration:Grpc` あり → `GrpcAssumptionsClient` | 無し → `HttpAssumptionsClient`／両方無し → `DefaultAssumptionsProvider` |
| 〃 | 構成の検証 | `http://…` は通る | `https://`・相対・非 URI は**起動時に例外** |
| 〃（**結合**・実 Kestrel h2c） | timeout | 遅い提供側 → deadline で `null`（既定 5 秒を短縮して観測） | 速い提供側 → 値が返る |
| 〃（**結合**） | retry | `Unavailable` を 1 回返す提供側に `MaxAttempts=2` → 2 回目で成功（**呼ばれた回数を数える**） | `PermissionDenied` は `MaxAttempts=3` でも **1 回**しか呼ばれない |
| `scripts` | 互換検査器 | `--self-test`（正例・負例・変異試験） | フィールド番号の付け替え・削除・型変更が**赤**になる |

## 走査した母集合（`.claude/rules/traceability.md` 規則 1〜10）

**軸 1**（`Configuration:BaseUrl` / `Configuration__BaseUrl`。誤りの側＝「REST しか無い」側から引く）:
`git grep -ln "Configuration:BaseUrl\|Configuration__BaseUrl" -- ':!CHANGELOG.md'` = **21 ファイル**。

| ファイル群 | 扱い |
| --- | --- |
| `backend/Services/{CostControl,TradeDecision}Service/Infrastructure/ExternalServices/AssumptionsClientExtensions.cs` | **変更**（切替の追加） |
| `backend/Services/{CostControl,TradeDecision}Service/Tests/…AssumptionsClientRegistrationTests.cs` | **変更**（切替の対照を追加） |
| `backend/Services/{CostControl,TradeDecision}Service/Program.cs` | 据え置き（`AddAiStockTradingAssumptions` の 1 行呼び出しは不変） |
| `deploy/helm/…/values.yaml`・`docker-compose.yml` | **据え置き**（既定は REST。gRPC の宛先は既定で書かない＝バイト等価の要件） |
| `backend/Services/CostControlService/{Infrastructure/ExternalServices/DefaultCostLimitsProvider.cs, Tests/CostControlWiringTests.cs, Tests/…/VersionedCostLimitsTests.cs}`・`backend/Services/TradeDecisionService/{Features/TradeDecision/ProfitabilityGateOptions.cs, Infrastructure/ExternalServices/NoOpProfitabilityAssumptionsProvider.cs, Tests/ProfitabilityWiringTests.cs}` | 据え置き（`Configuration:BaseUrl` を**別の目的**〔費用上限・採算ゲートの有効化判定〕で読む。トランスポートの話ではない） |
| `.ai-context/adr/IADR-0063`・`IADR-0065`・`IADR-0076`、`.ai-context/specs/` 3 件 | **書き換えない**（凍結記録。当時の記述として正しい） |
| `docs/data/trading-assumptions.md` | 据え置き（データ仕様。トランスポートに言及していない。実測で確認） |

**軸 2**（`IAssumptionsSource`。差し替える口の側から引く）:
`git grep -ln "IAssumptionsSource" -- ':!CHANGELOG.md'` = **10 ファイル**。実装 2・インタフェース 2・テストダブル 2・
キャッシュ 2 と凍結記録 2。**変更するのはインタフェース 2 ファイル**（新実装を足すのみ・既存メンバは不変）**と
キャッシュ 2 ファイル**（試験へ選択結果を見せる `internal` の読み取り専用プロパティ 1 行）。

**軸 3**（`grpc` 大小無視。段 0 の記述が本 PR で古くなるものを引く）:
`git grep -lni "grpc" -- ':!CHANGELOG.md'` = **27 ファイル**。

| ファイル | 扱い |
| --- | --- |
| `.ai-context/adr/IADR-0328`（決定 4「proto 互換検査器は段 1 へ移す」・決定 5「結合テストは段 1」） | **書き換えない**（凍結記録。本 PR がその履行であって、当時の記述は正しいままである） |
| `.ai-context/adr/README.md` | **変更**（IADR-0331 の行を追加） |
| `Directory.Packages.props` | **変更**（`Grpc.Tools` / `Google.Protobuf` を Contracts が使い始めるためコメントの「推移的に持ち込む」を実態へ） |
| `docs/blocked-tasks.md` | 据え置き（B-4 は段 0 で解消済みと記録済み。本 PR で真偽が動かないことを本文で確認した） |
| `.github/workflows/helm.yml` | 据え置き（段 0 が入れた ON 派生の描画検査。本 PR は helm を変えない） |
| `docs/observability/observability.md`・`infra/README.md`・`infra/otel/otel-collector-config.yaml`・`Foundation/Extensions/ObservabilityExtensions.cs` | 据え置き（**OTLP の gRPC**。east-west とは別物） |
| `docs/templates/api_spec_template.md` | 据え置き（雛形の例示） |
| 残り（`IADR-0259`/`0264`/`0284`、`specs/` 6 件、`Foundation/Grpc/*` 2 件、その試験 3 件、shim csproj、helm templates 2 件） | 据え置き（凍結記録／段 0 の成果物で本 PR は**足すだけ**） |

**軸 4**（`.proto` の実在）: `git ls-files "*.proto"` = **0 件**（本 PR が最初の 1 件）。
したがって検査器の「0 件走査で緑を返さない」ガードは、本 PR のあとで初めて意味を持つ。

**除外したものと理由**: `CHANGELOG.md`（生成物。是正は `scripts/changelog-overrides.json` の `remap` で行う規約）。
`.ai-context/specs/` の既存 6 件と `.ai-context/adr/` の既存 5 件（**凍結記録**。本文プロズを後から書き換えない）。
`backend/Services/{Backtest,OrderExecution}Service/**`（`protobuf` に当たるが **moomoo/futu API** の話であり east-west ではない。
軸 3 の別引き `git grep -lni "protobuf"` = 25 件で現れ、内容を読んで除外した）。

## 受け入れ基準（#745 本文）

- [x] proto と生成クライアント・サーバ実装。`MapGrpcService` **1 件**
- [x] 呼び出し元が `Configuration:Grpc` の有無で REST / gRPC を切替え、**既定は REST**（helm の既定描画は develop とバイト一致）
- [x] proto 互換検査器が CI に載る（**陰性対照**: フィールド番号の付け替えで赤）
- [x] timeout / retry の**結合試験**（陽性・陰性対照）
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` と文書系検査器が通る

## テスト方針

上の §5 の表のとおり。**変異による確認**（証跡は PR 本文）:

1. `GrpcAssumptionsClient` の再試行判定から `StatusCode.Unavailable` を外す → retry の陽性試験が落ちる。
2. 同じ判定に `StatusCode.PermissionDenied` を足す → retry の陰性対照（1 回しか呼ばない）が落ちる。
3. `CallOptions.Deadline` の設定を外す → timeout の陽性試験が落ちる（永久に待つ）。
4. `AssumptionsMapping` の `decimal` を `double` 経由にする → 桁保存の試験が落ちる。
5. `ResolveGrpcAddress` の scheme 検証を外す → `https` の陰性対照が落ちる。
6. proto のフィールド番号を 1 つ付け替える → `check-proto-contracts.js` が `[breaking]` で赤になる。

## 計画書との差異

- 差異: なし。射程・順序・一括移行の義務（IADR-0284 決定 1・2 と `MSP/ADR-0075`）は変えていない。
  段 0 → 段 1 への繰り延べ（proto 互換検査器・結合試験）は IADR-0328 決定 4・5 が既に宣言したものの履行である。

## 着手後に判明した制約（母集合の引き直し。規則 10）

**`Shared.Contracts` へ proto を置いた時点でアーキテクチャ検査が赤くなった**（実測・違反 2 件）。
これは着手前の走査（軸 1〜4）では出てこない —— **誤りの側の文字列が存在しなかった**（proto も
`Grpc.*` の `PackageReference` も本リポに 1 件も無かった）ためである。判明後に引き直した母集合:

| 引いたもの | 結果 | 扱い |
| --- | --- | --- |
| `git grep -n "DomainReachableSharedProjects" -A 6` | `AiStockTrading.Shared.Contracts` / `AiStockTrading.Shared.Kernel` の 2 件 | **新プロジェクトはどちらでもない**（検査対象外） |
| `git grep -n "IsAllowedDomainNamespace" -A 12` | 許可接頭辞は `System` / `…Shared.Contracts` / `…Shared.Kernel` / 自サービスの `Domain` | **名前空間を `AiStockTrading.Shared.Grpc` にする**（`…Shared.Contracts.Grpc` だと Domain から使えてしまう） |
| `git grep -ln "AiStockTrading.Shared.Infrastructure.csproj" --include=*.csproj` | 7 サービス ＋ 1 テスト。**設定管理・費用統制は未参照** | 検査の失敗メッセージが示す「Shared.Infrastructure へ置け」は採らない（無関係な依存を巻き込む） |
| `git grep -n "IsServiceClientProject" -A 4` | `*.Client` / `*.Client.Tests` の名前だけを見る | 新プロジェクト名は該当しない（`*.Client` の復活ではない） |
| `dotnet test … --collect:"XPlat Code Coverage"` の cobertura を `filename` で引く | 🔴 **生成 proto が分母に入っていた**（`obj/Debug/.../Assumptions.cs` / `AssumptionsGrpc.cs` = 751 行・被覆 348 行＝46.34%）。**床は割っていない**（除外前 86.00% / 除外後 87.10%・floor 83.00%） | **`coverage-floor.json` へ 3 つ目の除外**（IADR-0331 決定 8）。理由は「割るから」ではなく「ratchet が proto の本数で動くから」 |
| `git grep -n "findSourceFiles" -A 18 scripts/check-coverage.js` | `bin` / `obj` / `node_modules` を走査しない | G4（除外は自動生成の部分集合）は構造的に破れない＝この除外は手書きを飲み込まない |

## 未決事項

- **稼働クラスタでの h2c 往復は未実測**（基盤の実装ガイドも同じ未決を持つ）。本 PR は helm の既定を変えないため、
  実配備での有効化は段 6 までの間に別途判断する。
- `Microsoft.Extensions.Http.Resilience` / `HybridCache` への置換（#584 併記）は依然として別 PR。
  本 PR の retry は**呼び出し元の明示ループ**であり、ライブラリの置き換えではない。
