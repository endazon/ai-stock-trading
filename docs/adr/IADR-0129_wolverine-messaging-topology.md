---
title: IADR-0129 Wolverine 移行のトポロジ設計（キュー名にサービス名を前置し、ローカルルーティングを無効化する）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-03
  - FR-04
  - FR-10
  - FR-17
  - UC-01
  - UC-02
  - UC-06
  - ADR-0013
  - IADR-0001
  - IADR-0106
  - IADR-0128
author: claude
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0013_messaging-follow-wolverine-kafka.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0028_broker-rabbitmq-kafka.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
---

# IADR-0129: Wolverine 移行のトポロジ設計（キュー名にサービス名を前置し、ローカルルーティングを無効化する）

- 状態: Accepted
- 日付: 2026-08-03
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-03 / FR-04 / FR-10 / FR-17、UC-01・UC-02・UC-06、NFR（保守性・ライセンス継続性）
- 関連 ADR: 計画 [[ADR-0013]]（Wolverine 移行に追随する・Accepted）／platform ADR-0027（Wolverine）・ADR-0028（RabbitMQ 継続）・ADR-0030（ライブラリ棚卸し）
- 関連 IADR: [[IADR-0106]]（consumer クラス名＝キュー名。**本 ADR がその前提を無効化する**）／[[IADR-0128]]（標準プロジェクト構成。consumer を Infrastructure に置いた根拠に本移行を織り込んでいる）
- 関連仕様書: `docs/specs/20260803_354_wolverine-migration.md`
- Issue: #354（#345 分割 4/4）

## コンテキストと課題

計画 [[ADR-0013]]（Accepted）により、本ユニットのメッセージングは MassTransit から **Wolverine** へ移行することが確定している（MassTransit v9 の商用化、v8 の OSS サポート終了が 2026 年末）。移行そのものは決定済みであり、本 ADR が決めるのは**「どういうブローカのトポロジで移行するか」**である。

これは書き換えの美観の問題ではない。**キュー名の導出規則が変わることで、pub/sub が competing consumer へ退行し得る**。同じ事故は #258 として現に発生しており（[[IADR-0106]]）、取引判断が承認・拒否・エラーのいずれにも現れずに消えた。当時の対策は「consumer クラス名をサービス跨ぎで一意にする」であり、`scripts/check-consumer-endpoint-names.js` が CI で守っている。

**その対策は Wolverine ではまったく効かない。**実測（Wolverine 6.24.5 を実際に構成して名前を印字）で次を確認した。

1. Wolverine の RabbitMQ conventional routing は、リスニングキュー名を**メッセージ型名だけ**から導く（`NamingSource.FromMessageType` が既定）。**ハンドラのクラス名は一切関与しない。** `TradeDecisionMadeBaselineHandler` でも `Whatever` でも、キューは `TradeDecisionMade` である。
   - すなわち [[IADR-0106]] の改名（`TradeDecisionMadeConsumer` → `TradeDecisionMadeBaselineConsumer`）は無効化され、**同じイベントを購読する別サービスは「たまたま」ではなく「必ず」同一キューを共有する**。既定のまま移行すれば #258 が全 fan-out 経路（実測 19 経路）で同時に再発する。
2. さらに Wolverine の既定（conventional local routing）は、**発行しようとしたメッセージ型に自プロセス内のハンドラがあると、ルートをプロセス内に閉じる**。実測でも `PublishAsync(TradeDecisionMade)` の解決結果が `local://tradedecisionmade/` のみになった。
   - 本ユニットには該当が現に存在する。**RiskManagementService は `OrderApproved` を発行し、同時に `OrderApproved` を購読している**（台帳計上・活動投影）。既定のまま移行すると、承認済みの発注が RiskManagement のプロセス内で処理されるだけで **OrderExecutionService へ一通も届かず、発注が一件も執行されない**。`StopLossTriggered` も同型である。
   - この退行はビルドもユニットテストも緑のまま起こる。**#258 より広範で、より静かである。**

