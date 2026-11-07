---
title: 統制の入力になるメッセージの発行元を受け手で確かめる（発行元のサービスごとの鍵の HMAC・段 1 は記録だけ・段 2 で処理しない）
type: spec
status: accepted
related_ids: [NFR, NFR-05, FR-10, FR-04, FR-20, UC-01, UC-02, UC-06, ADR-0009, ADR-0013, ADR-0080, IADR-0627, IADR-0610, IADR-0129, IADR-0568, IADR-0623, MSP:IADR-0691]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md (決定4 3 統制は新規建てだけを止め、手仕舞いと損切りは止めない)
  - planning:projects/ai-stock-trading/07_adr/ADR-0080_cloud-poc-gate-in-cluster-credentials-and-pre-secret-reachability.md (決定 1 クラスタの中で乱数で作る資格情報も OpenBao から供給する)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10・NFR-05)
---

# 仕様書: 統制の入力になるメッセージの発行元を受け手で確かめる

## 起点

- 利用者裁定（2026-11-07）: **受け手で発行元を確かめる。** クラウド移行の前の堅牢化（非機能 `NFR`。計画の非機能要件表に当たる番号は無い）。
- 基盤の MSP の IADR-0691 §残る制約 1: ブローカの権限だけでは、受信キューを持つ利用者から他のキューへの投入を止められない。受け手で発行元を確かめる仕組みは取引ユニットの作業である。
- 決定は [IADR-0627](../adr/IADR-0627_message-origin-signing-for-control-inputs.md)。

## 1. 現状の棚卸し: 注文の承認の印（IADR-0610）

基点は develop `72747d18f`（shallow ではない）。

| 項目 | 中身 | 場所 |
| --- | --- | --- |
| 署名する側 | リスク管理の `OrderApprovalPublisher`（唯一の口）。審査・利用者の手仕舞い・維持率の自動縮小の 3 経路 4 か所がここを通る。素の発行は本サービスのソースを走査する試験が止める | `RiskManagementService/Features/RiskManagement/OrderApprovalPublisher.cs` |
| 確かめる側 | 発注執行の `OrderApprovalVerifier.Admit`。`OrderApprovedHandler.Handle` の先頭（直列化・予約・ブローカより前） | `OrderExecutionService/Infrastructure/Steps/OrderApprovalVerifier.cs` |
| 印 | ヘッダー `ast-order-approval-sig` に `v1.<base64(HMAC-SHA256)>`。正準形は項目を明示（文字列は長さを前置・decimal は末尾のゼロを落とす）。比較は固定時間 | `Shared.Infrastructure/Composable/Messaging/OrderApprovalSignature.cs` |
| 鍵 | 2 サービスで共有する 1 つ（32 バイト以上・base64）。構成 `OrderApprovalSigning:Key`／入れ替えの間だけ `PreviousKey`。chart は `global.orderApprovalSigning` から 2 サービスにだけ env を描く。クラウドは OpenBao `cloud/ast/ast-secrets` の `order-approval-signing-key` を基盤の `gen` で作り、`ast-secrets`（`dataFrom.extract`）で写す | `values.yaml`・`values-cloud.yaml`・手順書 §5.1 |
| 段 | `OrderApprovalSigning:Mode`。段 1 `Report`（既定）は記録して通す。段 2 `Enforce` は発注しない（EventId 15270）。段 2 で鍵が無ければ起動で止まる。chart は段 1 で `optional`、段 2 で必須。values-cloud は段 2 | 同上 |
| 再送の排除 | 印には ID・時刻を含めない。再送は受け手の業務の冪等（DecisionId の重複排除・予約）で止める | `OrderExecutionAppService`（DecisionId） |
| 対象外（IADR-0610 §残余） | 発注に進まない受け手（台帳・活動射影・監査）、承認以外の発注系メッセージ | — |

## 2. 対象の型の母集合（規則 9: ハンドラの受ける型をすべて走査した）

### 引き方

- **走査 1**（基盤の生成器の導出を流用）: MSP `scripts/gen-rabbitmq-permissions.js` の `derive()` を `AST_ROOT=<本ブランチ>` で走らせ、AST の 11 サービスの受ける型（Wolverine のハンドラ発見の規則: `*Handler` / `*Consumer` の公開クラスの `Handle` 系の第 1 引数）と、組み立てる型を得た。受ける型は **68**（契約のイベント 67 ＋ 通知の中だけで回す `DiscordCommandContext`）。
- **走査 2**（本 PR の試験 `MessageOriginCatalogCompletenessTests`）: 同じ規則を C# で書き、サービスごとの受ける型を表と突き合わせる（足し忘れで赤）。結果は走査 1 と一致した。
- 受け手が監査・通知だけの型は 43。残る 25 型が、判断・統制・発注の受け手（リスク管理・発注執行・取引判断・市場監視・費用統制）を持つ。その（受け手, 型）の組は 28。

### 対象（24 型・受け手の組 26）

