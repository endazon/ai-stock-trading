---
title: IADR-0342 SIMULATE の損切り実行機構を選択式にする — 設定点は risk-management の利用者専用設定、手法は承認に載せ、発注執行が SIMULATE 限定・空売り除外で解決し、S2 は免除の事実を別イベントで残す
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11, FR-12, UC-02, UC-06, SC-02, SC-03, ADR-0003, ADR-0016, ADR-0040, IADR-0016, IADR-0111, IADR-0134, IADR-0141, IADR-0161, IADR-0210, IADR-0211]
author: claude (Claude Code)
created: 2026-09-17
updated: 2026-09-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1・決定2・決定3・決定6)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 の 3 文〔口座種別の軸〕)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md (§逆指値が成立しない場合の扱い)
---

# IADR-0342: SIMULATE の損切り実行機構を選択式にする

- 状態: Accepted
- 日付: 2026-09-17
- 決定者: claude（起票 #819。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: FR-10（3 文に口座種別の軸。2026-09-17 改訂）、FR-12（ペーパートレード）、UC-06、SC-02 / SC-03、
  **ADR-0040 決定 1・2・3・6**（planning#638 の裁定）、ADR-0016 決定 2(b)（空売りの統制。改めない）、ADR-0003（AI は統制を上書きできない）
- 対象 Issue: #819（症状は #809: moomoo のペーパー口座は `OrderType_Stop` を受け付けず、SIMULATE の新規建てが全件取消になる）
- 関連する実装仕様書: [20260917_819_stop-loss-method-selection](../specs/20260917_819_stop-loss-method-selection.md)
- 関連 IADR: [IADR-0210](IADR-0210_broker-side-stop-loss-unification.md)（S0 の実装。本 IADR は覆さない）、
  [IADR-0211](IADR-0211_opend-unavailable-forgo-without-queueing.md)（見送り）、[IADR-0016](IADR-0016_safe-broker-execution.md) /
  [IADR-0111](IADR-0111_broker-tier-selection.md)（実弾の閂）、[IADR-0141](IADR-0141_live-switch-explicit-confirmation.md)（発注先の変更）、
  [IADR-0161](IADR-0161_broker-provider-allow-list-resolution.md)（設定行の allow-list 読み取り）、IADR-0134 決定 2（序数は末尾追加）

## コンテキストと課題

ADR-0040 は moomoo SIMULATE に限り損切りの実行機構を S0（既定・ブローカー側逆指値）/ S1（ソフトウェア逆指値）/
S2（逆指値なしの建玉を許容）/ S3（他のブローカー側注文種別）から選べるようにし、実弾（`TrdEnv=real`）では S0 以外を
選べない（S1〜S3 のまま実弾へ切り替わったら起動時に停止する）と定めた。空売り建玉には及ばない。変更経路は
統制値と同じ（利用者の設定変更または方針確定プロセス）で、生成 AI は上書きできない。

実装で決めるべきは (a) 選択をどこに保持しどう変更させるか、(b) 実弾での拒否をどこで効かせるか（発注執行は起動時に
手法を知らない）、(c) 手法を発注執行へどう届けるか、(d) S2 の「ペーパーで免除」をどう記録するか、(e) 未実装の S1 / S3 が
選ばれたときの扱い、である。本 PR の射程は選択機構・実弾での拒否・承認への搭載・空売りの除外・S2 であり、
S1 は #820、S3 は #821、表示（SC-02 の入力・SC-03・日報）は #823 である。

## 検討した選択肢

1. **発注執行の構成値（env / Helm）で手法を持つ** — 起動時に知れるため「起動時に停止」を字義どおり実装できる。
   しかし ADR-0040 決定 3 の「統制値と同じ経路（利用者の設定変更）・変更は監査される」を満たさず、変更に再配備が要る。
   発注先（BrokerProvider）が既に risk-management の設定と発注執行の構成の 2 か所にある状態へ、さらに 1 軸を構成側に足す。**却下**。
2. **risk-management の設定に保持し、承認（`OrderApproved`）に載せて発注執行が解決する**（採用）。
3. **発注執行が承認ごとに risk-management の設定を同期照会する** — 発注経路に同期呼び出しと障害点を足し、
   承認時点と発注時点の設定の競合（走行中の変更）を作る。**却下**。
4. S2 の記録を `ProtectiveStopCoverageLost` の新しい Remediation 値（例 `Waived`）で表す — 型の追加は無いが、
   「統制が働いた／破れた（Critical）」事象と「利用者の選択で置かなかった」事象が同じ型・同じ監査種別に混ざり、
   保護喪失の購読者（台帳結線・Critical 通知）すべてが新しい値の分岐を持つことになる。**却下**（新イベントを採る）。