つまり Wolverine の既定は、本ユニットの pub/sub トポロジに対して MassTransit の既定より**危険側**である。何を決めるかというと、その既定をどう覆すかである。

## 検討した選択肢

### 案A: 既定の conventional routing をそのまま使い、キューの分離はしない（棄却）

**棄却理由**: 上記 1 のとおり、同じイベントを購読する全サービスが 1 本のキューを取り合う。#258 の再現であり、検討に値しない。

### 案B: キュー名にサービス名を前置し、exchange は共有する（採用）

`QueueNameForListener(t => $"{ServiceName}.{t.Name}")` でリスニングキューを `<ServiceName>.<メッセージ型名>` にする。exchange は Wolverine 既定のまま「メッセージ型名の fanout」で共有し、各サービスのキューをそこに bind する。

### 案C: `PrefixIdentifiers("<service>")` で全識別子にサービス名を前置する（棄却）

Wolverine の `BrokerExpression.PrefixIdentifiers(string)` は queue も exchange も両方に接頭辞を付ける。

**棄却理由**: **exchange まで分離されると fan-out が成立しない。**発行側は `<自サービス>.OrderApproved` という自分専用の exchange へ publish し、購読側は `<購読サービス>.OrderApproved` を待つため、両者が永久に出会わない。「一意にする」ことだけを見て採ると、退行 A を避けたつもりで**もっと悪い形**（誰にも届かない）になる。

### 案D: 全キュー・全 binding を `ListenToRabbitQueue` / `PublishMessage<T>().ToRabbitExchange(...)` で明示宣言する（棄却）

**棄却理由**: 47 の購読と 21 の発行を全部手書きすることになり、宣言漏れが「静かに届かない」形の事故に直結する。規約から外れた 1 行を人間のレビューで見つけるのは、まさに #258 で失敗した方法である。**規約を 1 箇所に閉じ込め、規約からの逸脱を機械で検出する**ほうが強い。

### 案E: MassTransit の命名（exchange 名＝メッセージ URN）へ合わせ込み、混在期間も相互運用させる（第 1 段階では棄却）

`ExchangeNameForSending(t => "AiStockTrading.Shared.Contracts.Events:" + t.Name)` ＋ `UseMassTransitInterop()`。

**棄却理由**: 移行完了後に不要になる互換設定を、移行の全期間にわたって全サービスへ入れて回り、あとで剥がすことになる。剥がし忘れれば恒久的な負債になる。本リポジトリに自動デプロイのワークフローは無く（デプロイは `scripts/k8s-local-deploy.sh` の手動実行）、**混在状態をデプロイしない**運用で回避できる。回避できない事情が生じた場合の逃げ道としてのみ残す（決定 7）。

## 決定

### 決定 1: キュー名は `<ServiceName>.<メッセージ型名>` とする

リスニングキューの名前を `$"{ServiceName}.{messageType.Name}"` とする。`ServiceName` は各サービスの `Program.cs` に既にある一意な定数（例 `ai-stock-trading.cost-control-service`。OpenTelemetry / Serilog のサービス識別にも使われている）をそのまま用いる。

- 例: `ai-stock-trading.risk-management-service.TradeDecisionMade` / `ai-stock-trading.market-monitor-service.TradeDecisionMade`
- キュー名の一意性は **`ServiceName` の一意性だけ**に帰着する。新しい命名規約を人間が覚える必要はなく、クラス名の付け方が機能要件になることもない。
- 導出は純関数 `AiStockTradingQueueNaming.QueueNameFor(serviceName, messageType)` に置き、**キュー名の出所を 1 箇所にする**。

### 決定 2: exchange はメッセージ型名の fanout を共有する（Wolverine 既定のまま）

fan-out はここで成立する。1 イベント → 1 fanout exchange → 購読サービス数だけのキュー → 各サービスが全件受け取る。**exchange にサービス名を混ぜない**（案C の否定）。

### 決定 3: `DisableConventionalLocalRouting()` を全サービスで必須とする