| 型 | 発行するサービス | 確かめる受け手 | 受け手での影響 |
| --- | --- | --- | --- |
| `TradeDecisionMade` | 取引判断 | リスク管理・市場監視 | 審査を経て印つきの注文の承認になる。急変検知の基準値 |
| `TradeDecisionHeld` | 取引判断 | 市場監視 | 急変検知の基準値 |
| `PriceMovementDetected` | 市場監視 | 取引判断 | 取引判断のサイクルを起こす（LLM の費用・発注） |
| `InformationCollected` | 情報収集 | 取引判断 | 判断の材料 |
| `StopLossTriggered` | 市場監視 | 発注執行 | ソフトウェア逆指値の建玉を成行で決済する |
| `InformationSourceDegraded` / `InformationSourceRecovered` / `InformationSourceStateObserved` | 情報収集 | リスク管理 | 新規建ての停止の出し入れ |
| `BacktestEvaluated` | バックテスト | リスク管理 | 段階ゲートの昇格の入力・空売りの解禁の判定 |
| `BrokerAccountObserved` | 発注執行 | リスク管理 | 口座種別・基準資金（equity 比の上限の基準）・発注先の突き合わせ |
| `BrokerAvailabilityObserved` | 発注執行 | リスク管理 | 段階ゲートの稼働の分数 |
| `BrokerPositionsObserved` | 発注執行 | リスク管理 | 建玉の突き合わせ・強制買戻しの推定・維持率の自動縮小の材料 |
| `OrderExecuted` | 発注執行 | リスク管理 | 取引台帳・GFV の計数・段階ゲートの件数・相場操縦の検知 |
| `OrderCancelled` / `OrderDispatchForgone` | 発注執行 | リスク管理 | 処理中の決済の押さえの解放（取り違えると二重決済） |
| `OrderModified` | 発注執行 | リスク管理 | 相場操縦の検知の入力 |
| `ProtectiveStopPlaced` / `ProtectiveStopCoverageLost` | 発注執行 | リスク管理 | 保護レグの承認行（損切りの約定を台帳が相関する） |
| `SoftwareStopExecuted` / `SoftwareStopLineWidened` | 発注執行 | リスク管理 | ソフトウェア逆指値の記録 |
| `OrderApproved` | リスク管理 | リスク管理・発注執行 | 発注（発注執行は IADR-0610 の印も確かめる）・台帳と活動射影（IADR-0610 §残余 2 の分） |
| `PositionCloseCancellationRequested` | リスク管理 | 発注執行 | 板の手仕舞いをブローカで取り消す |
| `PositionDriftAdopted` | リスク管理 | 発注執行 | 保護レグを取り込んだ建玉に合わせる |
| `LlmCostIncurred` | 取引判断・報告書 | 費用統制 | 月次上限（費用の統制）の台帳 |

### 対象外（理由）

| 受け手 / 型 | 理由 |
| --- | --- |
| 監査のサービス（受ける 67 型すべて） | 受けた事実を記録するだけで、判断・統制・発注に使わない |
| 通知のサービス（受ける契約の型 31） | 利用者へ知らせるだけ。操作は認可つきの REST / gRPC を通る |
| リスク管理の `StopLossTriggered` | 検知をログに残すだけで、何も発行・記録しない（決済は発注執行） |
| 費用統制の `AssumptionsChanged` | キャッシュを捨てるだけで本文の値を使わない（版は REST で取り直す） |
| 通知の `DiscordCommandContext` | 通知のサービスの中だけで回す型（ブローカを通らない） |

### 発行元の突き合わせ

- 走査 1 の「組み立てる型」から、対象の型の発行元を確かめた。発注執行は `OrderApproved` を組み立てるが発行しない（保護レグの承認を自分の中で組み直す。IADR-0610 の仕様書 §実測 2）。
- 試験 `表の型を組み立てるサービスは表の発行元である` が、`new <型>(` を書くサービスが表の発行元であることを固定する（上の 1 件は理由つきで除く）。

## 3. 決定（IADR-0627 の要約）

1. 表（`MessageOriginCatalog`・24 型）を PlatformShim の `Foundation/Messaging` に置く。
2. 送る側: `Policies.AllSenders` の `CustomizeOutgoing` で、表の型を自分が発行元のときだけ印つきで送る。ヘッダー `ast-origin`・`ast-origin-id`・`ast-origin-at`・`ast-origin-sig`。印の対象は型・ID・発行元・時刻・本文の正規形。
3. 受け手: 表の型のハンドラの前の middleware で、発行元の許可・印・時刻の窓（古さ 24 時間・先 5 分）・再送（成功した ID を窓の長さだけ覚える）を確かめる。
4. 段: `MessageOrigin:Mode`。段 1 `Report`（既定・ローカル）は記録して処理、段 2 `Enforce`（values-cloud）は処理しない。監査の記録（EventId 16260 / 16261）とメトリクス `ast.messaging.origin_failures`、警報 `AstMessageOriginRejected`。段 2 で要る鍵が無ければ起動で止まる。
5. 鍵 7 つ（発行元の識別子）。OpenBao `cloud/ast/ast-message-origin-keys` に基盤の `seed-secrets.sh … gen` で作り、ESO（`externalSecrets.messageOriginKeys`）で Secret `ast-message-origin-keys` へ写す。chart は `global.messageOrigin.services` に従い、サービスごとに要る鍵だけを渡す。

