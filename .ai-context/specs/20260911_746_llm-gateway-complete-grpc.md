---
title: LlmGateway の POST /complete 2 経路へ gRPC 輸送を足す（既定は REST・切替は LlmGateway:Grpc の有無）
type: spec
status: done
related_ids: [NFR, FR-04, FR-06, FR-11, FR-16, NFR-05, ADR-0017, IADR-0284, IADR-0323, IADR-0328, IADR-0123, IADR-0051, IADR-0013, MSP:ADR-0029, MSP:ADR-0075, MSP:IADR-0424]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md
---

# 仕様書: LlmGateway `POST /complete` 2 経路の east-west gRPC 化（#746）

## 起点

issue #746（傘 #584）。[IADR-0328](../adr/IADR-0328_east-west-grpc-foundation-stage0.md) のフォローアップ 2 が
「**AST→MSP の LlmGateway 2 本は「基盤待ち」から外れた**（`platform/llmgateway/v1/completion.proto` が公開された）。
段の追加を起票する」と申し送った、その段である。

[IADR-0284](../adr/IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 2 は AST→MSP の 4 本
（LlmGateway `POST /complete` ×2、DocumentService `POST /documents`、RetrievalService `POST /search`）を
**基盤待ち**に置いていた。本 PR はそのうち **LlmGateway の 2 本だけ**を外す。残る 2 本は proto が無いため据え置く。

段の呼び方: 決定 5 の段 0〜6 は **AST 内 22 本**の順序であり、AST→MSP は別系列である。本 PR を
**段 1′（AST→MSP・LlmGateway）** と呼ぶ。#745 の段 1（Configuration `Assumptions`）とは提供側が違い、
ファイル領域も交差しないため**並行して進められる**。

## 再検証（2026-09-11・隣接クローン `../microservices-platform` の作業ツリー・読み取り専用）

| 待ち先の条件 | 実測 | 判定 |
| --- | --- | --- |
| proto の現物 | `src/platform/backend/Shared/Platform.Shared.Contracts/Protos/platform/llmgateway/v1/completion.proto`（`package platform.llmgateway.v1` / `service LlmCompletion` / `rpc Complete` unary ＋ `rpc CompleteStream` server-streaming） | **あり** |
| 実装ガイド | `docs/api/east-west-grpc.md` §1〜§4 ＋「3 つ目の面: テキスト生成（`platform.llmgateway.v1.LlmCompletion`）」 | **あり** |
| 認可 | ガイド「3 つ目の面」: 認証・認可は `ServiceCaller`。**REST の 3 口も同じ 1 つのポリシーを要する**（MSP#1364 / `MSP/IADR-0424`） | **REST と同一** |
| 縮退の向き | ガイド 🔴「縮退はエラーではない」。越境拒否・プロバイダ未登録・上流不調は **`sent=false` の応答**（REST の 200 ＋ `Sent=false` と同値） | **REST と同一** |

## 対象範囲

- 対象: `TradeDecisionService`（`HttpLlmCompletionClient`）と `ReportService`（`HttpReportNarrativeDrafter`）が
  呼ぶ `POST /complete` **2 経路**の輸送を差し替え可能にする。REST は並走で残す（撤去しない）。
- 対象外:
  - `POST /complete/stream`・`POST /embed`（本リポジトリから呼んでいない。IADR-0323 残余リスクのとおり）。
  - DocumentService `POST /documents`・RetrievalService `POST /search`（proto がまだ無い＝基盤待ちのまま）。
  - AST 内 22 本（段 1〜6。#745 ほかで別途）。
  - `Foundation/Grpc/`（IADR-0328 の土台）の**改修**。本 PR は利用するだけで手を入れない（#745 と同時進行のため）。
  - 稼働クラスタでの h2c 実往復（クラスタが要る。基盤 MSP#1255 と同じく**未計測のまま残す**）。

## 設計

### 1. proto は基盤の現物を写す（wire 面は逐語）

`backend/Shared/AiStockTrading.Shared.Infrastructure/Protos/platform/llmgateway/v1/completion.proto` へ置く。
パスは IADR-0328 決定 4 の規約どおり**所有者**（platform / llmgateway / v1）に従う。

🔴 **プロジェクトだけは IADR-0328 決定 4（`AiStockTrading.Shared.Contracts`）から外した。** 着手時は
決定 4 のとおり Contracts へ置いたが、`AiStockTrading.Architecture.Tests.SharedProjectDependencyTests`
が赤くなった（実測。下記「途中で分かったこと」）—— Contracts は **Domain から到達でき、
「Domain は .NET 標準のみに依存する」（`MSP/ADR-0030` §基本方針 / IADR-0256）を迂回させないため
`PackageReference` ゼロで保たれている**。gRPC の生成には `Grpc.Tools` / `Google.Protobuf` /
`Grpc.Net.Client` が要る。検査器の指示どおり `AiStockTrading.Shared.Infrastructure` へ置いた
（両サービスとも既に参照している）。IADR-0332 決定 1 が決定 4 のこの 1 点を上書きする。

- **`package` / `service` / `message` / フィールド番号・型・名前は 1 文字も変えない**（wire 面の同一性）。
- **`option csharp_namespace` だけ AST 側へ寄せる**（`AiStockTrading.Shared.Infrastructure.Grpc.LlmGateway.V1`）。
  C# だけの見え方であり wire には出ない。理由と残余リスクは IADR-0332 決定 1。
- 先頭に**出所（provenance）のコメント**を置く。本リポジトリは MSP に依存しない（ADR-0029 決定 2）ため、
  追随は人手であり、出所が書いていなければ次に見る人が正本を辿れない。
- 生成は `<Protobuf ... GrpcServices="Client" />`（呼び出し側だけ）。**生成物はコミットしない**（`obj/`）。

### 2. 輸送の継ぎ目（seam）を 1 つ作り、判定器は 1 つに保つ

現行 2 クラスの中身は「①要求を組む → ②送る → ③応答を解釈する」で、②だけが輸送に依る。
③（`Sent=false`・`stopReason`・割当照合・費用計測・Hold / プレースホルダの振り分け）は **輸送に依らない**。

**gRPC 用のクラスを別に作らない。** 作ると判定器が 2 つになり、片方だけ直る事故が構造的に入る
（基盤の実装ガイドも「判定器を 2 つにしない」と書く）。②だけを `ILlmCompletionTransport` へ切り出す。

```
ILlmCompletionTransport.CompleteAsync(LlmCompletionCall, TimeSpan? deadline, CancellationToken)
  → LlmCompletionExchange { Outcome, Payload?, Failure, Detail }
     Outcome = Completed | Failed | Malformed | TimedOut | Faulted
```

- `RestLlmCompletionTransport`（`HttpClient`）: 現行の `PostAsJsonAsync("/complete")` ＋ 非 2xx 分類 ＋ JSON 解釈をそのまま持つ。
- `GrpcLlmCompletionTransport`（生成クライアント）: `Complete` unary を呼び、`RpcException` を分類する。

既存クラスの**公開コンストラクタ（`HttpClient` を取る形）は残す** —— 内部で REST 輸送を組むだけにして、
既存の配線・既存試験を 1 行も変えずに通す。

### 3. 切替は `LlmGateway:Grpc` の有無。既定は REST

| 構成 | 解決される実装 |
| --- | --- |
| `LlmGateway:Grpc` が絶対 URI | **gRPC 輸送**（h2c。`http://<service>:8081`） |
| `LlmGateway:Grpc` 無し・空・不正 URI で `LlmGateway:BaseUrl` が絶対 URI | REST 輸送（現行） |
| どちらも無し | プレースホルダ（現行の安全既定＝常に Hold / 定型散文） |

- **helm の既定描画は 1 バイトも変わらない** —— `LlmGateway__Grpc` を `values.yaml` へ**足さない**。
  env は values の `env:` 配列がそのまま描かれる形であり、`{{- if }}` の分岐を新設しない。
  有効化は values に 1 行足すだけであることを `values.yaml` の**コメント**（描画に出ない）で示す。

### 4. s2s トークンは IADR-0323 と同じ資格情報（`ai-stock-trading-llm-caller`）

`LlmGateway:Auth`（MSP レルム）を引く。REST は `AddAiStockTradingPlatformRealmToken` が
名前付き HttpClient へ `ServiceTokenHandler` を挿す形だが、gRPC には `IHttpClientBuilder` が無いので、
**同じ `ServiceAuthOptions` から inline で `IServiceAccessTokenProvider` を作る**登録点を
`PlatformRealmAuthExtensions` へ足し（`CreatePlatformRealmTokenProvider`）、
IADR-0328 決定 1 の `GrpcClientExtensions.CreateAiStockTradingChannel(address, tokenProvider)` へ渡す。

- 🔴 **DI へ登録しない**（IADR-0323 決定 1 / IADR-0093 決定 2）。AST レルムの `IServiceAccessTokenProvider` と
  `TryAddSingleton` で衝突し、レルムを跨いでトークンが漏れる。
- 資格情報が未整備なら**トークンを返さない供給元**を返す（`GetTokenAsync` → `null`）。
  メタデータを付けずに送る → 提供側が `UNAUTHENTICATED` → 下の分類 → 既存 fail-safe。REST の「付けない → 401」と同じ向き。

### 5. 認可の失敗の分類（IADR-0323 決定 3 を gRPC へ写す）

| gRPC status | `LlmFailureKind` | 呼び出し元の扱い |
| --- | --- | --- |
| `UNAUTHENTICATED` / `PERMISSION_DENIED` | **`Unauthorized`** | trade-decision: `HoldUnauthorized`・**`TradeDecisionSkipped` を publish しない**／report: 資格情報を名指しする警告 |
| `RESOURCE_EXHAUSTED` | `Retryable` | 既存の「それ以外」枝（`HoldFallback` / プレースホルダ） |
| `DEADLINE_EXCEEDED`（と、呼び出し側キャンセルでない `CANCELLED`） | — | 既存のタイムアウト枝 |
| それ以外すべて | `Other` | 既存の「それ以外」枝 |

🔴 **gRPC の status からは `ModelUnavailable` を作らない。** 基盤の契約では「モデルが使えない」は
**エラーではなく `sent=false` の応答**で来る（ガイド 🔴「縮退はエラーではない」）。`INVALID_ARGUMENT` や
`UNIMPLEMENTED` を REST の 4xx になぞらえて `ModelUnavailable` へ倒すと、**輸送の誤設定が
「割当モデルが利用できません」として監査台帳・月報・Discord へ残る** —— IADR-0323 が閉じた誤帰属を、
別の入口から作り直すことになる。判断の記録は IADR-0332 決定 3。

### 6. 種別別タイムアウト（IADR-0123）→ gRPC の deadline

- report: `ReportNarrativeTimeouts.For(kind)`（日報 30 / 週報・月報 120 秒）が解決した値を、
  要求単位の CTS（現行のまま）**に加えて** `CallOptions.Deadline` へ写す。
- trade-decision: `LlmGateway:TimeoutSeconds`（未設定・非正値は 30 秒）を gRPC 輸送の**既定 deadline** に置く
  （REST では `HttpClient.Timeout` が担っていた上限。gRPC には `HttpClient` が無いので明示する）。
- deadline は**サーバ側へも伝播する**ため、CTS だけの打ち切り（クライアント側で捨てるだけ）より強い。

## 走査した母集合（規則 2・9・10）

**軸 1**（`/complete` の呼び出し）: `git grep -n '"/complete"' -- backend`（コード・試験の全ファイル。拡張子で絞らない）
= **6 行 / 6 ファイル**。実装 2（`HttpLlmCompletionClient` / `HttpReportNarrativeDrafter`）＝**改修対象**、
試験 4（`HttpReportNarrativeDrafterTests` / `HttpLlmCompletionClientTests` / 両 `LlmGatewayAuthWiringTests`）＝
**REST 輸送の試験としてそのまま残す**（REST は並走するので、これらが緑のままであることが「壊していない」の証拠になる）。

**軸 2**（`LlmGateway:` 構成キー）: `git grep -n 'LlmGateway:' -- backend` = **51 行 / 17 ファイル**。
新設するのは `LlmGateway:Grpc` の 1 キーのみで、既存キー（`BaseUrl` / `TimeoutSeconds` / `TimeoutSecondsByKind` /
`Purpose` / `Confidentiality` / `LogPrompts` / `Auth:*`）の**意味は変えない**。

**軸 3**（分類の消費点）: `git grep -ln 'LlmFailureKind\|LlmFailureClassification' -- backend` = **4 ファイル**
（`LlmFailureClassification.cs`・その試験・消費 2 クラス）。gRPC の写像は**同じファイル**へ足す
（分類の語彙を 2 箇所に置かない）。🔴 ただし引数は `Grpc.Core.StatusCode` ではなく **`int`** である ——
同ファイルのプロジェクトは `PackageReference` を持てない（上記）。数値が実際の enum とずれていないことは
`Grpc.Core` を参照できる層（`AiStockTrading.Shared.Infrastructure.Tests`）の試験が縛る。

**軸 4**（`east-west` の語）: `git grep -ln 'east-west'` = **18 ファイル**。扱い:

| ファイル | 扱い |
| --- | --- |
| `.ai-context/adr/IADR-0284_*.md` | **日付つき追記**（決定 2 の「AST→MSP 4 本は基盤待ち」が LlmGateway の 2 本について偽になる。規則 10） |
| `.ai-context/adr/IADR-0328_*.md` | **日付つき追記**（フォローアップ 2「段の追加を起票する」の履行。決定 1〜5 は不動） |
| `.ai-context/adr/README.md` | **追随**（IADR-0332 の行を追加。IADR-0284 / IADR-0328 の行へ 1 文ずつ） |
| `docs/blocked-tasks.md` | **据え置き**（B-4 の #584 行は段 0 の着手で解除済み。本 PR で新たに偽になる記述は無い ―― 実測: 同ファイルの `east-west` 該当行は「段 0 で解除」の記述のみ） |
| `.ai-context/specs/20260903_584_*.md` 2 件・`20260911_584_*.md`・`20260910_724_*.md` | **書き換えない**（確定済みの凍結記録。当時の実測として正しい） |
| `Foundation/Grpc/*.cs`（2）・その試験（2）・`.csproj`・`Directory.Packages.props`・`helm.yml`・`deploy/helm/templates/*`（2） | **据え置き**（段 0 の土台。本 PR は利用するだけで改修しない＝#745 とのファイル交差を作らない） |
| `CHANGELOG.md` | **触らない**（生成物） |

**軸 5**（本 PR で新たに偽になる自分の記述・規則 10）: `git grep -n '匿名\|/complete は' -- docs .ai-context` および
`git grep -n 'LlmGateway' -- docs` = docs 側 **7 行 / 3 ファイル**。
`docs/tech/system-architecture.md`（`POST /complete` の図・`LlmGateway:BaseUrl` の説明）と
`docs/security/security.md`（経路B は平文 HTTP）は、**既定が REST のままである以上いずれも真**であり据え置く
（h2c も平文であり、後者の「平文」の指摘は gRPC でも当たる）。`docs/blocked-tasks.md` の LlmGateway 行は
Istio STRICT による遮断の話で、輸送の別とは独立。**除外の理由は以上のとおりで、黙って落とした行は無い。**

## 受け入れ基準

- [x] `LlmGateway:Grpc` の有無で輸送が切り替わり、**既定は REST**（helm の既定描画・`values-local` 描画とも develop とバイト等価）
- [x] 認可失敗（`UNAUTHENTICATED` / `PERMISSION_DENIED`）が `LlmFailureKind.Unauthorized` へ倒れ、
      trade-decision は `TradeDecisionSkipped` を publish しない（陽性・陰性の対照つき）
- [x] 種別別タイムアウト（IADR-0123）が gRPC の `CallOptions.Deadline` へ写る（陽性・陰性の対照つき）
- [x] s2s トークンは `LlmGateway:Auth`（＝ IADR-0323 の `ai-stock-trading-llm-caller`）から取る。未整備ならメタデータを付けない
- [x] REST 経路の既存試験が 1 件も落ちない（並走の証明）
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` と文書系検査器が通る
- [ ] **稼働クラスタで h2c の実往復**（クラスタが要る。本 PR では**未計測のまま残す**。基盤 MSP#1255 と同じ扱い）

## テスト方針

xUnit v3 ＋ AwesomeAssertions。**陽性・陰性の対照を必ず組にする**。

| 観点 | 正（陽性） | 負（陰性の対照） |
| --- | --- | --- |
| gRPC status → 分類 | `Unauthenticated` / `PermissionDenied` → `Unauthorized` | `ResourceExhausted` → `Retryable`／`Unavailable`・`Internal`・`InvalidArgument`・`Unimplemented`・`NotFound` → `Other`（**`ModelUnavailable` にならない**） |
| 認可失敗の扱い（trade-decision） | `PermissionDenied` で `HoldUnauthorized` を返し `TradeDecisionSkipped` を **publish しない** | `Internal` では `HoldFallback`／`sent=false` では `HoldFallback`（縮退は応答であってエラーではない） |
| 認可失敗の扱い（report） | `Unauthenticated` でプレースホルダ散文＋資格情報を名指しする警告 | `Internal` ではプレースホルダ散文（同じ倒れ先でも**ログの原因が違う**こと） |
| deadline | 種別ごとの上限（日報 30 / 週報 120 秒）が `CallOptions.Deadline` に載る | 既定 deadline（`LlmGateway:TimeoutSeconds`）が per-call 指定の無いときに載る／`DeadlineExceeded` はタイムアウト枝へ |
| 応答の写し | `sent=true` の本文・model・トークン数・stopReason が REST と同じ経路で解釈される | `sent=false` は縮退（REST と同じ倒れ先） |
| 切替の配線 | `LlmGateway:Grpc` 設定時に gRPC 輸送が選ばれる | 未設定なら REST 輸送／`BaseUrl` も無ければプレースホルダ |
| s2s | 資格情報ありでメタデータへ `Authorization: Bearer` | 未整備ならメタデータを付けない（`GetTokenAsync` → `null`） |

**変異による確認**（証跡は PR 本文）: (a) `ClassifyGrpcStatus` の `Unauthenticated`/`PermissionDenied` 行を削って
`Other` へ倒すと認可分類の試験が赤、(b) `CallOptions.Deadline` の設定を落とすと deadline の試験が赤、
(c) `Unimplemented` を `ModelUnavailable` へ倒すと誤帰属の陰性対照が赤。

## 途中で分かったこと（着手時の設計から変えた点）

- 🔴 **proto と生成クライアントは `AiStockTrading.Shared.Contracts` へ置けない。**
  IADR-0328 決定 4 はそこを指定していたが、**段 0 の時点では proto が 0 件で当たっていなかった**。
  実測（`dotnet test backend/backend.slnx`）:
  `SharedProjectDependencyTests.Domain_から到達してよい共有プロジェクトが外部ライブラリへ依存しない` が
  「外部ライブラリが要る共有物は `AiStockTrading.Shared.Infrastructure` 側へ置くこと」と言って落ちた。
  **検査器の指示に従い移した**（`Composable/Llm/GrpcLlmCompletionTransport.cs` ＋ `Protos/`）。
  輸送に依らない型（`ILlmCompletionTransport` / `LlmCompletionCall` / `LlmCompletionPayload` /
  `LlmCompletionExchange` / `RestLlmCompletionTransport` / `LlmGatewayGrpc`）は
  **外部ライブラリを要さない**ので Contracts に残した。
- 🔴 **`Uri.TryCreate(..., Absolute)` は `llmgateway-service:8081` を受理する**（scheme が
  `llmgateway-service` の絶対 URI として解釈される。試験で実測）。`http`/`https` まで見て弾く。
- 生成コードの名前空間に `.Grpc.` が入るため、**同アセンブリ内で `Grpc.Core` が隠れる**
  （`…Infrastructure.Grpc.Core` を探して CS0234）。輸送のファイルは冒頭の `using Grpc.Core;`
  （コンパイル単位スコープ）で解決している。

## 計画書との差異

- 差異: なし。計画 `ADR-0017` 決定 2（フォールバック禁止・Hold へ倒す）・決定 3（429 とモデル不可の分割）は不変で、
  本 PR は輸送を 1 つ足すだけである。`MSP/ADR-0029`（east-west は gRPC）・`MSP/ADR-0075`（基盤先行）にも沿う。

## 未決事項

- **稼働クラスタでの実往復が未計測**である（受け入れ基準の最後の 1 項）。基盤側の LlmGateway に
  h2c ポート（`Grpc__Port`）が配備されていること・AST から `:8081` へ到達できること（Istio PeerAuthentication）は
  クラスタでしか確かめられない。**有効化はクラスタ作業と一体で行う**（本 PR では values を変えない）。
- 段 6（REST 撤去）の時期は未定。IADR-0284 決定 5 のとおり撤去まで並走させる。