発行がプロセス内へ閉じる退行（コンテキスト 2）を止める。発行元サービスが自分でも購読している場合は、自分のキューにもブローカ経由で届く（MassTransit の現行と同じ意味）。

### 決定 4: 上記 3 点をサービス側の裁量にしない

共通ヘルパ `WolverineOptions.UseAiStockTradingRabbitMq(serviceName, connectionString)`（`AiStockTrading.TestSupport.PlatformShim`）に封じ込め、各サービスの `Program.cs` はこれを 1 行呼ぶだけにする。**サービス側にトポロジの選択肢を残さない**。素の `UseConventionalRouting(` / `ListenToRabbitQueue(` / `PrefixIdentifiers(` をサービス側で直接呼ぶことは、静的検査（`scripts/check-consumer-endpoint-names.js`）で禁止する。

### 決定 5: 再試行と DLQ は現行の意味を保つ

- 再試行: `OnAnyException().RetryWithCooldown(2s, 10s, 30s).Then.MoveToErrorQueue()`（現行 `UseAiStockTradingRetry` と同値）。
- DLQ: **`<queue>_error`** を明示指定する（例 `ai-stock-trading.cost-control-service.LlmCostIncurred_error`）。Wolverine の既定は全キュー共有の `wolverine-dead-letter-queue` だが、それでは「どのキューで失敗したか」がキューを見ただけでは分からず、MassTransit 時代の運用手順（`<queue>_error` を覗く）が通じなくなる。**RabbitMQ の実配線の意味を変える変更であるため本 ADR に記録する。**
- キューは durable / auto-delete しない（Wolverine 既定＝現行と同じ）。

### 決定 6: `WolverineFx.RuntimeCompilation` を参照する

Wolverine 6 系はコア本体からランタイムコンパイラ（Roslyn）を分離しており、既定の `TypeLoadMode.Dynamic` のままでは**起動時に例外で停止する**（実測）。代替は `dotnet run -- codegen write` による事前生成＋`TypeLoadMode.Static` だが、11 サービス分の生成コードを版管理する運用が増える。第 1 段階では `RuntimeCompilation` を参照する。

- 再評価条件: 起動時間・コンテナサイズ・メモリが問題になった時点、または全サービス移行が終わり生成コードの管理コストが読める時点。

### 決定 7: 混在期間は「デプロイしない」で回避する

MassTransit と Wolverine は **exchange 名もエンベロープ形式も異なり、無設定では相互運用しない**（MassTransit の exchange 名は `AiStockTrading.Shared.Contracts.Events:X`、Wolverine は `X`）。ビルド時の併存は成立する（別パッケージ・別サービス）が、**混在状態をブローカ上で動かしてはならない**。

- 本リポジトリに自動デプロイのワークフローは無い（`ci.yml` / `helm.yml` / `integration.yml` にデプロイ手順は無く、デプロイは `scripts/k8s-local-deploy.sh` の手動実行）。第 2 段階完了までデプロイしないことで回避する。
- どうしても混在デプロイが必要になった場合の逃げ道は、Wolverine 側の `UseMassTransitInterop()` ＋ exchange 名の合わせ込み（案E）である。**採用する場合は必ず新しい IADR を起こす**（剥がし忘れが恒久的な負債になるため、期限と剥がす条件を明記させる）。

### 決定 8: 静的検査の不変条件を入れ替える

`scripts/check-consumer-endpoint-names.js` の不変条件を「consumer クラス名の一意性」から次へ移す。ファイル名と CI ジョブ名は据え置く（追跡の連続性のため）。

- 新: ① `ServiceName` 定数がサービス跨ぎで一意 ② サービスが共通ヘルパを迂回していない ③ 1 サービスが MassTransit と Wolverine を両方配線していない
- 旧規則（クラス名の衝突）は**未移行サービスに対してのみ**適用し、第 3 段階で撤去する。除外リストは作らず、`Program.cs` の内容から移行済み／未移行を自動判定する（除外リストは「一時措置」のまま残るのが常であるため）。
- 検査器が空振りしていないこと（走査したサービス数の下限）を併せて検査する（[[IADR-0127]] / [[IADR-0128]] 決定 6 と同じ「静かに失効する経路を塞ぐ」思想）。

