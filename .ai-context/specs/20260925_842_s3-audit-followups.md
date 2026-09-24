---
title: S3（#821）監査の非ブロッキング論点のうち今すぐ直せる 3 件 —— 拒否された代替レグに捏造 ID を載せない・台帳の自由記述欄へ入る理由文に上限と線引きを置く・仕様書の不在テスト名を追記で正す
type: spec
status: accepted
related_ids: [FR-10, FR-11, FR-12, ADR-0040, IADR-0347, IADR-0405, IADR-0211]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 / FR-11)
---

# 仕様書: S3 監査の残論点 1・2・4 の是正（#842）

## 起点

- [#842](https://github.com/endazon/ai-stock-trading/issues/842)（#838 ＝ S3 の監査で挙がった非ブロッキング 5 件）。
  2026-09-23 のトリアージで 5 件とも残と判定され、**1・2・4 は実測不要で今すぐ直せる**（分類 B）とされた。
- **本作業の射程は 1・2・4 だけである。** 3（発火したが約定しない StopLimit をガードが検知できない）と
  5（helm の `stopLimitOffsetRatio=0` が無言で既定へ戻る）は **S3 の受理可否の実測待ち**で、実測窓は #809 の裁定に
  依存する。本 PR は触らない（PR は `Refs #842`）。

## 🔴 実測（`origin/develop` = `9a1014f`）

`git rev-parse --is-shallow-repository` → **`true`**（本 worktree は浅い）。以下は `git log` / `git blame` を出典に
引かず、**ファイルの現物**だけを根拠にする。

| # | 論点 | 現物 |
| ---: | --- | --- |
| 1 | 拒否された代替レグに実在しない ID | `MoomooBrokerAdapter.Terminal()` が `Guid.NewGuid().ToString("N")` を採番する。`PlaceWithRejectionDetailAsync` の送信前棄却と `retType=-1` の確認済み拒否の 2 か所だけが `Terminal` を使う。`OrderExecutionAppService.PlaceProtectiveStopAsync` は `placement.Order.OrderId` を `AlternativeProtectiveStopAttempted.BrokerOrderId` へそのまま載せる。契約コメントは「発注できたときのブローカー注文 ID」 |
| 2 | 台帳の自由記述欄へ任意長の例外メッセージ | `OrderExecutionAppService` の例外分岐が `rejectReasonMessage: ex.Message` を載せる。接続確立の失敗の文面は `MMApiMoomooTradeClient` の `OpenD への InitConnect が失敗しました（{OpenDHost}:{OpenDPort}）。`。`AuditEntryFactory.From(AlternativeProtectiveStopAttempted)` は `Summary` を 200 字で切るが、`Detail`（`AuditSerialization.Serialize(e)`）は**全量・上限なし・秘匿なし** |
| 4 | 仕様書の不在テスト名 | `.ai-context/specs/20260918_821_s3-alternative-order-types.md` の受け入れ基準表 89・91・92 行の 3 件がリポジトリ全体に不在（下の母集合）。残る参照は実在する |

## 🔴 母集合（規則 9〜11。走査した語と結果・除外理由）

- **項目 1（誤りの側の文字列 `Terminal(` / `.OrderId` を載せる S3 の経路）**:
  `git grep -n "Terminal(" -- backend` → 本番は `MoomooBrokerAdapter.cs` の 2 か所だけ（他は `IsTerminal` の別物）。
  `git grep -n "BrokerOrderId" | grep -i "alternative\|StopAttempted"` → 契約 1・発行 1（`OrderExecutionAppService`）・
  テスト 2。
  - 採る: 契約 `AlternativeProtectiveOrderPlacement` に `BrokerOrderId` を足し、アダプタが「ブローカーが採番したか」を
    知っている場所で決める。発行側は `placement.BrokerOrderId` を載せる。契約コメント 2 か所
    （`AlternativeProtectiveStopAttempted.cs` / `IAlternativeProtectiveOrderBroker.cs`）を実態へ合わせる。
    `docs/api/events-and-ports.md` の行は項目名の列挙であり偽にならない（除外）。
  - 🔴 **除外（射程外・残余リスク）**: `Terminal` の捏造 ID は **S0・エントリーの拒否**でも `BrokerOrder.OrderId` に
    載り、エントリーの拒否は `ExecutionRecord` と `OrderExecuted.BrokerOrderId`（監査台帳）へ入る。
    `BrokerOrder.OrderId` は非 null の契約で paper を含む全発注経路が共有しており、null 化は契約全体の改訂になる。
    issue #842 の論点 1 は「拒否された**保護レグ**の BrokerOrderId」であり、本 PR は S3 の試行の記録に限る。
    エントリー側は同型の別論点として報告する（IADR-0405 残余リスク）。
- **項目 2（誤りの側の文字列 `ex.Message` / `e.Message` / `exception.Message` を契約イベントへ載せる経路）**:
  `git grep -n "ex\.Message\|exception\.Message\|e\.Message" -- 'backend/Services/*.cs' ':!*Tests*'` からログ出力を除いた
  13 件を 1 件ずつ見た。**契約イベント（＝監査台帳）へ入るのは `OrderExecutionAppService` の S3 の 1 件だけ**である。
  他はエンドポイントの 400/409 応答（台帳に入らない）・`TradeDecisionParser` の失敗詳細（ログへ出るだけ）・
  バックテストの欠測記録（バックテスト結果であり監査台帳ではない）・`MoomooBrokerAdapter` の不明例外の文面
  （例外が運び、S3 では上の 1 件を経て台帳へ入る＝同じ経路）。issue の「初めての経路」の主張と一致する。
  - `AlternativeProtectiveStopAttempted` の購読者: `AuditEventHandlers`（台帳）と `OrderApprovedHandler`（ログのみ）。
    通知は購読しない（IADR-0347 決定 8）。**台帳の境界（`AuditEntryFactory`）で一度だけ整える**のが母集合を閉じる形である。
- **項目 4（誤りの側の文字列＝不在の 3 テスト名そのもの）**: 3 語それぞれで `git grep` → 当該仕様書 1 ファイルだけ。
  - 🔴 **仕様書は凍結記録である**（`.ai-context/README.md`）。表の本文は書き換えず、同仕様書の末尾へ
    `［2026-09-25 追記 / #842］` で正しいテスト名への対応を足す（先例: `20260919_847_…` の日付つき追記）。
  - 除外: `docs/tests/FR-10_risk-controls-tests.md` の T-10-362〜364 の行（テスト名を持たず、振る舞いの記述は実体と一致）。
- **規則 10（本変更で新たに誤りになる自分の記述）**: 契約コメント「発注できたときのブローカー注文 ID」は本変更で
  **正しくなる側**（拒否で null になる）。`OrderExecutionServiceAlternativeStopTests` の `BrokerOrderId.Should().Be("alt-1")`
  （拒否の刺激）は本変更で**偽になる**ため改める。T-10-366 の行「全量 JSON にも理由文が残る」は、本変更後も
  上限内の理由文は残るので偽にならない（上限・伏せ字の注記を本 PR の新しい行側に書く）。
- **規則 11（窓）**: 本作業は時間差を扱わない（該当なし）。

## 射程

1. **項目 1**: `AlternativeProtectiveOrderPlacement` に `string? BrokerOrderId`（末尾）を足す。アダプタは
   ブローカーが採番した ID（`MoomooOrderResult.OrderId`）だけを入れ、送信前棄却・確認済み拒否（`Terminal` の合成）では
   null にする。発注執行は `placement.BrokerOrderId` を試行の記録へ載せる（`OrderExecutionAppService` は 1 行だけ）。
   **ブローカーが ID を返したうえで拒否状態だった場合は、その実在する ID を残す**（状態で null にしない）。
2. **項目 2**: `AuditService/Domain/AuditFreeText`（新規）で台帳の自由記述欄へ入る理由文を整える。
   `AuditEntryFactory.From(AlternativeProtectiveStopAttempted)` は整えた写しで `Summary` と `Detail` の両方を作る。
   - **上限**: 500 文字（超過は切り詰めて `…`）。サロゲートペアを割らない。
   - **線引き**: 接続先（URL・`ホスト:ポート`・IPv4）を `［接続先］` に伏せる。改行等の制御文字は空白 1 つへ畳む。
   - 載せてよいもの: ブローカーの `retMsg`・発注前検証の定型文・例外の種類が読める文面（上の伏せ字の後）。
3. **項目 4**: 仕様書 `20260918_821_…` へ日付つき追記（本文は不変）。

### 射程外

- 項目 3・5（上記）。エントリー／S0 の拒否に載る捏造 ID（母集合の除外）。
- 契約イベントの他の自由記述欄（`Rationale` 等は LLM の出力であり別の性質。既存の `Summary` 200 字切りのまま）。

## 決めたこと

実装判断は [IADR-0405](../adr/IADR-0405_s3-audit-followups-broker-order-id-and-free-text-bound.md) に記録する。

## 受け入れ基準 → テスト

テスト ID は依頼で予約された **T-10-850〜T-10-859** から採る（`git grep "T-10-85[0-9]"` を origin/* 全ブランチで実行し 0 件を確認）。

| ID | 受け入れ基準 | テスト |
| --- | --- | --- |
| T-10-850 | 拒否された代替レグ（送信前棄却・`retType=-1`）の試行の記録に**実在しない注文 ID を載せない**。受理ではブローカーの ID を残す（対の肯定形） | `MoomooBrokerAdapterAlternativeStopTests.拒否された代替レグにはブローカー注文IDを載せない_否定形` / `…受理された代替レグはブローカーが採番した注文IDを持ち帰る` / `OrderExecutionServiceAlternativeStopTests.SIMULATEでS3は代替注文種別で発注し拒否理由が試行の記録に残る`（BrokerOrderId が null） |
| T-10-851 | 実測の接続失敗文面（`opend.ai-stock-trading.svc:11111`）が監査台帳の `Summary` にも `Detail` にも残らない（否定形） | `AuditFreeTextTests.代替レグの理由文に接続先があっても台帳には残さない_否定形` |
| T-10-852 | 理由文の長さの境界（0 / 1 / 499 / 500 / 501 / 5,000）。上限以内は不変、超過は上限＋`…` | `AuditFreeTextTests.上限以内の理由文は変えない_境界値` / `…上限を超える理由文は上限で切り詰めて省略記号を付ける_境界値` / `…切り詰めはサロゲートペアを割らない` / `…代替レグの理由文が長くても台帳の全量JSONは上限を超えない` |
| T-10-853 | 接続先の形（URL・ホスト名:ポート・IPv4・IPv6）を伏せ、通常の理由文（`retMsg`・時刻・価格・`retType=-1`）は変えない。乱数入力で長さ上限と伏せ字が常に成り立つ（プロパティ） | `AuditFreeTextTests.接続先を伏せる` / `…通常の理由文は変えない_誤検出の否定形` / `…任意の入力で上限と伏せ字が成り立つ_プロパティ` / `…長い反復入力でも伏せ字の処理は打ち切られない` / `…改行などの制御文字は空白1つへ畳む` |

## 検証

- `dotnet build backend/backend.slnx -v q`（0 警告 0 エラー）
- `dotnet test` AuditService.Tests / OrderExecutionService.Tests / Shared.Contracts.Tests
- `dotnet format backend/backend.slnx --verify-no-changes`
- 文書検査器一式（共通ルールの列挙）

## 実装中に追加で判明したこと

- 伏せ字の正規表現を素朴に書く（語の途中からも始まり、語の中でバックトラックする）と、**1 万文字の入力で
  200 ms のタイムアウトに達し、理由文ごと失われた**（`代替レグの理由文が長くても…` の初回実行で実測）。
  各選択肢を語の先頭からだけ始め、語の中を原子グループで読む形へ改め、反復入力 6 形でタイムアウトしないことを固定した。
- 変異注入: 発注執行の `placement.BrokerOrderId` を `placement.Order.OrderId` へ戻す・アダプタの確認済み拒否で
  ID を返す、の 2 点を同時に入れると `AlternativeStop` の 20 テスト中 2 件が赤になる（T-10-850 の 2 面）ことを確かめてから戻した。
