---
title: IADR-0081 段階ゲートの Discord コマンドは Bot 側で Risk の OwnerOnly エンドポイントを呼ぶだけの薄い追加とし、数値 enum 整形を Worker に隔離する
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-14, UC-06, ADR-0008, IADR-0051, IADR-0062, IADR-0070]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs: []
---

# IADR-0081: 段階ゲートの Discord コマンドは Bot 側で Risk の OwnerOnly エンドポイントを呼ぶだけの薄い追加とし、数値 enum 整形を Worker に隔離する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・方針「Risk 無改修」「安全既定」「破壊的操作は確認」）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-20**（段階ゲート）、**FR-14**（Discord 操作）、UC-06、ADR-0008
- 対象 Issue: [#165](https://github.com/endazon/ai-stock-trading/issues/165)（親後続: `#20` / `#15`）
- 前提 IADR: [IADR-0070](IADR-0070_stage-gate-persistence-and-approval.md)（段階ゲートの永続化・承認・OwnerOnly エンドポイント・**無改修で呼ぶだけ**）、
  [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)（Bot 基盤・多層認証・owner マップ機密クライアント）、
  [IADR-0051](IADR-0051_service-to-service-auth.md)（s2s 最小権限）
- 関連仕様書: [20260718_165_stage-gate-discord-bot](../specs/20260718_165_stage-gate-discord-bot.md)
- 並行 PR との番号分離: #106=IADR-0080・本 issue=**IADR-0081**・#167=IADR-0082。

## 課題

段階ゲート（#20）は承認遷移・撤退評価を OwnerOnly の HTTP エンドポイントとして提供済みで、Discord からの承認は
「Bot ハンドラは後続」として分離された。本作業でこの Bot 側コマンドを NotificationService にどう置くかを決める。
論点は 3 つ。(1) コマンド体系と確認フローの粒度、(2) 昇格（破壊的操作）の二重適用防止、(3) Risk が数値で
シリアライズする enum（段階・拒否基準）を Discord 表示へどう整形し、層の結合をどこに閉じるか。

## 決定

### 決定1: pause コマンド経路の完全ミラーで置き、数値 enum 整形は Worker アダプタに隔離する

段階ゲートのコマンドは kill switch / pause（IADR-0062 / IADR-0075）と**同型・同経路**で置く。

- `BotCommandKind` に `StageStatus` / `StagePromote` / `StageDemote` / `StageWithdrawal` を追加、`BotCommand` に
  `int? TargetStage`（既定 null）を追加、`BotCommandParser` が `/stage …` を解析（純関数・全数テスト可能）。
- `IStageGateController`（Application ポート）は `GetStatusAsync` / `RequestTransitionAsync(target)` /
  `EvaluateWithdrawalAsync` を持ち、**整形済み文字列を返す**。実装 `HttpStageGateController`（Worker）が Risk の
  stage-gate エンドポイントを呼び、応答を Discord 表示テキストへ整形する。
- `StageGateCommandHandler`（Application）は多層認証（`DiscordCommandAuthorizer` 再利用）→ 解析 → Risk 呼び出し。

**理由**: pause の `HttpPauseController` は「enum の数値表現に依存しない（真偽値から導出）」方針で representation-agnostic
を保っている。段階ゲートは拒否基準（`StageGateCriterion`）という真偽値へ潰せない enum を含むため完全には避けられないが、
**数値 enum の射影 DTO と整形を Worker アダプタ 1 か所に閉じ**、Application 層（ハンドラ・ポート）は整形済み文字列だけを
扱う。これにより「Risk の JSON 表現」への結合点をハンドラや Gateway に漏らさず、pause と同じ層構造を維持できる。
`StageGateCriterion` / `TradingStage` は codebase の enum 慣行（連番・append-only）に依拠し、**未知の数値は
fail-safe のフォールバック文言**（`不明な基準(N)`）に倒して例外を出さない。この結合は本アダプタのテストで固定する。

### 決定2: 昇格の二重適用防止は Risk 側の連番検証＋確認ボタン無効化で担保し、版番号ガードは採用しない

昇格（`/stage promote`）は破壊的操作（ペーパー→実弾方向）だが、二重確定の防止に #15 の `VersionedConfirmationGuard`
（対象ID＋版番号の楽観ロック）は**使わない**。

- Discord では確認ボタン（2段階）で受け、押下後は `DisableComponentsAsync` でボタンを無効化し再押下を防ぐ（pause と同型）。
- 二重送信が起きても、Risk の `StageGate.RequestTransition` が**構造的に弾く**: 現段階への遷移は 422
  `TargetIsCurrentStage`、飛び級は `PromotionMustBeSequential`。したがって「promote を2回押す」の2回目は昇格ではなく
  422 拒否になり、二重に段階が進むことはない。

**理由**: `VersionedConfirmationGuard` は「同一ドラフトの版番号」という自然な版概念がある報告書確定（#14）向けの機構で、
段階遷移には対象ID＋版番号に相当する概念がない。段階遷移は Risk 側が現段階と連番を権威として持ち、そこで二重適用が
既に構造的に防がれているため、Bot 側に版番号ガードを重ねる必要がない（機構を増やさず既存の不変条件に委ねる）。
issue の受け入れ基準「昇格の破壊的操作は確認（版番号付き冪等確定と同型）を**検討する**」に対し、検討の結論として
**確認ボタン＋Risk 側連番検証**を採用する。

### 決定3: 確認の粒度は「遷移＝確認ボタン、撤退評価・状態照会＝直接実行」とする

- `promote` / `demote`（段階の手動遷移）は確認ボタンを要求する（pause と同水準・#152）。
- `withdrawal`（撤退評価）は確認なしで直接実行する。撤退評価は Risk 側で**安全側**（`HaltNewEntries` 成立時に
  kill switch を自動起動）にしか働かず、確認で摩擦を増やすと緊急時の安全化を遅らせるため。
- `status` は参照専用（副作用なし）。

**理由**: 確認の摩擦は「破壊的方向（実弾・段階前進）」に集中させ、安全化方向（撤退評価）には課さない。これは
kill switch（起動は確認フレーズ・解除はボタンのみ）と同じく「危険側に閂を厚く、安全側に薄く」の一貫方針に沿う。
demote は安全方向だが、手動の段階変更で監査に残る操作のため確認ボタンは付ける（pause が resume にもボタンを課すのと同様）。

## 影響・非対象

- **Risk・`Shared.Contracts` 無改修**: HTTP 呼び出しのみ。変更は NotificationService 配下に閉じる（#167 が Risk/Audit、
  #106 が frontend を並行で触るための境界）。
- **履歴のペイロード**: `/stage status` は `GET /risk-controls/stage-gate` の応答（`StageGateStatus.History` は追記専用台帳の**全件**）を受け、表示は直近 `RecentHistoryCount` 件のみに絞る。低頻度操作のため実害は小さいが、遷移回数の積み上げに対し Risk→Bot 間のペイロード・デシリアライズが線形に増える。Risk 側の絞り込み（`GET /risk-controls/stage-gate/history?limit=N` 相当）は **Risk 無改修の本スコープ外**であり、必要になった時点で別 issue として起票する（本 PR では対応しない）。
- **非対象**: 実 Discord Gateway 接続・実 Keycloak 認可の疎通確認（CI で外部 SaaS へは張れない）。後続 E2E（#82 系）で検証する。
  Gateway アダプタ（`DiscordNetBotGateway`）の `/stage` 配線は CI では単体テストせず、Application ハンドラ・パーサ・
  HTTP アダプタを fake handler で全数テストする（pause 先例と同じ切り分け）。
- **安全既定**: Bot は既定オフ・多層認証の設定が空なら全拒否・破壊的操作（promote/demote）は確認ボタン必須。
  owner トークン未設定は 401＝操作失敗（成功に見せない）。
