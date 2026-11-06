---
title: Discord ボットが kill switch の解除に「確認を出したときに照会した版」を添える（IADR-0620 の残余 1 のボットの側）
type: spec
status: accepted
related_ids: [FR-10, FR-14, UC-06, ADR-0009, IADR-0620, IADR-0613, IADR-0615, IADR-0622, IADR-0097, IADR-0450]
author: claude (Claude Code)
created: 2026-11-06
updated: 2026-11-06
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10・FR-14)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (§認証・認可 4 の確認ステップ)
---

# 仕様書: ボットが kill switch の解除に版を添える（#1561）

## 起点

- #1561。IADR-0620 の残余 1「Discord ボット（gRPC）と画面（BFF）が版を送るよう改めること」のうち、**ボットの側**。
- #1560（クラウドへの移行）の段 2 の表で、`RiskControls:KillSwitch:Precondition=Require` を入れる前提として要る。**この PR は値を変えない**（段 1 のまま）。`Require` はクラウドの値の PR で入れる。

## 決定（IADR-0620 の追記 2026-11-06）

1. **版は稼働状態の照会から読む。** REST の `GET /risk-controls/status` に `killSwitchVersion`、gRPC の `RiskControlsOwnerRead/GetRiskStatus` に `optional string kill_switch_version = 16` を足す（`GET /risk-controls/kill-switch` の `version` と同じ値）。`GET /kill-switch` はボットの許可表（IADR-0622）に無く、ボットの gRPC にも口が無いので、許可表にある照会へ載せる（経路を増やさない）。値は `PortfolioSnapshot` を組むときの kill switch の状態から写す。
2. **gRPC の解除は `optional string expected_version = 3`（`KillSwitchChangeRequest`）を受け**、REST と同じ `Disengage` へ渡す（同じ順で判定する）。起動にも同じ欄が届けば同じく渡す（REST の起動と同じ。ボットは起動には載せない）。
3. **gRPC の拒否は REST の本文の `code` を末尾の metadata `ast-error-code` で運ぶ。** 状態は従来の写し（409 → `ABORTED`）に、**428 → `FAILED_PRECONDITION`** を足す。これで保持（`operator_hold`）と版の競合（`version_conflict`）を、同じ `ABORTED` でも見分けられる。
4. **ボットの流れ**: `/killswitch off` は、多層認証を通ったら応答を予約し（照会は応答の期限 3 秒を越え得る）、版を照会して確認のボタンの ID に載せる（`ast-ks-off-b-<版>`）。ボタン → 確認の画面（`ast-ks-off-m-<版>`）→ 送信で解除の要求へ版を添える。**照会に失敗したら版なしのボタン（従来の ID）**＝段 1 では従来どおり通る。版の形（10 進の数字・19 文字まで）でない ID は振り分けない（版を推測しない）。起動は従来どおり（版を照会しない・添えない）。
5. **口は既定の実装つきの多重定義で足す**（#1552 と同じ）。`IKillSwitchController.DisengageAsync(reason, secondFactorCode, expectedVersion)` は既定で版を捨てて従来の口へ、`GetVersionAsync` は既定で null。`Http*`・`Grpc*` は必ず上書きする。REST の本文の `expectedVersion` は null なら欄ごと省く（本文は従来と同じ）、proto は入れたときだけ立てる。
6. **拒否の文言**: 版の競合は「確認を出した後に状態が変わった・`/killswitch off` からやり直す」、版なし（段 2）は「版を確かめられなかった・やり直す」。保持は従来の文言。印の無い解除の `ABORTED` は、印を送らない旧版の提供側の保持の拒否として従来どおり読む（配備順の窓）。
7. **ハンドラ**は版を解除にだけ渡す。閂（多層認証・確認フレーズ）で止まったら提供側を呼ばない。版の照会（`GetDisengageVersionAsync`）にも多層認証を掛ける（許可外へ状態を漏らさない）。

## 段 1 で害が無いこと