## 理由

- **一意性の根拠を「人間の命名」から「既にある一意な識別子」へ移せる。** [[IADR-0106]] の弱点は、キューの一意性がクラス名という「本来は自由なもの」に依存していた点にある。`ServiceName` はサービスの同一性そのものであり、重複させる動機が誰にも無い。
- **exchange を共有し queue だけを分ける形が、fan-out の意味をそのまま表す。** RabbitMQ の fanout exchange は「配りたい相手が増えたら queue を bind する」という道具であり、本ユニットの pub/sub はまさにそれである。
- **[[IADR-0106]] が「全キュー名が変わるから」を理由に案B（プレフィックス）を棄却した前提は、本移行で消える。** Wolverine 移行はキュー名が**どのみち全部変わる**（`TradeDecisionMade` は残るが、購読するサービスの数だけ新しいキューが要る）。孤児キューの発生は移行そのものの結果であり、プレフィックスの採否とは独立である。よって当時の棄却理由は本移行では成立しない。
- **危険な既定は、使える場所に置いておくと必ず使われる。**決定 4（共通ヘルパへの封じ込め＋逸脱の静的検出）が無ければ、サービスを 1 つ足すたびに退行 A・B の両方が再発し得る。

## 結果

- 良い影響:
  - サービス跨ぎのキュー名衝突が**構造的に不可能**になる（命名規律に依存しない）。
  - キュー名から所有サービスが即座に分かる（運用・障害解析が楽になる。#258 の調査ではキューと所有者の対応が読めないことが遅れの一因だった）。
  - 別プロダクトが同一ブローカへ同居しても衝突しない（#258 の増幅要因だった MSP 側の重複デプロイと同種の事故のうち、**別プロダクト由来のもの**は構造的に防げる。同一プロダクトの重複デプロイは防げない）。
  - テストで送信先 URI まで検証できるようになり、MassTransit ハーネスより表明が強くなる。
- 悪い影響 / トレードオフ:
  - **キュー名が全部変わる。** 旧キュー（`TradeDecisionMade` 等）はブローカ上に consumer 不在で残り、binding も生きているためメッセージが滞留し得る。**移行完了後に旧キューを手動削除する手順が必要**である（第 3 段階の作業項目とする）。
  - 同一サービス内で同じイベント型を複数ハンドラが処理する箇所は、MassTransit ではキューが分かれていたが Wolverine では 1 本に統合される（片方の失敗が両方の再実行を招く）。該当は RiskManagementService の 2 経路。第 2 段階で扱う。
  - ハンドラは **public でなければ発見されない**（実測）。現行の `internal sealed` な consumer は可視性を広げることになる。
  - キュー名が長くなる（最長でも約 70 文字。RabbitMQ の上限 255 バイトに対して十分）。
- フォローアップ:
  1. 第 2 段階: 残り 9 サービス＋ BFF。`MassTransitExtensions` 削除、CPM から MassTransit 削除、`check-banned-libraries.js` の PENDING → BANNED 昇格。
  2. 第 3 段階: 検査器から旧規則を撤去、Integration テスト（実 RabbitMQ）の追随、[[IADR-0106]] を **Superseded by IADR-0129** にする、旧キューの削除手順を運用仕様書へ。
  3. 計画への環流（`/plan-feedback`）: (a) Wolverine 6 のランタイムコンパイラ分離、(b) Wolverine の既定が fan-out を壊すこと。いずれも platform ADR-0027 の前提に対する重要な但し書きであり、基盤側の移行にも同じ罠がある。

## 関連

- Supersedes: なし（[[IADR-0106]] は第 3 段階で Superseded にする。第 1 段階では未移行サービスに対して**現に有効**であるため状態を変えない）
- Superseded by: なし
