---
title: IADR-0332 LlmGateway の一括生成は輸送だけを差し替え可能にし、既定を REST に据えたまま gRPC を並走させる（認可の分類は輸送ごとに写す）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-04
  - FR-06
  - FR-11
  - FR-16
  - NFR-05
  - ADR-0017
  - IADR-0051
  - IADR-0061
  - IADR-0071
  - IADR-0093
  - IADR-0104
  - IADR-0123
  - IADR-0216
  - IADR-0256
  - IADR-0284
  - IADR-0323
  - IADR-0328
  - MSP:ADR-0029
  - MSP:ADR-0030
  - MSP:ADR-0075
  - MSP:IADR-0424
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md
---

# IADR-0332: LlmGateway の一括生成は輸送だけを差し替え可能にし、既定を REST に据えたまま gRPC を並走させる（認可の分類は輸送ごとに写す）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: **Accepted**
- 日付: 2026-09-11
- 決定者: Claude Code（起案）／ endazon（マージ判断）

## 起点・関連

- 関連する計画書 ID: **`MSP/ADR-0029`**（east-west は gRPC。🔴 本リポの裸の `ADR-0029` は資料再編であり別物）、
  **`MSP/ADR-0075`**（基盤先行）、`ADR-0017`（LLM フォールバック方針。決定 2 の Hold・決定 3 の 429 分割は不変）、
  FR-04（売買判断）・FR-06 / FR-16（報告書）・FR-11（監査ログ）・NFR-05（認証情報）
- 対象 issue: #746（傘 #584）。基盤側は MSP#1255（gRPC 面）・MSP#1365 / `MSP/IADR-0424`（REST 3 口の `ServiceCaller`）
- 関連する実装仕様書:
  [`.ai-context/specs/20260911_746_llm-gateway-complete-grpc.md`](../specs/20260911_746_llm-gateway-complete-grpc.md)
- 前提:
  [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md)（射程・順序。AST→MSP 4 本は「基盤待ち」だった）、
  [IADR-0328](IADR-0328_east-west-grpc-foundation-stage0.md)（段 0 の土台。h2c チャネル・s2s・proto の置き場）、
  [IADR-0323](IADR-0323_llm-gateway-service-token-and-authz-failure-classification.md)（s2s の資格情報と認可失敗の分類）、
  [IADR-0123](IADR-0123_report-narrative-timeout-by-kind.md)（報告書種別ごとのタイムアウト）、
  [IADR-0093](IADR-0093_kb-writer-cross-realm-s2s.md)（クロスレルム s2s を DI へ登録しない型）

## コンテキストと課題

IADR-0328 のフォローアップ 2 が「**AST→MSP の LlmGateway 2 本は「基盤待ち」から外れた**
（`platform/llmgateway/v1/completion.proto` が公開された）。段の追加を起票する」と申し送った。その段である。

移す対象は 2 経路だけである（実測: `"/complete"` の呼び出しはコード 2・試験 4 の計 6 行）。

| サービス | 呼び出し口 |
| --- | --- |
| `TradeDecisionService` | `HttpLlmCompletionClient` → `POST /complete` |
| `ReportService` | `HttpReportNarrativeDrafter` → `POST /complete` |

ところが**この 2 クラスは「送る」クラスではない**。中身の大半は応答の解釈である ——
`Sent=false` の縮退・`stopReason`（拒否 / 上限到達）・割当モデルの照合（`ADR-0011` のピン）・
費用計測・Hold / プレースホルダの振り分け。輸送に依るのは「送る」1 段だけで、残りは
**REST でも gRPC でも同じでなければならない**。

決めるのは 6 点である: ①proto の写し方、②切替の設定点、③gRPC status の分類、
④タイムアウト → deadline、⑤クラスの分け方（判定器を 2 つにしない）、⑥s2s の供給。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A（採用）** | **輸送だけを継ぎ目（`ILlmCompletionTransport`）にする。** 既存 2 クラスは判定器として据え置き、REST / gRPC の 2 実装を差し替える | 判定器が 1 つのまま。既存の配線・試験（REST）は 1 行も変えずに通る＝「壊していない」ことが緑で示せる |
| B | `GrpcLlmCompletionClient` / `GrpcReportNarrativeDrafter` を新設して `ILlmCompletionClient` / `IReportNarrativeDrafter` を実装する | **不採用**。判定器が 2 つになる。約 200 行の縮退ロジックが二重化し、片方だけ直る事故が構造的に入る（基盤の実装ガイドも「判定器を 2 つにしない」と書く） |
| C | REST を撤去して gRPC へ一斉に切り替える | **不採用**。IADR-0284 決定 5 が「REST は撤去（段 6）まで並走」と定めており、退行時の切り分けができなくなる |
| D | 基盤の proto を参照だけして写さない | **不可**。本リポジトリは MSP に依存しない（submodule も pin も無い。ADR-0029 決定 2 / IADR-0228）。参照する手段が無い |