- 版を送らない呼び手（画面・旧版のボット）と、照会に失敗したボットの挙動は変わらない（`Report` は版なしを受ける）。
- 版を送るボットは、確認を出した後に状態が変わっていれば 409 で拒まれ、何も変わらない（これが目的。利用者はやり直す）。

## 母集合（規則 9）

- `kill_switch_version`・`expected_version` を運ぶ面: proto 2 本（`risk_controls_owner_read.proto`・`risk_controls_owner_write.proto`）、Risk の `RiskStatusView`・`RiskStatusService`・`RiskReadWireMapping`・`PortfolioSnapshot`・`PortfolioSnapshotBuilder`・`RiskControlsOwnerWriteGrpcService`。
- ボットの面: `IKillSwitchController`・`HttpKillSwitchController`・`GrpcKillSwitchController`・`NotificationGrpcCalls`（末尾の印）・`KillSwitchCommandHandler`・`DiscordConfirmationModals`・`DiscordNetBotGateway`。
- 拒否の印 `ast-error-code` を読む gRPC の口は kill switch の解除だけ（他の書き込みの `ABORTED` の扱いは変えない）。
- 試験の偽物（`IKillSwitchController` を実装する偽物）は既定の実装に委ねるため無改修。
- フロントの契約: `GET /risk-controls/status` の応答に欄が増えるため、契約フィクスチャ `frontend/src/testing/contract-fixtures/risk-controls.status.json` を再生成し、`frontend/src/lib/risk/contracts.ts` の `RiskStatusView` に任意の欄 `killSwitchVersion` を足す（画面はまだ使わない）。
- サービス間の契約テスト（IADR-0420）: ボットの `HttpKillSwitchController.GetVersionAsync` は送り手（リスク管理）の照会を読む新しい単位になるため、`RiskControlOperationReadContractTests` に送り手の本物の型で読む試験を足す（逆向きの解除の本文も）。
- 文書: `docs/functional/FR-10_risk-controls.md`（kill switch の前提条件の節）・`docs/api/east-west-grpc.md`（エラーの表）。
- **除外**: 画面（BFF）が版を送る作業（IADR-0620 残余 1 の画面の側）と、残余 3（照会・要求の DTO を `KillSwitchState` から切り離す）。どちらもこの PR の範囲外。

## 受け入れ基準

- [x] 稼働状態の照会（REST・gRPC）が kill switch の版を返し、`GET /kill-switch` の版と同じで、書くたびに進む（`KillSwitchVersionForBotTests`）。
- [x] gRPC の解除は、見た版が今の版なら解除し、古ければ `ABORTED`＋`version_conflict` で何も変えない。保持は `ABORTED`＋`operator_hold`。段 2 の版なしは `FAILED_PRECONDITION`＋`precondition_required`（同上）。
- [x] ボットは解除にだけ版を載せ（REST の `expectedVersion`・proto の `expected_version`）、版なし・起動では欄を送らない（`KillSwitchVersionTransportTests`）。
- [x] 照会に失敗したら版は null（例外にしない）。版の形でない値は null。
- [x] 拒否の印で文言を分け、REST と gRPC で同じ結果になる（同上）。
- [x] 版つきの確認の画面だけが版を運び、書式外の ID は振り分けない。ID は上限 100 文字に収まる（`DiscordConfirmationModalsTests`）。
- [x] 送り手の本物の稼働状態の型から版を読め、ボットの解除の本文は送り手の本物の要求型で版まで読める（`RiskControlOperationReadContractTests`。`CrossServiceReadContractTests` が緑）。
- [x] フロントの契約フィクスチャと型が応答に追随している（`FrontendContractFixtureTests`・フロントの typecheck と単体試験）。
- [x] 既存の試験は無改修で緑のまま（試験の偽物は既定の実装に委ねる）。

## 範囲外

- `RiskControls:KillSwitch:Precondition=Require`（クラウドの値の PR。#1560）。
- 画面（BFF）が版を送ること。DTO の切り離し（IADR-0620 残余 3）。
- 実 Discord での確認（#570）。