## 決定

1. **型**: `Shared.Contracts.Trading.StopLossExecutionMethod`（`BrokerStopOrder=0`=S0 / `SoftwareStop=1`=S1 /
   `NoProtectiveStop=2`=S2 / `AlternativeBrokerOrderType=3`=S3）。**0 を S0 に置く**ことで、本項目を持たない旧いメッセージ・
   旧い設定行が構造的に S0 として読まれる。序数は動かさず、新しい手法は末尾へ足す（IADR-0134 決定 2）。
2. **設定点（risk-management）**: `RiskManagementSettings.StopLossMethod`（既定 S0・本体プロパティ）を単一行 JSON
   （`risk_settings`・Version 並行トークン）へ nullable キーとして足し、読み取りは allow-list（`StopLossMethodChange.Resolve`。
   旧行・未知の序数は S0。**倒す先は逆指値を置く側**）。変更は `PUT /risk-controls/settings/stop-loss-method`
   （**OwnerOnly**。サービスロールは 403）で、理由必須・前後値つき履歴 `SettingsChangeType.StopLossMethodChanged`（序数 9・末尾追加）。
   受理条件は純関数 `StopLossMethodChange.Evaluate` に集約する: 未知の手法・理由なし・**発注先が moomoo REAL の間の S0 以外**を
   拒否し（400。設定も履歴も変えない）。**逆方向**として `BrokerProviderChange.Evaluate` に現在の手法を渡し、S0 以外のまま
   moomoo REAL へ切り替える要求を `BrokerProviderChangeRejection.StopLossMethodNotBrokerStop`（序数 4・末尾追加）で拒否する
   ——確認操作（同意・「REAL」）が揃っていても拒否する（確認操作は実資金を使う意思の確認であり、保護逆指値を置かない手法を
   実弾へ持ち込む許可ではない）。現在値は `GET /settings` と `GET /status`（SC-03）の `stopLossMethod` に載せる（表示は #823）。
   文字列トークン等の不正な型に対する寛容な JSON 変換器は付けない——本項目を書くのは数値 enum の API だけであり、
   発注先（IADR-0161）のように外部ツールが書いた行を想定する経路が無い。
3. **承認への搭載**: `OrderApproved` の末尾に `StopLossExecutionMethod StopLossMethod = BrokerStopOrder` を足す（任意項目・
   後方互換。`event-schemas.baseline.json` は追加として更新）。**取引判断のスクリーニングだけが審査時点の設定値を載せる**。
   owner 手仕舞い・維持率割れの自動縮小は Close であり既定のまま（手法は Open にしか効かない）。発注執行は承認が運ぶ値で
   保護レグを扱う（走行中の設定変更と承認が競合しない）。risk-management は解釈せず設定値をそのまま運ぶ——承認は
   「どの設定で承認したか」の記録でもある。
4. **発注執行の解決**（純関数 `OrderExecutionService.Domain.StopLossMethodPolicy.Resolve`。Open にのみ適用）。順序が統制である:
   1. 手法が S0 → S0（**発注先を問わない。現行挙動は 1 バイトも変わらない**）
   2. 発注先（**実際に発注するアダプタの `Provider`**）が moomoo SIMULATE でない → **拒否**: 発注せず `OrderDispatchForgone`
      （新理由 `StopLossMethodNotPermitted`・序数 3・末尾追加）を発行し Error ログ。予約も取らない。
      **内蔵 paper も拒否に含める**——ADR-0040 決定 1 の射程は `TrdEnv=SIMULATE` であり、S0 へ黙って読み替えると
      「S2 を選んだのに逆指値が置かれた」という説明のつかない状態を作る（見送りと通知で設定の食い違いを表に出す）。
   3. **空売りのエントリー（`ProductType.ShortSell`）→ S0**（ADR-0040 決定 1 末尾。ADR-0016 決定 2(b) が独立に効く）
   4. S2 → **保護逆指値を発注しない**（決定 6）
   5. S1 / S3 / 未知の値 → **S0 と同じ扱い**＋Warning ログ「未実装（#820 / #821）」。緩い側（免除）へ倒さない
