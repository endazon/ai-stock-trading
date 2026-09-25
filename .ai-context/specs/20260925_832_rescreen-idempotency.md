---
title: 承認済みの取引判断が再配送されたら再審査しない（#832 項目 2）と、未約定の算入（#829）の記録・表示の追随（項目 1・4）
type: spec
status: accepted
related_ids: [FR-05, FR-10, FR-11, FR-19, SC-03, UC-01, UC-02, ADR-0009, ADR-0016, IADR-0007, IADR-0057, IADR-0067, IADR-0129, IADR-0148, IADR-0158, IADR-0159, IADR-0211, IADR-0255, IADR-0346, IADR-0398, IADR-0407]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 1 日あたりの発注金額上限＝新規建ての発注代金の合計 / FR-05 注文状態「拒否」)
  - planning:projects/ai-stock-trading/05_screens/01_screens.md (SC-03 統制状態参照)
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_trading-controls-priority.md (手仕舞いは止めない)
---

# 仕様書: 承認済みの取引判断の再配送を再審査しない ほか（#832）

## 起点

- #832（PR #831 / #829 の監査で残った非ブロッキングの論点 4 件）と、同 issue のトリアージコメント（2026-09-23）。
- 本 PR の割り当て: ブランチ `fix/FR-10-832-rescreen-idempotency`・IADR-0407・テスト ID `T-10-870`〜`T-10-879`。

## 着手前の再検証（現行 develop `9a1014f5`・`git rev-parse --is-shallow-repository` → `false`）

| # | 判定 | 根拠（本 PR 着手時に自分で読んだ位置） |
| ---: | --- | --- |
| 1 | **残** | `IADR-0211` に `0346` の出現 0 件。決定 2 は「`Rejected` は 2 事象に限定」のまま。`OrderStatus.cs:12-17` の XML doc は「ブローカーへ到達した後にブローカーが拒否した終端状態」のまま。一方 `IOrderActivityStore.RecordForgone`（`:31-41`）と `OrderDispatchForgoneActivityHandler` は見送りを `Rejected` で終端化している |
| 2 | **残** | `OrderScreeningService.Screen` は DecisionId を見ずに毎回スナップショットを組む。`TradeDecisionMadeHandler` も同様。自分の `OrderApproved` が台帳（`approved_orders`）と注文アクティビティへ射影された後に同じ `TradeDecisionMade` が再配送されると、`IWorkingEntryOrderSource` が自分自身を未終端の新規建てとして返し、保有建玉数・日次枠・段階資金に算入される |
| 3 | **事実は確認。ただし「統制の欠落」ではない**（下の §項目 3） | |
| 4 | **残** | `RiskStatusView.cs:33,42` の XML doc、`docs/screens/20260718_SC-03_control-status.md:76`、`ControlStatusPage.tsx` の上限使用率パネル、いずれも未約定の算入に触れない。加えて `docs/data/risk-management-aggregates.md` の `PortfolioSnapshot` 表は `DailyOrderedAmount` を「新規建ての約定のみを積む」と書いており**#829 以降は偽**（規則 9 の走査で新たに見つけた） |

## 項目 2 の設計（IADR-0407）

- `OrderScreeningService.Screen` は、**新規建て（`PositionEffect.Open`）の判断に限り**、評価の前に
  `IPortfolioLedgerStore.FindApprovedPositionEffect(decision.DecisionId)` を引く。承認行があれば
  **再審査せず** `ScreeningOutcome.ApprovedReplay(...)`（新しい第 3 の形）を返す。ロックアウトの設定・観測・判定コアのいずれも走らせない。
- `TradeDecisionMadeHandler` は第 3 の形を**最初に**見て、**何も発行せず・観測も記録せず・審査メトリクスも刻まずに**戻る（ログ 1 行）。
- 根拠: 台帳の承認行は**自分の `OrderApproved` の射影**であり、行があることは「同じ判断の承認を発行済み」を意味する。
  発注執行は DecisionId で予約するため（IADR-0057 / IADR-0398）、仮に発行し直しても二重発注にはならないが、
  **承認時点の設定（損切りの実行機構）を再構成できない**（台帳に無い）ため発行し直さない。
- **手仕舞い（Close）は対象外**。再審査して承認を発行し直しても発注執行が DecisionId で止める。抑止すると、最初の発行が
  一部の宛先へ届かなかった場合に手仕舞いが出ない側へ倒れる（ADR-0009 の不変条件に反する向き）。読み取りも新規建てに限ることで、
  台帳の読み取りの失敗が手仕舞いの審査を新たに巻き込まない（#935 の損切り供給と同じ規律）。
- 拒否済みの判断の再配送は**従来どおり再審査する**（台帳に行が無い）。本 issue の射程外。

### 規則 11（窓）: 増える側・減る側のプローブと 3 通りの形

窓 = 「最初の承認の発行」から「自分の台帳・注文アクティビティへの射影」までの間。

