---
title: IADR-0097 kill switch 解除にも確認フレーズ検証を要求し、起動と同一の Verify・モーダル導線・安全既定（未設定は解除も拒否）へ揃える
type: impl-adr
status: Accepted
related_ids: [FR-14, UC-06, ADR-0009, IADR-0062, IADR-0063, IADR-0075, IADR-0081]
author: endazon (with Claude Code)
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/07_discord-bot-design.md
---

# IADR-0097: kill switch 解除にも確認フレーズ検証を要求する（起動と同一の仕組み・安全既定は解除も拒否）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-20
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-14**（双方向 Bot による運用操作）、**UC-06**（kill switch）、**ADR-0009**（Discord 運用 Bot・
  裁定 [project-planning#35](https://github.com/endazon/project-planning/issues/35) / 計画修正 PR
  [project-planning#37](https://github.com/endazon/project-planning/pull/37)）。
- 対象 Issue: [#223](https://github.com/endazon/ai-stock-trading/issues/223)（kill switch 解除に確認フレーズ検証を追加）。
- 関連 IADR: [IADR-0062]（双方向 Bot・多層認証・確認フレーズの導入）、[IADR-0063]（Bot 常駐）、[IADR-0075]（pause/resume）、
  [IADR-0081]（段階ゲート Bot コマンド）。

## コンテキストと課題

planning 裁定（project-planning#35）により、**kill switch は起動・解除の双方で「確認ボタン＋確認フレーズ」を要する**ことが
確定した。これは計画文書間の不整合が原因である: `07_discord-bot-design.md` は「kill switch は確認フレーズを要求する」と述べる
一方、同文書に「解除のみ確認ステップを追加する」（＝ボタンのみで足りると読める）記述が併存していた。実装は後者に沿って解除を
**確認ボタンのみ**（フレーズ検証なし）としており、裁定後の計画と乖離する。

kill switch の解除は「全取引停止の解除」＝実弾方向へ戻す高リスク操作であり、起動と同等の摩擦（確認フレーズ）を課すのが安全側。
実装（[IADR-0062]）は起動時のみ `KillSwitchConfirmation.Verify` を通し、解除は素通しだった。

## 決定

### 決定1: 解除にも起動と同一の `Verify` を適用する（新しい検証系を作らない）

`KillSwitchCommandHandler` の閂3（確認ステップ）を `command.Kind == KillSwitchEngage` 限定から外し、**起動・解除の双方**で
`KillSwitchConfirmation.Verify(confirmationPhrase, options)` を実行する。フレーズ不一致・未入力・未設定は `Denied` を返し
`controller.DisengageAsync` を呼ばない（誤爆防止の閂を解除方向にも掛ける）。検証の実装（前後空白・大小文字のみ正規化の
厳密一致）は起動と共有し、解除専用のロジックや別フレーズは設けない（設定・挙動の単一化）。

### 決定2: 安全既定は解除も「拒否」に倒す（未設定＝フレーズ不要にしない）

`KillSwitchConfirmationPhrase` 未設定時、解除も**拒否**する（起動と同じ）。「解除は摩擦を下げない」＝設定漏れで解除の閂が
外れないようにする。これに伴い `KillSwitchConfirmation.Verify` の未設定時の理由文言を操作中立へ一般化する
（「確認フレーズが未設定のため**起動しない**（安全既定）」→「…**実行しない**（安全既定）」）。文言のみの変更で検証挙動は不変。

**トレードオフ**: フレーズ未設定の運用者は `/killswitch off` で解除できなくなる。だが Bot 経由の kill switch はそもそも
フレーズ設定が前提（未設定なら起動もできない＝Bot での停止/解除運用が成立しない構成）であり、解除だけを緩めるのは
非対称で危険。Risk 側の直接 API・運用手順による解除経路は別途存在するため、Bot 経路を安全側に固定しても復旧性は損なわれない。

### 決定3: Gateway は解除も「ボタン→モーダル→フレーズ入力」の導線へ揃える

`DiscordNetBotGateway` の解除確認ボタン（`KillSwitchDisengageButtonId`）押下で即実行していたものを、起動と同じく**モーダル**
（確認フレーズ入力）提示に変える。起動用モーダル ID（`ast-killswitch-modal`）と解除用モーダル ID
（`ast-killswitch-disengage-modal`）を分離し、`OnModalAsync` が ID により `/killswitch`（起動）と `/killswitch off`（解除）を
出し分けて `HandleAsync(context, phrase)` を呼ぶ。認証はモーダル送信時（＝最終実行時）に Handler 側で再評価される
（押下者すり替え防止・既存の作法を踏襲）。

**なぜモーダル ID を分けるか**: 単一モーダルに操作種別を埋め込むと、モーダル送信時に「起動なのか解除なのか」を再解析する
必要が生じ、CustomId パースの分岐が増える。ID を分離すれば送信ハンドラは ID 一致だけで種別を確定でき、誤って別操作を
実行する余地が構造的に無い（段階ゲートが CustomId に遷移先を載せるのとは異なり、kill switch は 2 値のため ID 分離が単純）。

### 決定4: 監査・冪等は既存経路を踏襲（新規台帳・新イベントを作らない）

解除の**拒否**は Handler の `LogWarning`（操作種別で「起動 / 解除」を出し分け）に残す（Risk を呼ばない＝台帳に載らないのは
起動拒否と同じ）。解除の**実行**は `controller.DisengageAsync` 経由で Risk 側台帳（SettingsChangeLog）に記録される。冪等性
（フレーズ一致後、既に解除済みなら現状態を返すのみ）は controller 側で維持され、Handler は変更しない。`Shared.Contracts` は
不変・新イベント無し。

## 影響・波及

- 変更（追加なし）: `KillSwitchCommandHandler.cs`（閂3を両種別へ）／`KillSwitchConfirmation.cs`（未設定文言の一般化）／
  `DiscordNetBotGateway.cs`（解除もモーダル導線・モーダル ID 分離）。
- テスト: `KillSwitchCommandHandlerTests`（解除のフレーズ一致/不一致/未入力/未設定/冪等を追加、旧「フレーズなしで解除」を反転）／
  `KillSwitchConfirmationTests`（未設定文言の追随）。
- `Shared.Contracts`・realm-export.json・helm values（OwnerAuth secret 配線）は不変（後者は別 issue の領域）。

## 代替案と却下理由

- **解除は確認ボタンのみのまま（現状維持）**: 裁定 project-planning#35 に反する。却下。
- **解除用に別フレーズ・別検証を新設**: 設定・実装が二重化し運用が複雑化。単一フレーズで足りる。却下。
- **未設定時は解除だけ許可（摩擦を下げる）**: 解除は実弾方向へ戻す高リスク操作。非対称な緩和は危険（決定2）。却下。
- **モーダルを単一 ID のまま操作種別を埋め込む**: 送信時の再解析で分岐が増え誤実行の余地。ID 分離が単純で安全（決定3）。却下。
