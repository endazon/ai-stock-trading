---
title: IADR-0075 取引の一時停止(pause)を kill switch と同経路の別状態として新設し、監査は既存の設定変更履歴で満たす
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-14, UC-06, UC-07, ADR-0003, ADR-0009, IADR-0008, IADR-0051, IADR-0062]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - https://github.com/endazon/project-planning/pull/29
---

# IADR-0075: 取引の一時停止(pause)を kill switch と同経路の別状態として新設し、監査は既存の設定変更履歴で満たす

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・方針「軽い統制で重い統制を解除させない」「安全既定」）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-10**（リスク統制）、**FR-14**（Discord 操作）、UC-06 / UC-07、ADR-0003 / ADR-0009
- **計画裁定**: 計画 ADR-0009「取引の一時停止(pause)を日次損失ロックアウトと別状態とし、3統制の優先順位を定める」
  （Accepted・PR [endazon/project-planning#29](https://github.com/endazon/project-planning/pull/29)）。
  **3 状態の裁定は計画側の決定であり、本 IADR で再決定しない**（実装方法のみを決める）。
- 対象 Issue: [#152](https://github.com/endazon/ai-stock-trading/issues/152)
- 前提 IADR: [IADR-0008](IADR-0008_daily-loss-limit-basis.md)（日次損失ロックアウト・**無改修で別状態のまま維持**）、
  [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)（Bot 基盤・多層認証・owner マップ機密クライアント）、
  [IADR-0051](IADR-0051_service-to-service-auth.md)（s2s 最小権限）
- 関連仕様書: [20260718_152_pause-resume](../specs/20260718_152_pause-resume.md)

## 課題

計画 ADR-0009 は pause/resume を「日次損失ロックアウトと別状態・3統制の OR ゲート・優先順位 kill switch > lockout > pause」
と裁定した。実装リポジトリでこれをどう置くかを決める。論点は 4 つ。(1) pause 状態の置き場所と判定への結線、
(2) lockout との分離の担保、(3) `/status` の集約と参照専用性、(4) 監査を新イベントで満たすか既存経路で満たすか。

## 決定

### 決定1: pause は kill switch の完全ミラー経路で置く

pause 状態は kill switch（`IKillSwitchStore` / `KillSwitchState` / `KillSwitchService` / `EfKillSwitchStore` /
`PortfolioSnapshot.KillSwitchEngaged`）と**同型・同経路**で新設する。

- `PauseState`（`Paused, Actor?, Reason?, ChangedAt?`・既定 `NotPaused`）、`IPauseStore`、`InMemoryPauseStore`、
  `EfPauseStore` + `PauseRow`（単一行シングルトン）。
- `PortfolioSnapshot` に `TradingPaused`（bool）を追加し、`PortfolioSnapshotBuilder` が `IPauseStore` から合成する。
- `RiskEvaluator` は **kill switch と同じ位置**（`isEntry` のときのみ）で `snapshot.TradingPaused` を評価し、
  `RejectionReason.TradingPaused` を付す。

**理由**: 計画 ADR-0009 決定4 の共通不変条件（3統制とも新規建てのみ停止・手仕舞い/損切りは止めない）は、
現行 kill switch が `RiskEvaluator` の `isEntry` 短絡で構造的に保証している。pause を同じ判定点に置けば、
同一の不変条件を**同一のコードパスで**保証でき、「pause が損切りを止める」退行を作り込めない。別経路に置くと
不変条件が 2 か所に分散する。

### 決定2: lockout は無改修・pause とは判定層を分ける

日次損失ロックアウト（IADR-0008）は `OrderScreeningService`（アプリ層・`ILockoutStore`）に留め、pause は
`RiskEvaluator`（ドメイン層・スナップショット）に置く。`/resume`（`PauseService.Resume`）は `IPauseStore` のみを
操作し、`ILockoutStore` には一切触れない。

**理由**: 計画 ADR-0009 決定5「`/resume` は pause のみ解除」を、`PauseService` が lockout ストアへの参照を
**そもそも持たない**ことで構造的に保証する（軽い統制の解除操作から重い統制の解除経路が生まれ得ない）。
IADR-0008 は変更不要（計画側の指示どおり）。

### 決定3: `/status` は Risk 側で集約し参照専用とする

`RiskStatusService` が kill switch / pause / lockout / stage / 当日損益 / 上限使用率 / ポジションと、
**成立中で最優先の統制**（kill switch > lockout > pause の順）を集約し、`GET /risk-controls/status` で返す。
Discord `/status` はこれを呼んで表示するのみ（状態変更経路を持たない）。

**理由**: Bot が個別エンドポイント（kill-switch / pause / stage-gate / sizing-context / open-positions）を
複数回叩くより、Risk 側で 1 回の一貫スナップショットに集約するほうが表示の整合が取れる。優先順位（表示用）を
権威側（Risk）に置くことで、表示ロジックの重複と齟齬を避ける。判定は OR のままで優先順位は表示専用。

### 決定4: 監査は既存の設定変更履歴で満たす（新イベントを足さない）

pause/resume は `ISettingsChangeLog` に `SettingsChangeType.TradingPaused` / `TradingResumed` として記録する
（アクター・理由・日時・前後値）。**MassTransit の新イベントは発行しない・AuditService は無改修**。

**理由**: kill switch（FR-10・#15 相当）は現状**イベントを発行せず** `ISettingsChangeLog`（`/settings/history`）
だけで FR-11 の監査要件を満たしている。pause/resume を同経路で記録すれば kill switch と**同水準**の監査になり
（受け入れ基準「アクター・理由・日時」を充足）、スコープを Risk＋Notification に閉じられる。新イベントを足すと
`Shared.Contracts`・AuditService・`AuditConsumerCoverageTests` へ波及し、監査の担保が二経路に割れる。
`Shared.Contracts` の変更は `RejectionReason.TradingPaused`（列挙追加1件）に限る。

### 決定5: 冪等性はサービス層で担保する

`PauseService.Pause` は既に pause 中なら**状態を返すのみ**（ストア書き込みも変更履歴記録もしない）。`Resume` も
非 pause 時は同様。**理由**: 受け入れ基準「停止中の再 `/pause` は状態を返すのみで副作用なし」を満たし、
冪等でない再操作による監査ログの水増し（同一状態への無意味な `TradingPaused` 連発）を防ぐ。

### 決定6: 確認は確認ボタンのみ（フレーズ無し）

Discord の pause/resume は確認ボタン（2段階）のみで、kill switch のような確認フレーズ（`KillSwitchConfirmation`）は
要求しない。**理由**: 計画 ADR-0009 決定6。pause は `/resume` で戻せる可逆操作であり、kill switch の摩擦と同格に
する理由がない。逆に kill switch 側の確認フレーズは薄めない（`KillSwitchConfirmation` は無改修）。

## 影響・非対象

- **非対象**: 実 Discord Gateway 接続・実 Keycloak 認可の疎通確認（CI で外部 SaaS へは張れない）。後続 E2E（#82 系）で検証する。
- **回帰防止**: kill switch・日次損失ロックアウトの既存テストを緑のまま維持する（判定点・ストアを変更しない）。
- **安全既定**: 既定は非 pause。pause は新規建てのみ停止し手仕舞い・損切りは継続。owner トークン未設定は操作失敗（成功に見せない）。