- **増える側のプローブ**（再配送で拒否へ反転する）: 保有建玉数の上限 1・保有なし。判断 D を承認 → 自分の `OrderApproved` を
  射影 → D を再配送。**是正前**は `MaxPositionsExceeded` で拒否されることを実測した（下表。抑止を外す変異で T-10-870 が `found {RejectionReason.MaxPositionsExceeded}` で赤）。
- **減る側のプローブ**（抑止し過ぎて統制が緩む／手仕舞いが止まる）: (a) **別の** DecisionId の新規建ては同じ状態で拒否され続けること、
  (b) 承認済みの **Close** の再配送は再審査されて承認されること、(c) 射影の**前**に届いた再配送は通常の審査を受けること。

| 形 | 増える側（D の再配送） | 減る側 (a) 別判断 | 減る側 (b) Close の再配送 | 減る側 (c) 射影前の再配送 |
| --- | --- | --- | --- | --- |
| ① 現状（DecisionId を見ない） | 🔴 **拒否**（実測: `MaxPositionsExceeded`） | 拒否（正） | 再審査・承認（正） | 通常審査（自分は未算入） |
| ② 承認行があれば新規建ての再審査を抑止（**採用**） | 抑止＝発行なし（実測・T-10-870） | 拒否（実測・T-10-871） | 再審査・承認（実測・T-10-872） | 通常審査（実測・T-10-873） |
| ③ 自分の DecisionId だけ未約定の算入から除いて再評価 | 自分の**約定分**は射影の建玉に残るため、部分約定の後は保有建玉数・段階資金で拒否へ反転し得る（形として塞がない） | 拒否（正） | 同左 | 同左 |

③ は実装せず、`PortfolioProjection` の入力（約定は DecisionId で除けない建玉へ畳まれる）から判断した（プローブ未実走であることを明記する）。
② の窓 (c) では同じ DecisionId の承認が 2 度発行され得るが、発注執行の予約（DecisionId）が 2 本目を止める（従来と同じ）。

## 項目 1・4（記録と表示の追随）

- 項目 1: `IADR-0211` 決定 2 へ日付つき追記（`order_activity` の見送りの終端に `Rejected` を用いる例外は IADR-0346 決定 5。
  FR-05 の「拒否」の集計面は `order_activity` を読まない）。`OrderStatus.Rejected` の XML doc を同旨へ。
- 項目 4: `RiskStatusView` の XML doc（`DailyOrderedAmount`・`OpenPositionCount`）、SC-03 画面仕様書、
  SC-03 の上限使用率パネルへ注記 1 行（i18n カタログ再生成）、データ仕様書の `PortfolioSnapshot` 表（偽の記述の是正）。
  🔴 **#823（損切りの実行機構の SC-03 表示）とは束ねない**——#823 は SC-02 の選択 UI と日報を伴う M 規模であり、
  本件の注記 1 行と独立に入れられる。

## 項目 3（事実確認。本 PR では直さない）

トリアージの主張「本番では `shortSellContext` が `null` であり、空売りの 10%／50% 上限は一度も評価されていない」を確認した。

- `OrderScreeningService.Screen` は `RiskEvaluator.Evaluate` に `shortSellContext` を渡さない（既定 `null`）。
  `RiskEvaluator.Evaluate` の本番の呼び出し元はこの 1 箇所だけ（`git grep "RiskEvaluator.Evaluate("`、テストを除く）。
  `ShortSellOrderContext` を組む本番コードは 0 件。
- `ShortSellEvaluator.Evaluate` は `context is null` で `BorrowUnavailable` を立てて **return** する（`:93-98`）。
  10% / 50% の判定（`:149-161`）はその後にあるため**到達しない**。→ **主張の前半は事実**。
- 🔴 **ただし統制は欠落していない**: 同じ `return` の直前で `BorrowUnavailable` が理由に入り、`RiskEvaluator` は理由が 1 件でも
  あれば拒否する。したがって**空売りの新規建ては本番で全件拒否される**（フェイルクローズ。IADR-0131 / IADR-0158 決定 4 /
  IADR-0159 決定 4 / `docs/blocked-tasks.md` の「空売りの一次ゲート」行が既に記録している既知の状態）。現金口座では
  `ProductTypeDisabled` が止める。新規建ての承認を作る本番経路は `OrderScreeningService` だけで（`new OrderApproved(` の生成は
  他に `PositionCloseService`・`MaintenanceMarginReductionService` のみ＝いずれも決済）、迂回路は無い。
- 残る論点は「借株照会の供給元を実装して文脈を組む日に、10% / 50% のエクスポージャへ未約定の空売りを算入するか」であり、
  供給元の実装が前提である。`docs/blocked-tasks.md` はこの供給元の追跡先を #331（**クローズ済み**・供給元は未実装）/ #342 と書いており、
  専用の open issue が無い。→ **#967 として起票し、#832 からは切り出した**（本 PR のコードは変えない）。
  `docs/blocked-tasks.md` の「空売りの一次ゲート」行の追跡先を #967 へ付け替えた（#331 はクローズ済みのため）。

