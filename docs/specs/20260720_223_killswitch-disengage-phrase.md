---
title: kill switch 解除（/killswitch off）に確認フレーズ検証を追加する
type: work
status: done
related_ids: [FR-14, UC-06, ADR-0009]
author: endazon (with Claude Code)
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/07_discord-bot-design.md
---

<!-- 注: 裁定 ADR-0009 は計画 PR project-planning#37 で新設される（planning サブモジュール同期前のため
     ファイル参照は張らない）。出所は下記 issue/PR（外部リンク）で辿れる。 -->


# 作業仕様書: kill switch 解除（`/killswitch off`）に確認フレーズ検証を追加する

> Issue [#223](https://github.com/endazon/ai-stock-trading/issues/223)（planning 裁定
> [project-planning#35](https://github.com/endazon/project-planning/issues/35) / ADR-0009 /
> 07_discord-bot-design・計画修正 PR [project-planning#37](https://github.com/endazon/project-planning/pull/37)）を対象とする。
> `Closes #223`。設計判断は [IADR-0097](../adr/IADR-0097_killswitch-disengage-confirmation-phrase.md)。

## 背景 / 出所

planning 裁定により、**kill switch は起動・解除の双方で「確認ボタン＋確認フレーズ」を要する** ことが確定した。
これは計画文書間の不整合（07_discord-bot-design が「解除のみ確認ステップを追加する」＝ボタンのみ、と読める記述と、
「kill switch は確認フレーズを要求する」という記述が併存）が原因であり、実装の無断逸脱ではない。本 issue で解除経路にも
フレーズ検証を追加し、計画（裁定後）と整合させる。

## 前提の確認結果（着手前調査・実コードで裏取り）

- `KillSwitchCommandHandler.HandleAsync`（`KillSwitchCommandHandler.cs`）の閂は 3 段: (1) 多層認証 → (2) コマンド解析
  （kill switch 種別のみ許可）→ (3) **起動時のみ**確認フレーズ検証 → Risk 呼び出し。**解除（`KillSwitchDisengage`）は
  フレーズ検証を経ずに `controller.DisengageAsync` を呼ぶ**（`:46-57`・`:63-65`）。
- フレーズ検証は `KillSwitchConfirmation.Verify(entered, options)`（純関数）。未設定なら拒否（安全既定）・前後空白と
  大小文字のみ正規化。未設定時の理由文言は「確認フレーズが未設定のため**起動しない**（安全既定）」＝起動限定の文言。
- Gateway（`DiscordNetBotGateway.cs`）の解除フロー: `/killswitch off` → 認証 → 解除確認ボタン提示（`KillSwitchDisengageButtonId`）
  → 押下で **即実行**（`OnButtonAsync` が `HandleAsync(..., confirmationPhrase: null)` を呼ぶ・`:347-356`）。
  起動フローは 押下で**モーダル**（`KillSwitchModalId`）を提示しフレーズ入力 → `OnModalAsync` が `HandleAsync(context, phrase)`
  を呼ぶ（`:336-345`・`:401-416`）。モーダルは単一 ID で `/killswitch`（起動）にハードコードされている。
- 監査経路: 拒否は Handler の `LogWarning`（Risk を呼ばない＝台帳に載らない）。実行（起動/解除）は `controller` 経由で
  Risk 側の台帳（SettingsChangeLog）に記録される。→ **解除の確認/拒否も同じ経路**（拒否=LogWarning／実行=Risk 台帳）で整合。
- 既定挙動（Enabled=false・GuildId 等未設定・AllowedUserIds 空・フレーズ空）はすべて「拒否」側（`DiscordBotOptions` の安全既定）。

## 課題

解除（`/killswitch off`）で、起動と同一の仕組み（`KillSwitchConfirmation.Verify`）により確認フレーズの入力・検証を
必須とする。フレーズ不一致・未入力・**未設定**は解除を拒否し Risk を呼ばない。冪等性（既に解除済みなら状態を返すのみ）は
controller 側で維持する。起動側の既存挙動は不変。

## 受け入れ基準（issue 由来）

- [x] `/killswitch off` で、起動時と同様に確認フレーズの入力・検証が必須になる。
- [x] フレーズ不一致・未入力では解除しない（Risk の `DisengageAsync` を呼ばない）。
- [x] フレーズ未設定では解除しない（安全既定・解除も摩擦を下げない）。
- [x] 冪等性は維持する（フレーズ一致後、既に解除済みなら controller が現状態を返すのみ）。
- [x] 起動側の挙動は不変（既存テスト緑）。
- [x] 監査記録（解除の確認/拒否）が既存経路に整合（拒否=LogWarning／実行=Risk 台帳）。

## 実装方針

1. **Handler**（`KillSwitchCommandHandler.cs`）: 閂3のフレーズ検証を **起動・解除の双方**に適用する。
   `command.Kind == Engage` 限定の分岐を外し、`Verify` を両種別で実行する。拒否ログは操作種別で「起動 / 解除」を
   出し分け（例:「kill switch 解除を確認ステップで拒否しました（Actor=...・理由=...）」）。拒否時は `Denied` を返し
   Risk を呼ばない。以降の冪等な `Engage/Disengage` 呼び出しは不変。
2. **確認フレーズ純関数**（`KillSwitchConfirmation.cs`）: 未設定時の理由文言を操作中立に一般化する
   （「確認フレーズが未設定のため**起動しない**」→「…**実行しない**」）。検証ロジック（正規化・厳密一致）は不変。
3. **Gateway**（`DiscordNetBotGateway.cs`）: 解除確認ボタン押下で即実行せず、起動と同じく**モーダル**を提示する。
   起動用と解除用でモーダル ID を分離（`ast-killswitch-modal` = 起動 / `ast-killswitch-disengage-modal` = 解除）し、
   `OnModalAsync` で ID により `/killswitch`（起動）と `/killswitch off`（解除）を出し分けて `HandleAsync(context, phrase)`
   を呼ぶ。スラッシュコマンド登録の説明文（「確認ボタンと確認フレーズが必要」）は起動・解除双方に該当するため不変で足りる。

## テスト（TDD・受け入れ基準の写像）

- Handler（`KillSwitchCommandHandlerTests`）:
  - 本人が正しいフレーズを入力すれば**解除される**（`DisengageCalls==1`）。
  - フレーズ不一致では**解除しない**（`DisengageCalls==0`・`WasExecuted==false`）。
  - フレーズ未入力（null）では**解除しない**。
  - フレーズ未設定では**解除しない**（安全既定）。
  - 冪等: 解除→再解除（いずれも正しいフレーズ）で状態は解除のまま。
  - 既存の「解除は確認フレーズなしで実行できる」テストは裁定に合わせ**反転**した（本人が正しいフレーズを入力すれば解除される）。
- 確認フレーズ純関数（`KillSwitchConfirmationTests`）: 未設定理由文言を「起動しない」→「実行しない」へ一般化するが、既存アサーションは
  `Reason` の部分一致（「未設定」を含む）のみで文言変更の影響を受けないため、**テスト変更は不要**（グリーン維持・本 PR では同ファイルを変更していない）。

## 影響範囲

- `NotificationService.Application`（`KillSwitchCommandHandler.cs`・`KillSwitchConfirmation.cs`）
- `NotificationService.Worker`（`DiscordNetBotGateway.cs`）
- テスト（`KillSwitchCommandHandlerTests.cs`・`KillSwitchConfirmationTests.cs`）
- `Shared.Contracts` は不変。新イベント無し。realm-export.json・helm values（OwnerAuth secret 配線）は**別セッション領域につき触れない**。

## 非スコープ

- Discord OwnerAuth realm client 追加（realm-export.json・notification chart の OwnerAuth 配線）＝別セッション。
- pause/resume・段階ゲートの確認方式（可逆・別統制のため不変）。
- 実 Discord 送信の有効化（既定オフのまま）。