| サービス | 渡す鍵 |
| --- | --- |
| trade-decision | information-collection・market-monitor・trade-decision |
| order-execution | market-monitor・order-execution・risk-management |
| market-monitor | market-monitor・trade-decision |
| risk-management | backtest・information-collection・order-execution・risk-management・trade-decision |
| information-collection | information-collection |
| backtest | backtest |
| report | report |
| cost-control | report・trade-decision |

- 基盤（MSP）の変更は要らない: OpenBao の policy `ast-eso` は `cloud/data/ast/*` を読める（MSP `deploy/cloud/vault/policies/ast-eso.hcl`）。`seed-secrets.sh` の `gen` はパス `cloud/ast/<英小文字・数字・->`・キー `[A-Za-z0-9._-]`・16〜1024 バイトを受ける。
- 計画との関係: FR-10・ADR-0009 決定 4（決済を止めない）に反しない（確かめは発行元だけで、決済にも新規建てにも同じく掛かる）。鍵は ADR-0080 決定 1・NFR-05 のとおり OpenBao から供給する。計画への環流は要らない。

## 4. 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC-1 | 24 型それぞれで、発行元の口の印は段 2 でも通る | `MessageOriginVerificationTests.発行元の口で印を付けたメッセージは段2でも通る` |
| AC-2 | 24 型それぞれで、Wolverine の直列化を往復しても印は合う | `…Wolverineの直列化を往復しても印は合う` |
| AC-3 | 24 型それぞれで、段 2 は 印なし・許されない発行元・別の鍵の印（発行元の偽装）・本文の改竄・処理し終えた ID の再送 を拒む | `…印の無いメッセージは段2で拒まれる` ほか 4 つ |
| AC-4 | 24 型それぞれで、段 1 は上の 4 つを記録だけで通す | `…段1では合わないメッセージも記録だけで通す` |
| AC-5 | ID・時刻の書き換え、古すぎ・先にありすぎは段 2 で拒む。失敗からの再試行・同じ配信の再試行は再送に数えない | `…印の対象の見出しを書き換えた…`・`…古すぎる…`・`…処理が成功する前の再試行…` |
| AC-6 | Wolverine の配線を通して、段 2 の受け手は印なし・偽装・改竄・再送をハンドラへ渡さず、段 1 は渡す。発行元の送り先の封筒に印が付く | `MessageOriginWiringTests` |
| AC-7 | 段 2 で要る鍵が 1 つでも無ければ起動で止まり、揃えば起動する（8 サービス） | `…段2で要る鍵が1つでも無ければ起動で止まる`・`…揃えば起動する` |
| AC-8 | 表は、ハンドラの受ける型の母集合と理由つきの対象外に一致し、全 Program.cs が配線を呼ぶ | `MessageOriginCatalogCompletenessTests` |
| AC-9 | chart の各サービスの鍵は要る鍵に一致し、values-cloud は段 2・鍵を OpenBao から写す。既定とローカルは段 1 | `MessageOriginChartTests`・helm.yml「Assert values-cloud secrets from OpenBao and stage 2」 |
| AC-10 | メトリクスは型・理由・結果で数え、ダッシュボードと警報が引く | `BusinessMetricsTests`・`check-observability-assets.js` |
| AC-11 | 検証を外す変異で試験が落ちる | §5 |

## 5. 変異（検証を外す）の実測

本 PR の作業ツリーで、部品を 1 つずつ外して `MessageOrigin` の試験を流し、元へ戻した（結果は PR の本文にも記す）。

`dotnet test backend/TestSupport/AiStockTrading.TestSupport.PlatformShim.Tests --filter "FullyQualifiedName~MessageOrigin"`（変異なしは 239 件すべて緑）。

| 変異 | 落ちた試験（239 件中） |
| --- | --- |
| M1 確かめを外す（`Admit` が常に通す） | 124 |
| M2 発行元の許可を外す | 24 |
| M3 印の照合を外す | 51 |
| M4 再送の検出を外す | 26 |
| M5 受け手の middleware の配線を外す | 4 |
| M6 段 2 の起動の止めを外す | 8 |
| M7 送る側の印の配線を外す | 1 |
| M8 本文を正準形から外す | 25 |
| M9 古さの窓を外す | 1 |

## 6. 範囲の外

- ブローカの権限の絞り込み（基盤の管掌。MSP の IADR-0691）。
- 非対称鍵・鍵の自動の入れ替え・受け手の間で共有する再送の記録（IADR-0627 §残余）。