## 受け入れ基準

- AC1（T-10-870・否定形・最重要）: 承認済み（自分の `OrderApproved` を射影済み）の新規建ての判断が再配送されても、
  拒否へ反転せず、`OrderApproved` も `OrderRejected` も発行しない。
- AC2（T-10-871・否定形）: 同じ状態で**別の** DecisionId の新規建ては従来どおり拒否される（統制を緩めない）。
- AC3（T-10-872）: 承認済みの手仕舞い（Close）の再配送は従来どおり再審査して承認する（抑止しない）。
- AC4（T-10-873）: 射影の前（台帳に承認行が無い）に届いた再配送は通常の審査を受ける。
- AC5（T-10-874）: 抑止した再配送は観測ログを書かない（審査メトリクスも刻まない。メトリクスは静的な計器を並行テストと共有するため
  否定形の自動テストは置かず、コードの早期 return で担保する）。
- AC6（T-10-875）: 本番と同じメッセージ配線（`TradeDecisionMadeHandler`）で、抑止した再配送の発行は 0 件・例外なし
  （kill switch 起動中に流す——再審査していれば `KillSwitchActive` の拒否が出る形）。
- AC7（T-10-876）: SC-03 の上限使用率パネルに、未約定の新規建てを含む旨の注記が出る。

## テスト ID（割り当て T-10-870〜T-10-879 のうち T-10-870〜T-10-876 を使う）

| ID | 内容 |
| --- | --- |
| T-10-870 | AC1（審査サービス単体＋射影の通し） |
| T-10-871 | AC2 |
| T-10-872 | AC3 |
| T-10-873 | AC4 |
| T-10-874 | AC5（T-10-875 と同じテストで観測ログが未供給のままであることを見る） |
| T-10-875 | AC6 |
| T-10-876 | AC7（フロントエンド） |

## 是正・追随の母集合（規則 9・10）

- 審査の再実行を前提にした記述: `git grep -n "再審査\|審査そのものは再走"`（`.ai-context/specs` を除く）→ 0 件（本件で偽になる記述なし）。
  `TradeDecisionMadeHandler` 冒頭の「発行が失敗して再送されても DecisionId で冪等」（観測の記録）は真のまま。
- `ScreeningOutcome` の利用者: 本番は `TradeDecisionMadeHandler` 1 箇所だけ。テスト 10 ファイルは既存の 2 形だけを作る（追随不要）。
- `Rejected` の意味を述べる記述: `git grep "証券会社が受理しなかった\|ブローカーへ到達した後\|ブローカーが拒否した終端"` → 23 件。
  対象は `OrderStatus.cs:15` と IADR-0211 決定 2 だけ。除外: 発注執行・監査・契約（`OrderDispatchForgone`・`BrokerUnavailableException`）・
  結合テストの記述は **FR-05 の集計面**（`OrderExecuted.Status`・監査の EventType）を述べており、`order_activity` の内部射影は含まない＝真のまま。
  `IPortfolioLedgerStore.MarkForgone` の「`Rejected` を捏造しない」は**取引台帳**の話で、台帳は見送りに状態を与えていない＝真のまま。
- 未約定の算入の表示: `git grep "OpenPositionCount\|DailyOrderedAmount" -- docs frontend/src` → `docs/data/risk-management-aggregates.md:117,119`
  （対象。`:119` は偽）、`docs/functional/FR-19_trading-guard.md:68`（GFV の入力名の列挙のみ。除外）。`InvestedCapital` の表示は画面に無い。
- 規則 10（自分の記述）: 本仕様書の行番号は着手時点の値。`OrderScreeningService.cs` の行番号を他文書へ転記しない。

## 変異注入の実測（2026-09-25。本件の 2 クラス 10 件に対して）

| 変異 | 結果 |
| --- | --- |
| 審査の抑止（承認行があれば戻る判定）を外す | 2 件赤（T-10-870・T-10-875） |
| 抑止の新規建て限定を外す（手仕舞いも抑止する） | 1 件赤（T-10-872） |
| ハンドラが第 3 の形を見ない | 1 件赤（T-10-875。`Rejected!` の参照で例外→再試行） |

T-10-871・T-10-873 は抑止し過ぎる側（別判断・射影前）の否定形で、上の変異では赤にならない（緑のまま＝正）。

## 残余リスク

- 最初の発行が一部の宛先（発注執行のキュー）へ届かず、自分の射影だけ済んだ状態で再配送されると、**その新規建ては出ない**
  （抑止）。発注しない側（新規建てを止める側）であり、次の定時判断で改めて審査される。未終端の算入は当日中その分の枠を食う（IADR-0346 の既存の残余リスクと同じ向き）。
- 拒否済みの判断の再配送は再審査されるため、状態が変わっていれば承認へ反転し得る（従来どおり・本件の射程外）。
