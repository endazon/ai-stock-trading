---
title: 通知の受け口とメッセージの発行元の確かめ、所有者のトークンの許可集合と gRPC 面の宛先を硬くする
type: spec
status: done
related_ids: [NFR, NFR-06, FR-09, FR-10, FR-11, FR-14, FR-20, UC-06, ADR-0047, IADR-0611, IADR-0627, IADR-0633, IADR-0634, IADR-0637, IADR-0638]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06・FR-09・FR-10・FR-14)
---

# 仕様書: 通知の受け口・発行元の確かめ・所有者のトークンの硬化（NFR）

> 本仕様書は実装着手前に作成する。実装の判断は IADR-0638。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: NFR-06（利用者本人のみ）。機能要求: FR-09（通知）・FR-10（リスク統制）・FR-11（監査）・FR-14（Discord からの操作）・FR-20（段階ゲート）。
- ユースケース: UC-06（統制の操作）。関連 ADR: ADR-0047（ボットのトークンはサービスの身元・gRPC 面）。
- 起点: クラウドへの移行の前の点検の指摘（詳細は非公開）。利用者裁定（2026-11-07）「稼働前にすべて直す」。
- 硬化そのものの計画の番号は無いので、裸の `NFR` を併記する。

## 現状（着手前に develop `1e89cc106` で読んだもの）

1. 通知サービスの Discord のコマンドの処理（`Features/Notifications/*/…CommandHandler.cs` の 8 クラス）は、名前が Wolverine のハンドラの規則に当たり、
   `Program.cs` がアセンブリ全体を発見の対象にするため、`DiscordCommandContext` を受けるハンドラの連鎖が作られていた（試験で実測。下の AC-1）。
   入口は Discord の Gateway だけのはずである。
2. メッセージの発行元の確かめ（`MessageOriginCatalog`）: 監査は既に「記録だけの受け手」（`RecordOnlyServices`。IADR-0633 決定 5）で、
   表の型をすべて確かめ、確かめられないものを Warning とメトリクスに残している（監査の分は解消済み）。通知は `ExcludedServices` で何も確かめない。
   通知が受ける表の型は 7 つ（`OrderExecuted`・`StopLossTriggered`・`OrderDispatchForgone`・`ProtectiveStopPlaced`・`ProtectiveStopCoverageLost`・
   `SoftwareStopExecuted`・`PositionDriftAdopted`。発行元は発注執行・市場監視・リスク管理）。
3. 所有者のトークンの azp の許可集合の既定（`OwnerTokenBinding.DefaultClients`）に、MSP のローカルで所有者の操作を中継するクライアントの名前が残る。
   values-cloud は `Auth__OwnerToken__Clients__*` を描かない（コードの既定に頼る）。
4. gRPC 面の所有者の門（`GrpcOwnerOnly`・`GrpcOwnerOrService` の所有者の分岐）は、ロールと azp だけを見て、宛先（aud）を見ない。
   REST（`OwnerTokenBindingHandler`）は段 `Auth:OwnerToken:Mode` で aud も見る。
5. 空売りの実弾の解禁の判定（verdict）の記録は、既に第 2 要素の門を通る（`RequestStageTransition/Endpoint.cs` の `ShortSellReleaseVerdict` の分岐。
   IADR-0634 決定 5・`StageTransitionApproverTests` の段ごとの行）。**解消済み。本仕様書では変えない。**

## 母集合（規則 9・10。誤りの側の文字列で走査した結果）

- `ExcludedServices` / `notification` の参照: `MessageOriginCatalog.cs`・`MessageOriginCatalogCompletenessTests.cs`・`MessageOriginWiringTests.cs`
  （「通知は鍵が要らない」の行）。chart は `values.yaml` の `verifiers`・`helm.yml` の数（9 サービス・前の鍵の 3 サービス）。