5. **「起動時に停止」の実現**: 発注執行は手法を起動時に知らない（設定は risk-management の DB にあり承認ごとに届く）。
   加えて発注執行の実弾は `LiveTradingGate`（閂 0）がアダプタ生成前に起動時停止させ、現状は到達不能である。
   したがって起動時チェックは置かず、「実弾で S0 以外が有効」を **(i) 設定側の 2 方向の拒否**（決定 2）と
   **(ii) 承認ごとの拒否**（決定 4-2）で塞ぐ。(ii) は設定行の手編集や将来の閂解禁で (i) が破られた場合の最後の関門であり、
   Error ログと見送り通知で即時に表面化する。**実弾解禁の IADR は本決定の見直しを含めること**（解禁後は発注執行が起動時に
   risk-management の設定を照会して停止する形も検討対象になる）。
6. **S2 の事実**: 新イベント `ProtectiveStopWaived`（EntryDecisionId, Symbol, Market, Side, ProductType, Quantity,
   StopLossPrice, Method, Provider, OccurredAt）。**エントリーが生きている（Accepted / PartiallyFilled / Filled）ときだけ**
   発行する。取消・手仕舞いも行わない。逆指値レグの記録（`protective_stop_orders`）を作らないため
   **`ProtectiveStopGuard` の巡回対象に入らない**（失効扱いで手仕舞われることが構造的に無い。S0 の建玉に対するガードは不変）。
   監査は `ProtectiveStopWaivedAuditHandler`（EventType は保護喪失と別・相関はエントリーの DecisionId）、通知は
   `ProtectiveStopWaivedNotificationHandler`（**Warning**。利用者の選択どおりの結果であり、統制が破れた Critical と同じ重みに
   しない。本文に「損切りライン到達でもシステムもブローカーも決済しない」を明記）。ハンドラは Wolverine の規約発見と
   ビルド時 codegen（#814）にそのまま載り、登録の追加は要らない。`ProtectiveStopCoverageLost` の意味は変えない。
   再配送（相 1 の完了済み再発行）は免除の事実を再発行しない（保護レグのイベントと同じ規律）。

## 理由

- **設定に置き承認に載せる**ことで、ADR-0040 決定 3（統制値と同じ経路・監査）を既存の設定機構（版・履歴・OwnerOnly）の
  再利用だけで満たし、発注経路に同期照会を足さない。承認時点の値で発注するため、走行中の変更と承認の競合が無い。
- **解決を発注執行の純関数 1 か所に置く**のは、「実際に発注するアダプタ」を知るのが発注執行だけだからである
  （risk-management の発注先設定と発注執行の構成は別物であり、食い違い得る）。拒否の判定をアダプタの `Provider` で行えば、
  設定側の関門が何らかの理由で破られても実弾は無防備にならない。
- **未実装・未知を S0 へ倒す**のは、損切りの手法で「分からないときは免除」を選ぶと利用者が選んでいない無防備な建玉が生まれるため。
- **免除を別イベントにする**のは、監査台帳の「建玉あり ⇒ 有効な逆指値あり（または解消済み・人手対応）」の読みに対する例外を、
  保護喪失の型を汚さずに台帳上で明示できるため（選択肢 4 の却下理由）。

## 結果・残余リスク

- 良い影響: SIMULATE＋S2 で新規建てが残り、PoC の位置系（建玉保持・維持率）を観測できる。S0 と実弾の挙動は不変。
- **市場監視の損切り到達通知**（`StopLossTriggered`）は S2 の建玉にも「決済はブローカー側の逆指値が実行します」と書く。
  S2 の建玉には当たらない。免除の通知が「決済しない」を明示することで緩和し、通知文の手法別の出し分けは S1（#820）で
  損切り到達を購読する際に併せて扱う。
- **同一銘柄に S0 の建玉と S2 の建玉が併存する場合**、`ProtectiveStopGuard` の「建玉消滅なら残存逆指値を取り消す」判定は
  銘柄単位の建玉数量を見るため、S0 側の建玉が消えても S2 側の数量が残っていれば残存逆指値を取り消さない。SIMULATE では
  S0 の逆指値が拒否されるため現状は起こらない（手法を途中で切り替えた場合にだけ理論上あり得る）。
- 手法の選択は内蔵 paper では見送りになる（決定 4-2）。内蔵 paper で S2 を試す経路は無い（ADR-0040 の射程外）。
- 表示（SC-02 の入力・SC-03・日報に手法を出す）は #823。**BFF の経路（`/bff/risk-controls/settings/stop-loss-method`）も
  #823 で足す**（BFF は経路を明示列挙し、基盤側にも同趣旨の全経路テストがあるため、画面の消費と揃えて入れる）。
  それまでは risk-management の `PUT /risk-controls/settings/stop-loss-method` を利用者トークンで直接呼んで選ぶ。
