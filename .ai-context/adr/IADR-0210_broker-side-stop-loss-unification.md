---
title: IADR-0210 損切りはブローカー側逆指値へ一本化し、発注執行が保護レグの同時発注・建玉解消・失効ガードまで持つ
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, UC-01, UC-02, ADR-0002, ADR-0016, IADR-0015, IADR-0057, IADR-0113, IADR-0117, IADR-0118]
author: claude (Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md (決定2(b))
---

# IADR-0210: 損切りはブローカー側逆指値へ一本化し、発注執行が保護レグの同時発注・建玉解消・失効ガードまで持つ

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: claude（起票 #331。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: FR-10（損切りの実行機構＝ブローカー側逆指値。検知・記録・通知のみ・決済注文を発行しない・
  逆指値なしの建玉を持たない）、FR-05、UC-01/UC-02、ADR-0016 決定 2(b)（同時発注必須・方向を問わない）
- 対象 Issue: #331（親 #344 フェーズ 2。旧 #292 / #304 を吸収）
- 関連する実装仕様書: [20260828_331_order-execution-stop-loss-and-rejection](../specs/20260828_331_order-execution-stop-loss-and-rejection.md)
- 関連 IADR: [IADR-0015](IADR-0015_stop-loss-mechanical-close.md)（旧機構。本 IADR が Supersede）、
  [IADR-0057](IADR-0057_order-dispatch-idempotency.md)（発注 3 相）、[IADR-0113](IADR-0113_moomoo-fill-polling.md)（約定追跡）、
  [IADR-0117](IADR-0117_owner-position-close-path.md)（owner 決済）、[IADR-0118](IADR-0118_broker-position-reconciliation.md)（建玉突合）

## コンテキストと課題

利用者裁定（planning#88・2026-07-31）で損切りの実行機構は**ブローカー側の逆指値に一本化**され、FR-10・UC-02・
業務フロー 02 は fixed で反映済みである。現行実装は旧計画のまま: 市場監視のソフト検知（`StopLossTriggered`）を
リスク管理が購読し、`StopLossExecutionService` が **Close の `OrderApproved` を発行**する（IADR-0015）。
ブローカー側逆指値の発注はどこにも無い（moomoo クライアントは指値 `OrderType_Normal` のみ）。この状態は
(1) 系停止中に損切りが効かない（NFR-04 の裁定理由）、(2) 逆指値を導入した瞬間に二重決済（システム決済＋
ブローカー逆指値）になる、という二重の問題を持つ。

決めるべきは (a) 逆指値レグの発注・追跡・台帳結線をどの経路に載せるか、(b) 「逆指値が未受理・失効したら
建玉を持たない」の対処をどこが担うか、である。

## 検討した選択肢

1. **リスク管理が逆指値レグの `OrderApproved` を追加発行し、発注執行は 1 注文 = 1 承認のまま** —
   エントリーと逆指値が別メッセージになり、「同時」の保証（エントリーだけ約定し逆指値が届かない窓）が
   メッセージングの再送・順序に依存する。未受理時の建玉解消もリスク管理へ往復する。**却下**。
2. **発注執行がエントリーと逆指値を 1 承認から対で発注し、逆指値レグを自サービスの記録・追跡へ載せる**（採用）。
3. moomoo の複合注文（PlaceComboOrder・ブラケット）を使う — SIMULATE での対応が未確認（#342 PoC 前）であり、
   paper アダプタに同等物が無く、判断・記録・報告のフローを paper と同一に保てない。**却下**（PoC 後の再検討は妨げない）。

## 決定

1. **保護逆指値はエントリーと同一の `OrderApproved` 処理内で発注する**（`OrderExecutionService.ExecuteAsync`）。
   能力ポート `IProtectiveOrderBroker`（逆指値発注＋成行手仕舞い）を Shared.Contracts に置き、moomoo
   （`OrderType_Stop` + `AuxPrice`）と paper（滞留 Accepted / 即時約定）が実装する。
   **Open 注文は `StopLossPrice` が無い・ブローカーに逆指値能力が無い場合、発注せず見送る**（fail-closed。
   建玉を作らない側へ倒す。見送りは `OrderDispatchForgone`・[IADR-0211](IADR-0211_opend-unavailable-forgo-without-queueing.md)）。
2. **逆指値レグは `ExecutionRecord` として保存し、既存の約定追跡ポーリング（IADR-0113）にそのまま載せる。**
   StopDecisionId はエントリー DecisionId から決定的に導出（`ProtectiveStopIds`・試行番号つき）。
   台帳への結線は新イベント **`ProtectiveStopPlaced`**（Close Intent 同伴）をリスク管理が購読して
   `AppendApproval` する形とする —— `OrderApproved` を再利用すると発注執行自身が購読して**二重発注**する
   （相 1 の冪等で実発注は防げるが、意味の違うイベントを同名で流すことになる）ため、専用イベントに分ける。
   逆指値がブローカー側で約定（＝損切り成立）すると、ポーリングの `OrderExecuted` が台帳の建玉を減らす。
   **決済の観測経路を増やさない**（IADR-0117 と同じ規律）。
3. **未受理時の建玉解消は発注執行がその場で行う**（業務フロー 02 の表のとおり）: エントリー未約定なら取消、
   約定済みなら成行手仕舞い（Close レグも決定的 DecisionId・台帳結線は `ProtectiveStopCoverageLost` の
   Close Intent で行う）。解消も失敗した場合は Remediation=None の `ProtectiveStopCoverageLost` を発行し
   Critical 通知（人手対応）。逆指値・手仕舞いレグは**リスク管理のスクリーニングを通さない**
   （Close は統制で止めない・FR-10。IADR-0015 の規律を執行側で維持する）。
4. **失効検知・残存取消は発注執行の常駐 `ProtectiveStopGuard` が担う**（moomoo 構成のみ配線）。
   Active な逆指値を巡回し、(a) 失効（Cancelled/Rejected/Expired）かつ建玉残あり → 再発注、不可なら成行手仕舞い、
   (b) 建玉消滅（owner 決済・自動縮小・強制買戻し等）かつ逆指値が滞留 → **逆指値を取り消す**（決済後に残る
   注文が反対建玉を生む事故の防止）、(c) 照会不能（注文 null / 建玉 null）→ 据え置き（不明を「無い」と
   取り違えない・IADR-0118 と同じ）。**計画の業務フロー 02 は検知を市場監視・再発注指示をリスク管理に
   描くが、本実装は両方を発注執行へ置く** —— ブローカー注文状態と建玉の照会経路（OpenD 接続）を持つのが
   発注執行だけであり、規則（検知・再発注・不可なら手仕舞い・逆指値なしの建玉を持たない）は計画どおりで、
   サービス配置のみの差異である。計画側シーケンス図への環流候補として親へ報告する。
5. **リスク管理の `StopLossTriggeredHandler` は決済発行を除去し、検知の記録（ログ）のみとする。**
   `StopLossExecutionService` は削除する。市場監視のソフト検知（`StopLossTriggered`）と監査・通知の購読は
   存置する（計画の「検知・記録・通知のみ」の経路そのもの）。**[IADR-0015](IADR-0015_stop-loss-mechanical-close.md)
   は Superseded by IADR-0210**（無条件執行の規律は決定 3 が引き継ぐ。「システムが決済注文を発行する」部分だけが覆る）。
6. **逆指値レグの永続化は発注執行の専有 DB に新テーブル `protective_stop_orders`**（EntryDecisionId 主キー・
   試行番号・状態 Active/Completed）。ガードの巡回対象の洗い出しと再発注の冪等（同一試行番号は 1 回）の権威。

## 理由

- 「同時発注」と「未受理なら建玉を持たない」は**エントリーの発注結果を見て初めて分岐できる**。エントリーを
  発注した当のサービスがその場で対処するのが、分岐（未約定→取消／約定済→手仕舞い）を最短・無競合で実装できる形である。
- 逆指値レグを `ExecutionRecord` ＋既存ポーリングへ載せることで、約定の観測・台帳反映・スリッページ記録・
  監査の**すべてを既存経路で再利用**でき、新しい観測経路（＝新しい取りこぼし方）を作らない。
- ガードを moomoo 構成に限るのは、判定の前提（ブローカー注文照会・建玉照会）を paper が持たないためである。
  paper の逆指値は滞留 Accepted であり、Stage 0 の検証で分岐（未受理・失効）をフェイクで注入して固定する。

## 残余リスク

- **クラッシュ窓**: エントリー発注後〜逆指値発注前にプロセスが落ちると、逆指値なしの建玉が残る。
  予約（IADR-0057）は Reserved のまま残りリコンサイルが終端化するが、保護レグは張られない。
  検知は建玉突合（IADR-0118・`PositionReconciliationDrift`）と Critical 通知に委ね、自動再保護は実装しない
  （リコンサイル復元の Intent には StopLossPrice が無く、誤った価格で自動発注する方が危険）。頻度は
  再送窓 1 回分に限られる。#342 PoC 後、実測頻度で再評価する。
- **SIMULATE が `OrderType_Stop` を受理しない可能性**（計画 03_moomoo-integration「模擬取引は指値・成行のみ」）。
  その場合、本実装は設計どおり**建玉を一切作らない**（安全側だが Stage 1 が進まない）。#342 の PoC 項目で
  確認し、結果に応じて計画へ環流する（実装側で勝手に逆指値必須を外さない）。
- ガードの再発注は巡回間隔ぶん遅れる。その間はブローカー側の保護が無い（計画も「システムが検知して再発注する」
  としており、遅延自体は計画の想定内。間隔は構成で短縮可能）。