## 決定

### 決定 1 — proto は wire 面を逐語で写し、C# 名前空間だけ AST 側へ寄せる

パスは IADR-0328 決定 4 の規約どおり **所有者のユニット・サービス・版**に従う
（所有者は LlmGateway であって AST ではない）——
`Protos/platform/llmgateway/v1/completion.proto`。

🔴 **ただし置くプロジェクトは `AiStockTrading.Shared.Contracts` ではなく
`AiStockTrading.Shared.Infrastructure` である。IADR-0328 決定 4 のこの 1 点を上書きする。**
Contracts は **Domain から到達でき、「Domain は .NET 標準のみに依存する」（`MSP/ADR-0030` §基本方針 /
IADR-0256）を迂回させないため `PackageReference` ゼロで保たれている** ——
`AiStockTrading.Architecture.Tests.SharedProjectDependencyTests` が csproj の静的解析で止める。
gRPC の生成には `Grpc.Tools` / `Google.Protobuf` / `Grpc.Net.Client` が要るので置けない。
**決定 4 が Contracts と書けたのは、段 0 の時点で proto が 0 件＝規約が一度も実行されていなかったからである**
（着手時に決定 4 のとおり置いて赤を実測した。作業仕様書 §途中で分かったこと）。
**パスの規約（所有者に従う）は不変**で、変えたのは器だけである。

| 項目 | 扱い |
| --- | --- |
| `syntax` / `package` / `service` / `rpc` / `message` / フィールドの名前・番号・型 | **1 文字も変えない**（wire 面の同一性。ここが違うと通信そのものが壊れる） |
| `option csharp_namespace` | **変える**（`Platform.Shared.Contracts.Grpc.LlmGateway.V1` → `AiStockTrading.Shared.Infrastructure.Grpc.LlmGateway.V1`）。C# の見え方だけで wire には出ない |
| 先頭コメント | **出所（provenance）を書く**。正本のリポジトリ・パス・写した日・「追随は人手である」ことを明記する |
| `rpc CompleteStream` | **落とさない**（本リポジトリは呼んでいない）。落とすと「写しが部分集合である」ことを次に見る人が判別できない |
| 生成 | `GrpcServices="Client"`（呼び出し側だけ）。生成物は `obj/` でコミットしない |

C# 名前空間を変える理由: **AST のアセンブリが `Platform.Shared.Contracts.*` の型を出荷するのは所有関係の誤表示**である。

🔴 **代償を 1 つ引き受けた。** `….Grpc.…` という名前空間が生えることで、**同アセンブリ内では
`Grpc.Core.X` が素の名前で解決できなくなる**（`…Infrastructure.Grpc.Core` を探して CS0234。実測）。
輸送のファイルは冒頭の `using Grpc.Core;`（コンパイル単位スコープ）で解決している。
`.Grpc.` の階層は基盤の versioning 規約（実装ガイド §2）と揃えるために残す。

🔴 **写しの追随は機械で検知できない。** 本リポジトリは MSP に依存しないので、正本が変わっても CI は緑のままである
（残余リスクへ再掲）。

### 決定 2 — 切替は `LlmGateway:Grpc` の**有無**。既定は REST で、helm の既定描画は 1 バイトも変えない

| 構成 | 選ばれる実装 |
| --- | --- |
| `LlmGateway:Grpc` が `http`/`https` の絶対 URI | **gRPC 輸送**（h2c。既定ポート 8081） |
| 上記が無く `LlmGateway:BaseUrl` が絶対 URI | REST 輸送（現行） |
| どちらも無い | プレースホルダ（安全既定＝常に Hold / 定型散文） |

- **`values.yaml` へキーを足さない。** env は values の配列がそのまま描かれる形なので、空値で置くだけでも
  既定描画が変わる。有効化の手順は**コメント**（描画に出ない）で示した。実測: 既定描画・`values-local` 描画とも
  develop と SHA-256 一致。
- **新しい `{{- if }}` の分岐をテンプレートに作らない**ので、`helm.yml` に「ON にした派生」の描画検査
  （IADR-0058 の核心）を足す必要は無い。捕まえるべき「有効化した瞬間に壊れるテンプレート」が存在しない。
