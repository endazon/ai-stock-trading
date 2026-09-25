---
title: IADR-0413 損切りの実行機構の設定側判定は観測へ寄せず運用で揃え、S2 の免除の打ち消しは監査台帳の派生記録で残す
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11, FR-12, UC-02, UC-06, ADR-0040, IADR-0019, IADR-0141, IADR-0161, IADR-0342, IADR-0344, IADR-0347]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1・決定3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 口座種別の軸 / FR-11 監査ログ)
---

# IADR-0413: 損切りの実行機構の設定側判定は観測へ寄せず運用で揃え、S2 の免除の打ち消しは監査台帳の派生記録で残す

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 #826。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #826（#819 / PR #825 の監査の非ブロッキング指摘）の項目 1 と項目 5。
- 関連する実装仕様書: [20260925_826_stop-method-audit-followups](../specs/20260925_826_stop-method-audit-followups.md)
- 関連 IADR: [IADR-0342](IADR-0342_simulate-stop-loss-method-selection.md)（選択機構・決定 2 / 4 / 5 / 6。本 IADR は覆さず補う）、
  [IADR-0019](IADR-0019_audit-log-service.md)（監査台帳）、[IADR-0347](IADR-0347_alternative-broker-order-types-for-simulate.md)（S3）

## コンテキストと課題

1. **設定側の判定が発注側より緩い**（項目 1）: 手法の変更の受理（`StopLossMethodChange.Evaluate`）は**設定上の発注先**
   （`RiskManagementSettings.BrokerProvider`）が moomoo REAL のときだけ S0 以外を拒否する。発注執行は**実際のアダプタ**の発注先で
   判定し（IADR-0342 決定 4-2）、moomoo SIMULATE 以外なら見送る。稼働では設定上の発注先が内蔵 paper のまま、発注執行は
   moomoo SIMULATE へ発注している食い違いも観測されている。設定上の発注先は**まだ発注経路を動かさない**（FR-12 の機能仕様書 §未決事項）。
2. **免除イベントが建玉を誤表示し得る**（項目 5）: `ProtectiveStopWaived` はエントリーの受付（Accepted）時点で**発注数量**を載せて
   発行される。受付のまま約定 0 で取消・失効されても、7 年保持の監査台帳には「逆指値なしの建玉を保持する」の記録だけが残る。

## 検討した選択肢

### 項目 1

1. **設定側の判定を観測（`BrokerAccountObserved` の発注先）へ寄せる** — 利用者は保存の時点で見送りを予見できる。しかし
   観測は届かない・古い時間帯がある（OpenD 停止・起動直後）。そのとき「観測が無いので許す／拒否する」を新たに決める必要があり、
   許す側なら判定は結局設定値か素通しに戻り、拒否する側なら観測の欠落が利用者の設定変更を止める。安全は既に発注執行の
   承認ごとの判定が担っており（下の理由）、寄せても安全は増えない。**却下**。
2. **設定上の発注先を発注執行の構成と一致させる機構（起動時の突合・拒否）を足す** — 設定値を発注経路へ結線する別 issue
   （FR-12 の未決事項）と同じ関心であり、手法の選択の是正の射程を越える。**却下（本 IADR では扱わない）**。
3. **設定側の判定は現行のまま（実弾の拒否に限る）とし、揃える運用を明記する**（採用）。

### 項目 5

1. **発注執行が約定（Filled / PartiallyFilled）の時点で免除を発行し直す** — 約定を観測する唯一の点は約定追跡（`OrderFillPoller`）で、
   記録（`ExecutionRecord`）は手法を持たない。手法の永続化（列の追加・移行）と約定追跡の改修が要り、並行する改修（#973）と同じ
   ファイルに入る。受付時点の免除（利用者の選択どおり逆指値を置かなかった事実）も失われる。**却下**。
2. **発注執行が取消・失効の時点で打ち消しイベント（新しい契約）を発行する** — 1 と同じく約定追跡に手法の知識が要る。**却下**。
3. **監査サービスが、同じ相関（エントリーの DecisionId）の免除と終端の約定記録から派生記録を追記する**（採用）。
   監査台帳は既に両方を同じ相関で持っており（免除の相関＝エントリーの DecisionId、`OrderExecuted` の相関＝同じ DecisionId）、
   約定追跡は終端化で `OrderExecuted` を再発行する。新しい契約も発注執行の改修も要らない。

