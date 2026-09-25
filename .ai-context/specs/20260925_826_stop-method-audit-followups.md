---
title: 損切り手法の選択（#819）の監査で残った論点の是正 — 到達通知への S3、免除の打ち消しの記録、設定上の発注先の読み方、IADR-0342 の追記（#826）
type: spec
status: accepted
related_ids: [FR-10, FR-11, FR-12, UC-02, UC-06, ADR-0040, IADR-0342, IADR-0344, IADR-0347, IADR-0019, IADR-0413]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1・決定3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 の口座種別の軸 / FR-11 監査ログ)
---

# 仕様書: 損切り手法の選択の監査で残った論点（#826）

## 起点

- #826（PR #825 / #819 の監査の非ブロッキング指摘 5 件）と、同 issue のトリアージコメント（2026-09-23）。
- 本 PR の割り当て: ブランチ `fix/FR-10-826-stop-method-audit-followups`・IADR-0413・テスト ID `T-10-896`〜`T-10-909`。
- 衝突回避: open PR #973（`OrderFillPoller`・`ProtectiveStopGuard`・`IExecutedOrderStore`）・#977（`ProtectiveStopNetting`・
  `BrokerPositionSnapshotService`）のファイルは触らない。

## 着手前の再検証（develop `058475d8`・`git rev-parse --is-shallow-repository` → `false`）

| # | 判定 | 根拠（本 PR 着手時に自分で読んだ位置） |
| ---: | --- | --- |
| 1 設定側の判定が発注側より緩い | **残** | `StopLossMethodChange.IsPermittedOn` は設定上の発注先が moomoo REAL のときだけ拒否。発注執行は実アダプタの `Provider` で判定（`StopLossMethodPolicy.Resolve`）。`docs/functional/FR-12_paper-trade.md` §未決事項は「発注先の設定値はまだ発注経路を動かさない」と書く |
| 2 到達通知の文面 | **是正済み・残余あり** | `NotificationFormatter.From(StopLossTriggered)` と `RiskManagementService/.../StopLossTriggeredHandler` は S0 / S1 / S2 を列挙するが **S3 が無い**。S3 は IADR-0347 で実装済み（`StopLossMethodDisposition.AlternativeBrokerOrderType`） |
| 3 S0 と S2 の混在時のガード | **残**（本 PR では扱わない） | 基底は銘柄単位の合算（`ProtectiveStopNetting`＝#977 が改修中のファイル）。S2 は保護記録を持たない |
| 4 文書の不足 | **半分是正** | `docs/functional/FR-10_risk-controls.md` に「S2 から S0 へ戻したとき」の注意あり。IADR-0342 に無い |
| 5 免除イベントが建玉を誤表示し得る | **残** | `OrderExecutionAppService` は受付（Accepted）時点で `intent.Quantity` を載せて `ProtectiveStopWaived` を発行する。約定 0 のまま取消されても監査台帳の要約は「逆指値なしの建玉を保持する」のまま |

## 設計

### 項目 2（S3 を到達通知へ）

- `NotificationFormatter.From(StopLossTriggered)` の列挙に「S3＝ブローカー側の代替注文種別（ストップリミット／トレーリングストップ）が実行
  （システムは発注しない。ストップリミットは指値のため約定しないことがある）」を足す。
- リスク管理の `StopLossTriggeredHandler` のログ文面とコメントにも S3 を足す。
- `docs/functional/FR-10_risk-controls.md`「注意（S2 の読み方）」の列挙にも S3 を足す。

### 項目 5（免除の打ち消しを監査台帳へ残す。IADR-0413 決定 2）

- **発注執行・契約は変えない**（受付時点で発行する規律は IADR-0342 決定 6 のまま）。約定時に発行し直す形は、終端化を観測する唯一の点
  （`OrderFillPoller`）と記録（`IExecutedOrderStore`）に手法を持たせる改修になり、#973 と同じファイルに入る。
- 監査サービスが、同じ相関（エントリーの DecisionId）に **`ProtectiveStopWaived` と終端の `OrderExecuted`**（Filled / Expired /
  Cancelled / Rejected）が揃った時点で、**終端の約定数量が免除の数量より少なければ**派生の記録
  `ProtectiveStopWaiverSettled` を 1 件追記する。約定 0 なら「免除を打ち消し（建玉は生じなかった）」、一部なら「免除の対象は約定数 N」。
- どちらの到着順でも 1 件にするため、記録 Id は相関から決定的に導く（`AuditCorrelation.From("protective-stop-waiver-settled:{DecisionId}")`）。
  追記は Id で冪等（`IAuditEventStore.Append`）。
- 通知・監査の免除の文面は「数量＝発注数量（建玉は約定で確定。約定しないまま取消・失効すれば建玉は生じない）」と読めるようにする。

### 項目 1（設定側の判定は観測へ寄せず、運用で揃える。IADR-0413 決定 1）

- 設定側（リスク管理）の判定は**実弾の拒否（安全側の関門）**に限り、設定上の発注先で判定する現行を維持する。
  観測（`BrokerAccountObserved`）へ寄せない。理由: (a) 設定側が緩くても、発注執行の承認ごとの判定（実アダプタの発注先）が
  fail-closed（見送り＋Error ログ＋通知）で塞ぐため、安全は既に担保されている。(b) 観測は届かない・古い時間帯があり、
  そのとき判定を観測に委ねると「観測が無いので許す／拒否する」のどちらかを新たに決める必要が生じ、関門の根拠が弱まる。
  (c) 発注先の設定値はまだ発注経路を動かさない（FR-12 の未決事項）ため、設定値と構成の一致を強制する機構は、結線の issue で扱う方が筋がよい。