- `DefaultClients`（所有者）の参照: `OwnerTokenBinding.cs`・chart README・`values.yaml` のコメント。
- `GrpcOwnerOnly` / `GrpcOwnerOrService` の登録: `AuthExtensions.cs` の 1 か所（各サービスの属性は名前で引くので変えない）。
- 除外: `.ai-context/specs/` の確定済みの記録（凍結）。

## 対象範囲

- 対象:
  - 通知の 8 つのコマンドの処理に `[WolverineIgnore]`。通知の連鎖の母集合を固定する試験。
  - 発行元の表に通知を受け手として足す（7 型）。表の外の型をまとめて確かめないサービスの一覧（`ExternalTypesUnverifiedServices`）。
  - chart: `verifiers.notification`・サービスごとの段の下げ（`global.messageOrigin.modeOverrides`）・values-cloud は `notification: Report`。
  - chart: `global.ownerTokenBinding.clients`（描くと既定を置き換える）。values-cloud は `[ai-stock-trading-owner]`。
  - gRPC 面の所有者の分岐に、REST と同じ段と aud の許可集合の確かめ（`GrpcOwnerAudienceRequirement`）。
  - 描画の検査（`helm.yml`）・chart README・手順書（`cloud-single-node-migration-runbook.md` §5.1）の追随。
- 対象外:
  - 第 2 要素の verdict（解消済み）。監査の発行元の記録（解消済み）。
  - 基盤（MSP）のブローカの権限・exchange の一覧（通知の `DiscordCommandContext` の exchange は、AST が作らなくなるだけで、基盤の側の行は残っても害は無い。消すのは基盤の作業）。
  - コードの既定の azp の集合（ローカルの MSP 連結の中継を止めない）。

## 受け入れ基準

- [x] AC-1 通知が Wolverine に持つ連鎖は `*NotificationHandler` の型だけで、`DiscordCommandContext` の連鎖は無い（invoker は NoHandler）。陰性対照: 属性を外すと赤（実測）。
- [x] AC-2 通知は受ける表の型をすべて確かめる受け手で、記録だけではない。段 2 では印の無い表の型を処理せず、正しい印は処理する。段 1 は記録して処理する。
- [x] AC-3 表の受け手の完全性の試験は、通知について「受ける表の型 ＝ 確かめる型」を突き合わせる。陰性対照: 表の行から通知を外すと赤（実測）。
- [x] AC-4 通知に要る鍵は公開の鍵 3 つ（発注執行・市場監視・リスク管理）だけで、秘密の鍵は要らない。chart の `verifiers` と一致する。
- [x] AC-5 values-cloud の発行元の段は通知だけが `Report`（他は `Enforce`）。下げを外すと全部 `Enforce`。下げに知らない値は描画で止まる。鍵は段 2 のまま必須。
- [x] AC-6 values-cloud は auth の全サービスに `Auth__OwnerToken__Clients__0=ai-stock-trading-owner` だけを描く。既定・values-local は描かない。名前の形でない値は描画で止まる。陰性対照: 値を消すと検査が赤（実測）。
- [x] AC-7 gRPC 面の所有者の分岐は、段 2 で宛先の無い・許可集合に無いトークンを通さず、段 1（既定）は記録して通す。サービスの分岐は変わらない。陰性対照: 要件を外すと赤（実測）。
- [x] AC-8 `dotnet test backend/backend.slnx`（フィルタなし）・format・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・helm の描画の検査が通る。

## 稼働への影響と段

- 通知の確かめは、values-cloud で段 1（記録だけ）から入れる。通知のログに確かめられない記録（EventId 16261）が 1 日出ないことを確かめてから下げを外す（手順書 §5.1）。
  鍵はクラウドの既存の公開の鍵の Secret にあり（監査が同じ Secret を読む）、新しい秘密は要らない。
- 所有者の azp の明示: クラウドで所有者の操作を送るのはボットのクライアントだけなので、通る呼び出しは変わらない。
- gRPC 面の宛先: クラウドでは gRPC の口が開いておらず、サービスのトークンの段（Enforce）が認証で宛先を既に確かめている。ローカルの既定は段 1。