- 🔴 **不正な値で起動を落とさない。** `Uri.TryCreate(..., Absolute)` は `llmgateway-service:8081` を
  **scheme が `llmgateway-service` の絶対 URI として受理する**（実測）。scheme まで見て弾き、弾いたら REST へ倒す。
  受け側の `Grpc:Port`（IADR-0328）が構成誤りで**落とす**のとは立場が逆である ——
  あちらは「立てたつもりで立っていない」が沈黙するので落とす。こちらは安全既定が効く経路であり、
  綴り誤りをサービスの起動不能に化けさせない。

### 決定 3 — gRPC の status からは `ModelUnavailable` を作らない

IADR-0323 決定 3 の分類（`Unauthorized` / `Retryable` / `ModelUnavailable` / `Other`）はそのまま使い、
写像だけ輸送ごとに置く（**同じファイル** `LlmFailureClassification.cs` に並べる。語彙を 2 箇所に置かない）。

🔴 **引数は `Grpc.Core.StatusCode` ではなく `int`（canonical status code）である。** 同ファイルは
`PackageReference` を持てない層にあるため（決定 1 の 🔴）。語彙を輸送側へ切り出して 2 箇所に分けるより、
**数値で受けて写像を 1 箇所に残す**ほうがよい —— 分かれると片方だけ直る。数値が実際の enum と
ずれていないことは、`Grpc.Core` を参照できる層の試験（`Shared.Infrastructure.Tests`）が突き合わせる。

| gRPC status | 分類 |
| --- | --- |
| `UNAUTHENTICATED` / `PERMISSION_DENIED` | **`Unauthorized`**（基盤の拒否表: トークン無し → 前者、`platform-service` 無し → 後者） |
| `RESOURCE_EXHAUSTED` | `Retryable`（REST の 429 と同じ。`ADR-0017` 決定 3） |
| `DEADLINE_EXCEEDED` / `CANCELLED` | 分類しない（決定 4 で `OperationCanceledException` として上げる） |
| それ以外すべて | `Other` |

🔴 **`INVALID_ARGUMENT` / `UNIMPLEMENTED` / `NOT_FOUND` を REST の 400 系になぞらえない。**
基盤の契約では「モデルが使えない」は**エラーではなく `sent=false` の応答**で来る（実装ガイドの
「縮退はエラーではない」）。status からモデル不可を作ると、**proto の食い違いや gRPC 面の未配備という
輸送の誤設定が「割当モデルが利用できません」として監査台帳・月報・Discord へ残る** ——
IADR-0323 が閉じた誤帰属を、別の入口から作り直すことになる。倒れ先は同じ Hold なので**安全側は変わらない**。
変わるのは**記録の正しさ**であり、それが IADR-0323 の主題だった。

### 決定 4 — 打ち切りは輸送で握り潰さず、上限は `CallOptions.Deadline` へ写す

- **タイムアウトとキャンセルの区別は呼び出し元が持つ。** 呼び出し元は
  `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)` で
  「タイムアウト（縮退してよい）」と「停止要求（伝播すべき）」を分けており、この判別には
  **外側のトークン**が要る（報告書は要求単位の CTS を linked で被せるため、輸送へ渡るトークンは
  既に「タイムアウトでも立つ」）。輸送は `RpcException(DEADLINE_EXCEEDED / CANCELLED)` を
  `OperationCanceledException` へ翻訳するところまでを担う。伝送の例外も握り潰さない。
- **deadline は要求単位 → 輸送の既定、の順で解決する**（IADR-0123 決定 1 を gRPC へ写す）。
  report は種別別（日報 30 / 週報・月報 120 秒）を要求ごとに渡し、trade-decision は
  `LlmGateway:TimeoutSeconds`（既定 30 秒。REST では `HttpClient.Timeout` が担っていた上限）を輸送の既定に置く。
  どちらも無ければ deadline を**付けない**（勝手な上限を作らない）。
- REST 輸送は `deadline` 引数を**見ない**。呼び出し元の CTS ＋ `HttpClient.Timeout` が既に上限であり、
  二重に張ると「どちらで切られたか」が読めなくなる（IADR-0123 決定 5 が残した秒数の意味が壊れる）。

### 決定 5 — クラス名 `Http…` は据え置く（改名は別 PR）

