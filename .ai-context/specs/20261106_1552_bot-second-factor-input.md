---
title: ボットの確認の画面に任意の 6 桁の入力を足し、取引を再開・緩める操作で提供側へ運ぶ（#1542 の続き）
type: spec
status: accepted
related_ids: [NFR-06, FR-14, FR-10, FR-19, FR-20, UC-06, ADR-0047, ADR-0009, IADR-0615, IADR-0097, IADR-0062, IADR-0450]
author: claude (Claude Code)
created: 2026-11-06
updated: 2026-11-06
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06・FR-14)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (§認証・認可 4 の確認ステップ)
---

# 仕様書: ボットの 6 桁の入力（#1552）

## 起点

- #1552。IADR-0615 のフォローアップ「ボットの確認の画面に 6 桁の入力を足す（7 操作）」。段 2（Enforce）へ上げる前に、段 1 の記録で 7 操作とも `Verified` が出ることを確かめるために要る。
- #1545（#1542 の段 1）のレビューで残った軽微な点（🟢1〜5）を同じ PR で直す（下の「#1545 のレビューの残り」）。

## 決定（IADR-0615 の追記 2026-11-06）

1. **確認の画面（モーダル）に任意の欄「認証アプリの 6 桁」**（`ast-second-factor-code`。`required: false`・6 文字ちょうど）を足す。対象は 7 操作だけ。止める向き（kill switch の起動・一時停止・差し戻し）の画面には足さない（ADR-0009 の非対称）。
   - 既にモーダルがある 3 操作（kill switch の解除・GFV 停止の解除・乖離の取り込み）は、既存の欄の下に足す。
   - **ボタンだけで実行していた 4 操作（再開・昇格・報告書の確定・方針の確定）は、ボタンでモーダルを開き、送信で実行する。** モーダルの ID は操作ごとに分け（IADR-0097 の規律）、対象（遷移先・期間キーと版番号）はボタンの CustomId から写す。モーダルの接頭辞はボタンの接頭辞より短くする（CustomId の上限 100 文字に必ず収まる）。
   - 送信後は、従来のボタンの経路と同じく元のメッセージを結果で差し替えてボタンを取り除く（`SocketModal.DeferAsync` はボタンから開いたモーダルでは「元のメッセージの更新」の予約）。差し替えが拒まれたら結果を追送する（実 Discord での確認は #570 の範囲で未了）。
   - モーダルの組み立ては `DiscordConfirmationModals` へ切り出す（Socket* は試験で作れないが Modal は作れる＝欄の有無を試験で固定する）。
2. **ボットは 6 桁を確かめない。** 前後の空白を落とし、空なら null（欄ごと送らない）。数字かどうかも見ない（確かめるのは提供側の門。ボットで弾くと「届かなかった失敗」が記録に残らない）。**ログ・応答に出さない。** `DiscordCommandContext`（record）には載せず、ハンドラの任意の引数で渡す（record の `ToString` やログの構造化引数へ紛れ込ませない）。
3. **口（6 つの controller）は既定の実装つきの多重定義で足す**（`string? secondFactorCode` を取る形。既定の実装はコードを捨てて従来の口へ委ねる＝口を実装する試験の偽物を壊さない）。`Http*` と `Grpc*` は必ず上書きし、REST の本文の `secondFactorCode`（null は `JsonIgnore(WhenWritingNull)` で欄ごと省く＝本文は従来と同じ）と proto の `optional second_factor_code`（入れたときだけ立てる）で運ぶ。方針の確定は報告書の確定の口を通る（提供側が版から `PolicyConfirm` か `ReportConfirm` かを決める）。
4. ハンドラは、昇格にだけ渡す（差し戻しには null）。再開にだけ渡す（一時停止には渡さない）。解除にだけ渡す（起動には渡さない）。閂（多層認証・確認フレーズ・理由）で止まったときは提供側を呼ばない＝コードも送らない。
5. **段 2 で拒まれたときの専用の文言は、この PR では足さない**（段 1 は拒まない）。いまの表示は認可の失敗と同じ汎用の文言で、段 2 へ進める変更で足す（手順書に明記）。