- 運用を明記する: `docs/functional/FR-10_risk-controls.md` に「S0 以外を選ぶ前に、設定上の発注先を発注執行の構成（`Broker:Provider`・環境）と揃える」
  「揃っていないとき、S2 等を保存できても内蔵 paper では全件見送り（通知あり）になる」を書く。

### 項目 4（IADR-0342 への追記）

- IADR-0342 冒頭へ日付つき追記ブロック（`［2026-09-25 追記 / #826］`）: S2 → S0 へ戻しても既存 S2 建玉は無保護／到達通知に S3 を足した／
  免除の打ち消し（IADR-0413）／設定側の判定の読み方（IADR-0413）／項目 3 は残る。

### 項目 3（見送り）

- 基底の合算は `ProtectiveStopNetting`（#977 が改修中）にあり、S2 の建玉を差し引くには S2 に保護記録（機構 S2 の行）を持たせる設計が要る。
  現構成（稼働は S1・SIMULATE では S0 の逆指値が拒否される）では発火しない。実弾・S3 常用の前に扱う。**本 PR は `Refs #826`**。

## 受け入れ基準

- AC1（T-10-896）: 損切りライン到達の通知は S0 / S1 / S2 / S3 の 4 手法の帰結を列挙し、S3 がブローカー側の注文で決済されること（システムは発注しない）が読める。
- AC2（T-10-897）: エントリーが受付のまま約定 0 で取消（失効・拒否）された S2 の免除は、監査台帳に「免除を打ち消し（建玉は生じなかった）」の記録が 1 件残る。
- AC3（T-10-898）: 一部約定で終端した S2 の免除は、監査台帳に「免除の対象は約定数 N（発注 M のうち）」の記録が 1 件残る。
- AC4（T-10-899・否定形）: 全量約定で終端した免除、および免除の無いエントリーの終端では、打ち消しの記録を作らない。受付・一部約定（非終端）でも作らない。
- AC5（T-10-900）: 到着順が逆（終端の約定記録が先・免除が後）でも、同じ記録が 1 件だけ残る。同じメッセージの再配送・両方の経路からの追記でも 1 件のまま。
- AC6（T-10-901）: 本番と同じメッセージ配線（Wolverine の監査ハンドラ）で、免除 → 受付 → 取消（約定 0）を流すと打ち消しの記録が 1 件残る。
- AC7（T-10-902）: 免除の通知と監査の要約は、数量が発注数量であり、建玉は約定で確定する（約定しないまま取消・失効すれば生じない）ことが読める。

## テスト ID（割り当て T-10-896〜T-10-909 のうち T-10-896〜T-10-902 を使う）

`git grep -E "T-10-(89[6-9]|90[0-9])"` を origin/* 全ブランチで実行し、使用 0 件を確認した（`20260925_957_...` の範囲表記 `T-10-880〜T-10-899 のうち 880〜886 を使う` の 1 件は範囲の記述で、ID の使用ではない）。

## 是正・追随の母集合（規則 9・10）

- 規則 9（S3 が落ちた列挙）: `git grep -n "S2＝\|S2=誰"`（`.ai-context/specs` を除く）→ 7 箇所。対象: `NotificationFormatter.cs`・
  `NotificationFormatterTests.cs`・`NotificationTemplateGoldenTests.cs`・`RiskManagementService/.../StopLossTriggeredHandler.cs`（2 行）・
  `docs/functional/FR-10_risk-controls.md`（注意の列挙）・`docs/tests/FR-10_risk-controls-tests.md`（T-10-350 の説明）。
  除外: `IADR-0344:95` と `.ai-context/adr/README.md` の該当行（凍結記録。当時の決定の記述として真）。
- 規則 9（免除＝建玉を保持と読ませる記述）: `git grep -n "建玉を保持"` のうち免除の文脈 → 契約コメント（`ProtectiveStopWaived.cs`）・
  `StopLossMethodPolicy.cs:71`・`docs/api/events-and-ports.md:51`・`docs/functional/FR-10_risk-controls.md` の S2 行・通知と監査の要約。
  `StopLossMethodPolicy.cs` の要約（「保護逆指値を発注せず建玉を保持し」）は発注時点の扱いとして真＝除外。契約コメント・イベント表・FR-10 の S2 行へ
  「数量は発注数量・打ち消しは監査台帳」を追記する。
- 規則 10（自分の記述）: 到達通知の文面が変わるため、ゴールデンテストと FR-10 のテスト仕様書（T-10-350 の説明）を引き直す。
  打ち消しの記録は新しい EventType であり、`AuditConsumerCoverageTests`（契約イベント全数とハンドラの対応）は契約に型を足さないため影響しない。
- 規則 11（窓）: 窓＝「免除の記録」と「終端の約定記録」の到着の間。増える側（打ち消しが要るのに残らない）＝終端が先に届く順序、
  減る側（打ち消しが要らないのに残る／2 件残る）＝全量約定・両経路からの二重追記。
  形は ①終端側だけで判定（免除が後に届くと残らない）②免除側だけで判定（免除時点では終端が無く残らない）③両側で判定し決定的 Id で 1 件へ畳む（採用）。
  ③ を T-10-897 / T-10-900 で実測、① ② は T-10-900 の片側を外す変異で赤になることを確かめる。

## 残余リスク

- 項目 3（S0 と S2 の同一銘柄併存時のガード）は残る。
- 打ち消しは監査台帳の派生記録であり、Discord 通知は出さない（終端の約定通知「約定 Cancelled 数量0」は従来どおり出る）。
- 約定追跡の期間を過ぎても非終端のまま残った注文には打ち消しが付かない（終端の約定記録が来ないため）。