`HttpLlmCompletionClient` / `HttpReportNarrativeDrafter` は輸送を意味しなくなったが、**改名しない**。
挙動を 1 バイトも変えない改名で 2 サービス・十数の試験ファイルに触ると、**この PR の diff から
「輸送の追加」が読めなくなる**（IADR-0259 決定 9 の「移動と書き換えを混ぜない」と同じ理由）。
名前が実態を指していないことは、両クラスの先頭コメントに明記した。

### 決定 6 — s2s は REST と同じ資格情報を inline で供給する

`LlmGateway:Auth`（MSP レルム。IADR-0323 の 2026-09-11 追記により専用 client
**`ai-stock-trading-llm-caller`** ＝ `ast-secrets` の `llm-auth-*`）をそのまま引く。
REST は `IHttpClientBuilder` へハンドラを挿す形だが gRPC にはそれが無いので、
`PlatformRealmAuthExtensions.CreatePlatformRealmTokenProvider(...)` を足し、
IADR-0328 決定 1 の `CreateAiStockTradingChannel(address, tokenProvider)` へ渡す。

- 🔴 **DI へ登録しない**（IADR-0323 決定 1 / IADR-0093 決定 2）。AST レルムの `IServiceAccessTokenProvider` と
  `TryAddSingleton` で衝突すると**レルムを跨いでトークンが漏れる**。これは「認可が通らない」より悪い。
- 資格情報が未整備なら `NoServiceAccessTokenProvider`（常に `null`）を返す ——
  メタデータを付けずに送る → `UNAUTHENTICATED` → 決定 3 → 既存 fail-safe。REST の「付けない → 401」と同じ向き。
- 🔴 **チャネルはプロセスに 1 本。** `ILlmCompletionClient` は Scoped であり、解決のたびにチャネルを作ると
  HTTP/2 接続が増え続ける。生成クライアント（`LlmCompletion.LlmCompletionClient`）を singleton で登録する
  —— **LlmGateway 専用の型**なので、同じサービスへ他の面（#745 の Configuration `Assumptions` ほか）が
  入っても DI で衝突しない。

## 理由

- **判定器を 1 つに保つことが、この移行で最も守るべき不変条件である。** 縮退の向き・記録の原因・
  Hold の理由は輸送の話ではない。輸送だけを継ぎ目にすれば、REST の既存試験がそのまま
  「壊していない」の証拠になる（実測: REST 側の試験は 1 件も変更していない）。
- **既定を動かさない。** `LlmGateway:Grpc` を置かない限り、コードも helm も本 PR 前と等価である。
  有効化はクラスタ側の作業（基盤の h2c 配備・到達性）と一体であり、コードのマージとは切り離す。
- **誤帰属を作り直さない。** 決定 3 は「安全側かどうか」ではなく「記録が正しいか」の判断である。

## 結果

- 良い影響: AST→MSP の LlmGateway 2 本が「基盤待ち」から外れた。残る 2 本
  （DocumentService `POST /documents`・RetrievalService `POST /search`）は proto が無く据え置き。
- 悪い影響・トレードオフ:
  - **クラス名が実態を指していない**（決定 5）。改名は別 PR。
  - **proto の写しは人手追随**（決定 1）。正本が変わっても CI は気付かない。
  - **稼働クラスタでの h2c 実往復は未計測**である（クラスタが要る。基盤 MSP#1255 と同じ扱い）。
    #746 の受け入れ基準のうちこの 1 項は**チェックしないまま残す**。
- フォローアップ:
  1. **有効化はクラスタ作業と一体で行う**（values に `LlmGateway__Grpc` を 1 行足す）。実往復を測ってから
     経路B の values-local へ入れる。
  2. `/complete/stream`・`/embed` を呼ぶことになったら、同じ `LlmGateway:Grpc` と同じ分類に乗る
     （`CompleteStream` は写しに含めてある。ただし**サーバストリーミングを unary へ潰さない**こと）。
  3. proto 互換検査器（`buf breaking` 相当）は IADR-0328 が段 1 へ送った。**本 PR の proto は AST が
     所有しない写しであり、互換の権威は基盤側にある** —— 検査器を入れるなら AST 所有の proto（#745）と一緒に。
  4. #584 は `Refs`（閉じない）。段 6（REST 撤去）まで開けておく。

## 関連

- Supersedes: なし（IADR-0328 決定 5 の「fail-safe の写像方針」を、LlmGateway の 2 経路について具体化したもの。
  同決定 5 は「共通のヘルパは書かない・写像は段 1 以降に各 `Infrastructure/` へ」としており、本 ADR は
  **写像そのものは輸送側に、安全側の倒れ先は呼び出し元に**という形でこれを満たす）
- Superseded by: なし