## 母集合（規則 9）

- **口**: `IKillSwitchController.DisengageAsync`・`IPauseController.ResumeAsync`・`IStageGateController.RequestTransitionAsync`・`IGoodFaithViolationController.ClearAsync`・`IPositionDriftAdoptionController.AdoptAsync`・`IReportReviewController.ConfirmAsync`（`/report approve` と `/policy` の確定）。実装は各 `Http*`・`Grpc*` の 12 本。
- **ハンドラ**: `KillSwitchCommandHandler`・`PauseCommandHandler`・`StageGateCommandHandler`・`GoodFaithViolationCommandHandler`・`PositionDriftAdoptionCommandHandler`・`ReportCommandHandler`・`PolicyApprovalCommandHandler`（`ReportCommandHandler` へ委ねる）。
- **画面**: `DiscordNetBotGateway` の `OnButtonAsync`（7 操作のボタン）と `OnModalAsync`（送信の受け取り）。止める向きのボタン（起動・一時停止・差し戻し）は従来どおり。
- **試験の偽物**: 口を実装する偽物（`DiscordSettingsAreReadOnlyTests`・`SecretRedactionTests`・`DiscordBotGatewayFactoryTests`・各ハンドラの試験・`PolicyFakes` ほか）は既定の実装に委ねるため無改修（既存の試験は無改修で緑のまま）。
- **除外**: `/report approve` の slash（版番号なし）は照会だけでボタンを出す（確定はボタン → モーダル）。`/policy` の slash も案を作るだけ。どちらも確定の口を呼ばない。

## #1545 のレビューの残り（🟢1〜5）

1. 仕様書 `20261105_1542` の決定 2・3・5 の古い記述 → 同仕様書の末尾へ日付つき追記（決定 2・3 は決定 7 で置き換わった。決定 5 は values-cloud.yaml にもある）。本文は書き換えない（凍結記録）。
2. 件数（`ast-owner-totp-enroll.test.sh 19 件`）は導出値で腐る → 同じ追記で「件数は書かない」とし、本仕様書・PR 本文にも件数を書かない。
3. `Replayed` も連続失敗に数える → IADR-0615 の追記で決定 3 に明記（意図どおり。正当な利用者の二重送信でも 1 増えるが、5 回でロックのため実害は小さい）。手順書にも書く。
4. 段 1 の Warning の量 → 手順書の注意を「`Verified` 以外は毎回 Warning」に改め、IADR-0615 の追記にも書く。
5. 正しいコードで通る試験が kill switch の解除だけ → `OwnerSecondFactorAcceptedRequestTests` で、**受理される要求の土台**（昇格: バックテスト合格を仕込む／GFV: 違反の記録を 1 件仕込む／乖離: 台帳の建玉と 0 株の観測を 2 回仕込む）を作り、Enforce で正しいコードなら通る・無い／違うなら 403 で何も変えないことを固定する。

## 受け入れ基準

- [x] 7 操作の確認の画面に任意の 6 桁の欄があり、入れた値が提供側へ届く（REST・gRPC の両方）。`DiscordConfirmationModalsTests`・`SecondFactorCodeTransportTests`・`SecondFactorCodeHandlerTests`。
- [x] 止める向きの操作の画面には欄が無く、口にもコードを載せない。
- [x] 入力が空なら、従来どおりコードなしで送る（REST の本文に欄が無い・proto の欄が立たない）。
- [x] コードはハンドラのログに出ない。
- [x] 段階の昇格・GFV 停止の解除・乖離の取り込みで、Enforce でも正しいコードなら通る。無い・違うなら 403 で何も変えない。

## 範囲外

- 段 2（Enforce）への切り替えと、拒まれたときの専用の文言。
- ボットの権限の分割（IADR-0615 決定 7 の B・C）。
- 実 Discord での確認（#570）。