## 決定

1. **項目 1: 設定側の判定は観測へ寄せない。揃える運用を明記する。**
   - `StopLossMethodChange.Evaluate` / `BrokerProviderChange.Evaluate` の 2 方向の拒否（IADR-0342 決定 2）は**設定上の発注先**で判定する
     現行を維持する。役割は「実弾で S0 以外が有効になる設定を作らせない」ことに限る。
   - 設定上の発注先と実際の発注先の食い違いは、発注執行の承認ごとの判定（IADR-0342 決定 4-2。実アダプタの発注先・見送り＋Error ログ＋通知）が
     fail-closed で表に出す。
   - FR-10 の機能仕様書へ「S0 以外を選ぶ前に、設定上の発注先を発注執行の構成と揃える」と、食い違ったときの 2 方向の帰結を書く。
2. **項目 5: 免除の打ち消し・数量の確定を監査台帳の派生記録 `ProtectiveStopWaiverSettled` で残す。**
   - 判定は純関数 `AuditService.Domain.ProtectiveStopWaiverSettlement.TryCreate(同じ相関の記録列)`: 免除があり、終端
     （Filled / Cancelled / Expired / Rejected）の `OrderExecuted` のうち最大の約定数が**免除の数量より少ない**ときだけ 1 件作る。
     約定 0 なら「免除を打ち消し（建玉は生じなかった）」、一部なら「対象を約定数 N に確定」。全量約定・免除なし・非終端では作らない。
   - **到着順に依らず 1 件**: `OrderExecutedAuditHandler`（終端のときだけ）と `ProtectiveStopWaivedAuditHandler` の**両方**が判定し、
     記録 Id は相関から決定的に導く（`AuditCorrelation.From("protective-stop-waiver-settled:{DecisionId}")`）。追記は Id で冪等。
   - 相関はエントリーの DecisionId、時刻は終端の約定記録の時刻（免除より前なら免除の時刻）、Detail は
     `ProtectiveStopWaiverSettledDetail`（発注数量・約定数・終端状態・OrderId・手法・発注先）。
   - 通知・監査の免除の文面は「数量＝発注数量・建玉は約定した数量で確定する」と読めるようにする。契約（`ProtectiveStopWaived`）の
     形・発行の時点・再配送で再発行しない規律（IADR-0342 決定 6）は変えない（契約コメントに Quantity の意味を追記するのみ）。
   - Discord 通知は出さない（終端の約定の通知〔約定 Cancelled 数量0 など〕が従来どおり出る。派生記録は台帳の読みの是正である）。

## 理由

- 項目 1: 設定側の関門は**安全側の片方向**（実弾で S0 以外を作らない）だけを担えばよく、それは設定上の発注先で足りる。
  発注先の実態との整合は、実アダプタを知る発注執行の 1 か所（IADR-0342 の理由）で fail-closed に判定されている。観測へ寄せると、
  観測の欠落という新しい状態に関門の意味が依存する。
- 項目 5: 台帳の誤表示は台帳で直すのが最小である。両側判定＋決定的 Id は、規則 11 の 3 通り（終端側だけ／免除側だけ／両側）のうち
  到着順の窓を両方塞ぐ唯一の形である（片側だけでは逆順で残らない。変異注入で確認した）。

## 結果・残余リスク

- 良い影響: 約定 0 で終わった S2 のエントリーについて、台帳に「建玉は生じなかった」が残り、7 年後に読んでも誤読しない。
- **約定追跡の期間を過ぎても非終端のまま残った注文**には終端の約定記録が来ないため、打ち消しが付かない（免除の記録は発注数量のまま）。
- 派生記録は契約イベントではないため、`AuditConsumerCoverageTests`（契約イベント全数とハンドラの対応）の対象外である。
  監査の種別照会（`/events/by-type`）で `ProtectiveStopWaiverSettled` を指定すれば引ける。
- 監査は終端の `OrderExecuted` ごとに相関の照会を 1 回行う（全注文。相関は索引つき）。
- 項目 1 の食い違い（設定上の発注先と構成）は運用で揃えるまで残る。設定値を発注経路へ結線する issue で再検討する。
