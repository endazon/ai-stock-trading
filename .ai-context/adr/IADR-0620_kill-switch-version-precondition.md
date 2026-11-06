---
title: IADR-0620 kill switch の発動・解除は「見た状態」の版（書くたびに 1 つ進む単調増加の値）を前提条件に取り、違えば 409 で何も変えない。段 1 は前提条件なしも受け、段 2 は前提条件なしの解除を拒否する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-14, FR-13, ADR-0009, ADR-0003, IADR-0012]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-06
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10・FR-14)
---

# IADR-0620: kill switch の前提条件

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-05
- 決定者: Claude Code（実装）／ オーケストレーター（kill switch の解除・変更に見た状態の前提条件を付け、古い状態を見た操作が上書きしないようにする、の指示）

## 起点・関連

- issue: #1534
- 前提: IADR-0012（設定の楽観排他。Version 列）。
- 実装仕様書: [`.ai-context/specs/20261105_1534_kill-switch-precondition.md`](../specs/20261105_1534_kill-switch-precondition.md)

## コンテキストと課題

kill switch の発動・解除は現在の状態を見ずに上書きする。利用者が画面や Discord で「発動中」を見て解除を決める間に、別の操作（別の端末・撤退の自動発動）が入っても、解除はそのまま通る。

## 検討した選択肢

1. 版を「最後に変えた時刻の tick」で表す — 列を足さずに済むが、時計の戻り・同じ tick の 2 回の書き込みで同じ版が再び現れ（ABA）、古い状態を見た操作が通る。採らない（レビューで指摘・2026-11-06 に改めた）。
2. **版の列（`bigint`・書くたびに 1 つ進む・並行トークン）を足し、比較と書き込みをストアで原子的に行う（採用）** — 移行は列の追加 1 本（既存行は 0）。
2b. Postgres の `xmin` を並行トークンにする — 列は要らないが、値はトランザクション ID で単調でなく、インメモリの実装と版の意味が揃わない。採らない。
3. 前提条件を即座に必須にする — 版を送らない呼び手（Discord ボット・既存の画面）の解除が全部止まる。段で切り替える。

## 決定

1. `KillSwitchState.Version`（版の列 `kill_switch.Version` の文字列・未操作と既存行は `"0"`）。版はストアが書くたびに 1 つ進める（渡された状態の値は使わない）。`GET /kill-switch` は本文と `ETag` で返す。
2. `KillSwitchRequest.ExpectedVersion`（本文）または `If-Match`。渡されたら `IKillSwitchStore.TrySetState(状態, 版)` で比較と書き込みを原子的に行い、違えば `KillSwitchVersionConflictException`（409・本文の `code` は `version_conflict`）。EF は直列化可能な取引と並行トークン、インメモリは錠。**「版が違う」として扱うのは、比較の不一致・直列化の失敗（SQLSTATE 40001）・デッドロック（40P01）・楽観排他の失敗（`DbUpdateConcurrencyException`）・行の無い状態での並行の初回の作成の主キー違反（23505）だけ**で、接続断・タイムアウト・その他の DB の失敗は再送出する（5xx。DB の障害を「状態が変わった」と見せない）。競合で false を返すときは、失敗した書き込みの追跡を外す。
2a. `If-Match` は版を 1 つだけ受ける。`*`・複数の値（カンマ区切り・ヘッダーの重複）は解釈せず 400（`code` は `if_match_unsupported`）。弱い印 `W/` と引用符は外す。428 の本文も `code`（`precondition_required`）を持つ。
2b. **前提条件なしの書き込み（段 1 の発動・解除、gRPC、撤退の自動発動）は、最後の書き込みが勝つ従前の挙動を保つ。** 版の列が並行トークンなので並行の他の書き込みと競合し得るが、そのときは読み直して書き直す（`EfKillSwitchStore.MaxWriteAttempts` = 5 回まで。競合と同じ分類の失敗だけを再試行し、上限を超えたら例外を返す＝5xx）。発動が並行で失われない。
2c. **SSH の経路の保持（IADR-0613）の間の解除は、版の前提条件より先に保持で拒否する**（409・`code` は `operator_hold`。版が合っていても古くても無くても、段 2 でも同じ）。同じ 409 でも `code` で版の競合（`version_conflict`）と区別できる。保持による起動は前提条件なしの書き込みとして版を進める。
2d. **第 2 要素（IADR-0615）との順**: 解除は「保持の拒否 → 段 2 の版なし（428）→ 版の不一致（409・門の手前の早期の判定）→ 第 2 要素の門 → 原子的な比較と書き込み」の順で判定する。前提条件で拒否される解除は第 2 要素のコードを消費しない。要求の `ExpectedVersion` は `SecondFactorCode` の後ろ、`Disengage` の `expectedVersion` は `beforeCommit` の後ろに置く（位置で渡す既存の呼び手を変えない）。
3. `RiskControls:KillSwitch:Precondition` = `Report`（既定）/ `Require`。`Require` は前提条件の無い解除を `KillSwitchPreconditionRequiredException`（428）で拒否する。発動は求めない。知らない値は起動で止める。

## 結果

- 段 1 の既定では、前提条件を送らない呼び手の挙動は変わらない（記録が増える）。送る呼び手は古い状態での上書きを防げる。

## 残余・フォローアップ

1. Discord ボット（gRPC）と画面（BFF）が版を送るよう改めること。段 2 の前提（オーナーの判断で上げる）。
2. （2026-11-06 に解消）時刻の tick を版にしていたときの ABA は、単調増加の版の列で塞いだ。
3. `KillSwitchState.Revision` は照会の JSON に出さない（`JsonIgnore`。版は `version` の文字列だけを見せる）。残余 1（画面・ボットが版を送る作業）のときに、照会・要求の DTO を `KillSwitchState` から切り離し、内部の値と公開の形を分ける。

## ［2026-11-06 追記 / #1561］残余 1 のボットの側を行った

- **ボットは解除の確認を出すときに版を照会し、確認の画面の送信で解除の要求へ添える。** 版は稼働状態の照会（REST の `GET /risk-controls/status` の `killSwitchVersion`・gRPC の `GetRiskStatus` の `kill_switch_version`）から読む（`GET /kill-switch` はボットの許可表〔IADR-0622〕に無いため、許可表にある照会へ載せた）。照会に失敗したら版なしで送る（段 1 では従来どおり通る）。起動には添えない。
- **gRPC の解除は `expected_version` を受け**、REST と同じ順（決定 2d）で判定する。決定 2b の「前提条件なしの書き込み（gRPC）」は、版が無いときだけに当たる。
- **gRPC の拒否は末尾の metadata `ast-error-code` に REST の本文の `code` を載せる**（`operator_hold`・`version_conflict` は `ABORTED`、`precondition_required` は `FAILED_PRECONDITION`＝REST の 428 の写しを足した）。
- 詳細は仕様書 [`20261106_1561_bot-kill-switch-version`](../specs/20261106_1561_bot-kill-switch-version.md)。**残余 1 のうち画面（BFF）の側と残余 3 は残る。** 段 2（`Require`）はクラウドの値の変更（#1560）で入れる。
