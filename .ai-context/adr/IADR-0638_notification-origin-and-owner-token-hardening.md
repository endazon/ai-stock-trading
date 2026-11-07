---
title: IADR-0638 通知の Discord のコマンドの処理をブローカのハンドラにせず、通知は受ける表の型の発行元を確かめ（段を下げて入れる）、所有者のトークンの azp の許可集合をクラウドで明示し、gRPC 面の所有者の分岐も宛先を確かめる
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-06, FR-09, FR-10, FR-11, FR-14, UC-06, ADR-0047, IADR-0611, IADR-0622, IADR-0627, IADR-0633, IADR-0634, IADR-0637]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06・FR-09・FR-10・FR-14)
---

# IADR-0638: 通知の受け口と発行元の確かめ・所有者のトークンの硬化

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「全部直してから稼働」）・claude（方式）

## 起点・関連

- 要求 **NFR-06**・**FR-09**（通知）・**FR-10**・**FR-14**。硬化の番号は計画に無いので裸の `NFR` を併記する。
- 改めるもの: IADR-0627 / IADR-0633 の「通知は確かめない（`ExcludedServices`）」・IADR-0611 の「gRPC 面の門は変えない」。
- 延長するもの: IADR-0611（所有者のトークンの aud・azp の段）・IADR-0633（発行元の署名。受け手は公開の鍵だけ）。
- 作業仕様書: `.ai-context/specs/20261107_message-receivers-and-owner-token-hardening.md`。

## コンテキストと課題

- 通知の Discord のコマンドの処理は、名前が Wolverine のハンドラの規則に当たり、ブローカから受ける連鎖が作られていた（入口は Gateway だけのはず）。
- 通知は統制の事象（約定・損切り到達・保護レグなど）を利用者へ知らせるが、発行元を確かめていなかった。判断・発注には使わないが、利用者の判断の材料になる。
- 所有者のトークンの azp の許可集合はコードの既定に頼り、既定にはクラウドに無いクライアントの名前が残っていた。
- gRPC 面の所有者の門は aud を見ず、REST と要件が揃っていなかった。

## 決定

### 決定 1 — Discord のコマンドの処理はブローカのハンドラにしない

- 通知の `*CommandHandler`（8 クラス）に `[WolverineIgnore]` を付ける。名前は変えない（呼び手・試験・文書の追随を増やさない）。
- `NotificationConsumerCoverageTests` が「通知の連鎖は `*NotificationHandler` の型だけ」を母集合ごと固定する（`DiscordCommandContext` は NoHandler）。

### 決定 2 — 通知は受ける表の型の発行元を確かめる（記録だけの受け手にはしない）

- `MessageOriginCatalog.Rules` の 7 型（`OrderExecuted`・`StopLossTriggered`・`OrderDispatchForgone`・`ProtectiveStopPlaced`・`ProtectiveStopCoverageLost`・
  `SoftwareStopExecuted`・`PositionDriftAdopted`）の受け手に `notification` を足す。要る鍵は 3 つの発行元の公開の鍵だけ（秘密は持たない）。
- 段 2 では確かめられない表の型を知らせない（偽の統制の事象を利用者へ出さない）。監査と違い、通知は捨てても台帳は欠けない。
- 表の外の型は発行元の印が付かないので確かめない。型ごとの対象外を並べる代わりに、`ExternalTypesUnverifiedServices` に理由つきで載せる。
  完全性の試験は、このサービスについて「受ける表の型 ＝ 確かめる型」だけを突き合わせる。
- `ExcludedServices` は空になる（型は残す。将来の対象外のため）。

### 決定 3 — 受け手を新しく足すときは、そのサービスだけ段を下げて入れる

- chart `global.messageOrigin.modeOverrides`（既定 `{}`）。`<サービス>: Report` で、そのサービスの `MessageOrigin__Mode` だけを下げる。値は Report か Enforce（他は描画で止める）。
- 鍵の Secret の参照の `optional` は global の `mode` のまま（段を下げても鍵の欠けを隠さない。クラウドの公開の鍵の Secret には既に全発行元の鍵がある）。
- values-cloud は `notification: Report`。通知のログに確かめられない記録（EventId 16261）が 1 日出ないことを確かめてから外す（手順書 §5.1）。
- 描画の検査（`helm.yml`）は、values-cloud の Report が通知の 1 つだけであること、下げを外すと全部 Enforce になることを固定する。

### 決定 4 — 所有者のトークンの azp の許可集合を、クラウドで明示する

- chart `global.ownerTokenBinding.clients`（既定 `[]`＝描かない＝コードの既定）。値があれば auth の全サービスへ `Auth__OwnerToken__Clients__<n>` で描く（既定を置き換える）。
- values-cloud は `[ai-stock-trading-owner]`。コードの既定（ローカルの MSP 連結で中継に使う名前を含む）は変えない。

### 決定 5 — gRPC 面の所有者の分岐も、REST と同じ段と許可集合で宛先を確かめる

- `GrpcOwnerAudienceRequirement`（`GrpcOwnerOrService` は `AllowService: true`・`GrpcOwnerOnly` は `false`）を両ポリシーに足す。
  判定器は `Auth:OwnerToken:Mode` と `Auth:OwnerToken:Audiences` を使う（段 1 は記録して通し、段 2 は通さない）。azp の門（`Auth:GrpcOwnerClients`）は従来どおり。
- REST の経路ごとのボットの許可表（IADR-0622）は gRPC 面へは当てない。gRPC 面の所有者の分岐は azp がボットのクライアントのときだけ通るので、
  面の全体がボットの許可表に当たる。
- サービスの分岐は変えない（サービスのトークンの宛先は IADR-0637 の段が認証で見る）。

## 検討した代替案

- 通知を記録だけの受け手にする（監査と同じ）: 確かめられない事象も知らせ続けるので、利用者を誤らせる面が残る。採らない。
- コマンドの処理の名前を変える: 呼び手・試験・文書の改名が要る。属性 1 つで足りる。採らない。
- コードの既定の azp の集合から中継の名前を消す: ローカルの MSP 連結の所有者の操作が止まる。クラウドの明示で足りる。採らない。
- 通知を最初から段 2 で入れる: 稼働中の通知が止まり得る。段を下げて入れる（決定 3）。

## 結果（影響）

- 通知はクラウドで 3 つの公開の鍵を読む（Pod の env が増える。鍵の Secret は既存）。描画の検査の数（発行元の段 10・前の鍵の受け手 4）を改めた。
- 基盤のブローカの権限・exchange の一覧に残る通知の `DiscordCommandContext` の行は、AST が使わなくなる。消すのは基盤の作業（害は無い）。

## 残る制約

- 通知の段 2 は、手順書の確かめの後に values-cloud の下げを外す作業が要る（オーナーの配備の作業）。
