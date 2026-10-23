---
title: 決済の建玉ゲートが組み立て次第で外れ、ガードは逆指値の取消を確かめずに保護記録を完了させる（#1423 L-1・L-2。ゲートは _positions で判定し、ガードは取消の後に照会で終端を確かめ、保護記録を楽観並行で当て直す）
type: spec
status: done
related_ids: [FR-10, UC-02, UC-06, ADR-0040, ADR-0050, ADR-0016, IADR-0210, IADR-0355, IADR-0396, IADR-0406, IADR-0428, IADR-0461, IADR-0569, IADR-0574, IADR-0575, IADR-0576]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
---

# 仕様書: 決済の建玉ゲートを組み立てに依らず効かせ、ガードは逆指値の取消を確かめてから保護記録を完了させる（#1449）

> 本仕様書は実装着手前に作成した（着手 2026-10-23。AST `origin/develop` `ba3d1546`＝#1447 マージ後）。判断の記録は IADR-0576。

## 起点（トレーサビリティ）

- 起点: #1449（親 #1423 の L-1・L-2。監査は読み取りのみ）。統制は FR-10（逆指値なしの建玉を持たない／建玉なき逆指値を残さない・保有 0 からの売りを出さない）。
- 計画: 業務フロー 02「逆指値の未受理・失効を検知 → 再発注、不可なら成行で手仕舞い」と補足の二重決済（決済済み建玉に残る逆指値が発火すると反対建玉）、ADR-0040（保護逆指値の手法）、
  ADR-0050 決定 1（処理中の決済の差し引き。S0/S3 の保護レグは引かない）、ADR-0016（実建玉との突合）。いずれとも矛盾しない（取消を確かめる・書き込みを並行に安全にするのは実装の詰めで、規則は変えない）。
  `TradingDefaults` の既定値は変えない。
- 前提（覆さない）: IADR-0355（決済は実建玉と突き合わせる）、IADR-0461（処理中の決済の差し引き）、IADR-0210 決定4（ガードの巡回）、IADR-0396（保護記録の楽観並行・`Update`）、
  IADR-0406 決定3（レグの追跡の起点）、IADR-0428（送信結果待ち）、IADR-0569（エントリーの取消は照会で確かめる＝同じ作法）、IADR-0574（閉場）、IADR-0575（決済の直列化）。
- 同梱: #1445（マージ済み）の AI レビュー 🟢「`MarketSessionGateCompositionTests.cs` 冒頭の注記が古い（引数は今は省略不可）」の是正（コメントのみ）。
- 範囲外: #1448（ガードの成行が板に残る判断の決済を取り消さない）、L-3。
- 採番: develop の IADR の最大は 0575、開いている PR は #1159（CHANGELOG・IADR なし）。本件は IADR-0576（push 直前に再確認）。試験 ID は develop の最大 T-10-3011 の次から T-10-3012〜T-10-3015。

## 原因（develop `ba3d1546` で引き直した）

1. **L-1**: `OrderExecutionAppService` の `_positions = brokerPositions ?? broker as IBrokerPositionSource`（79 行）。決済の建玉ゲートと処理中の決済の差し引きの入口（182 行の `if`・192 行の照会）は
   コンストラクタ引数 `brokerPositions` を見ていた。S1 の武装（351・808・837 行）は `_positions`。本番の `Program.cs` は両方へ同じ値を渡すので実害は無いが、
   **アダプタだけを渡す組み立てではゲートが黙って外れる**。実測: 実アダプタ（`MoomooBrokerAdapter`）を `brokerPositions` なしで組む既存の試験
   （`OrderExecutionServiceIndeterminateDispatchTests` の決済）は、是正後にゲートが効いて建玉 0 で見送るようになった（＝是正前は外れていた）。
2. **L-2a**: `ProtectiveStopGuard.EvaluateAsync` の建玉消滅の分岐（376〜379 行）は `CancelOrderAsync` → `RenewStopLegTracking` → `MarkCompleted`。照会しない。
   ポートの契約は「約定済み・未知の ID は InvalidOperationException」で、取消の窓の約定は巡回の 1 件の失敗になっていた（次の巡回で Filled の分岐が閉じる）。
3. **L-2b**: ガードの保護記録の書き込みは 5 箇所すべて `stops.Save`（無条件の上書き）: 張り直しの成功（547）、送信結果待ちの採用（745）、送信結果待ちへの移行（969）、
   拒否された成行の試行の前進（1018）、`MarkCompleted`（1130）。`IProtectiveStopOrderStore.Save` の注記は「無条件のまま残しているのは常駐ガードの経路」と書いていた。

## 母集合（規則 9・10。誤りの側の文字列で走査した）

- `brokerPositions`（`OrderExecutionAppService.cs`）: 61・79・179（注記）・182・192 行。是正は 182・192（179 の注記も直す）。他のクラスに同名の引数の取り違えは無い（`grep -rn "brokerPositions is not null"` は 0 件）。
- `stops.Save(` / `protectiveStops.Save(`（発注執行の本体）: ガードの 5 箇所＋`OrderExecutionAppService` の 6 箇所（すべて `new ProtectiveStopOrder(` ＝新しい行の作成）。是正はガードの 5 箇所。
  新しい行の作成は無条件の保存のままでよい（並行に進んだ状態が無い）。
- `MarkCompleted(`（ガード）: 329・378・393・411・794・994 行の 6 呼び出し。すべて戻り値を見るように直す。
- 「無条件」の記述: `IProtectiveStopOrderStore.cs` の `Save` の注記、`docs/tests/FR-10_risk-controls-tests.md` の残余リスク（2051 行）、
  `ProtectiveStopOptimisticConcurrencyTests.cs` 237 行の注記。3 つとも直す。IADR-0396 本文は凍結記録なので書き換えない（IADR-0576 が改める）。
- 取消の後に完了させる経路（`CancelOrderAsync` の呼び出し）: ガード 1・`SoftwareStopExecutor` 2（判断の決済の取消〔IADR-0466〕と発動時のエントリーの残りの取消。どちらも取消の後に照会している）・
  `OrderExecutionAppService` 1（未受理時のエントリーの取消。IADR-0569 で照会して確かめ済み）・`OrderAmendmentService` 1（照会して確かめ済み）。是正はガードの 1 箇所。
- 是正で新たに誤りになる自分の記述（規則 10）: 試験の偽物の `CancelOrderAsync` が照会の状態を変えない（取消しても `Accepted` のまま）ものは、是正後のガードでは完了しない。
  走査（ガードを巡回させる試験の偽物）: `ProtectiveStopGuardTests`・`ProtectiveStopGuardEntryStateTests`・`ProtectiveStopGuardSoftwareStopTests`・`SoftwareStopBlockingRegressionTests`・
  `BrokerStopLegFillTrackingTests`・`ProtectiveStopGuardCompletionBookkeepingTests`・共有の `StopLegScriptedBroker`。いずれも「取消した注文は照会で取消済みに見える」へ直す（本物の証券会社と同じ）。

## 方式

1. **L-1**: 182・192 行を `_positions` へ揃える（IADR-0576 決定1）。
2. **L-2a**: 取消の後に `GetOrderAsync` で照会する（決定2）。
   - `Cancelled` / `Rejected` / `Expired` → `RenewStopLegTracking`（部分約定を拾う）→ 完了。
   - `Filled` → 約定として扱う（`RenewStopLegTracking` で約定追跡の窓へ戻す・Error のログ）→ 完了。
   - null・非終端 → 完了させない（`Outcome.Unknown`・Warning）。次の巡回は同じ評価に入り、生きていれば取り消し直し、終端なら既存の失効・約定の分岐が閉じる。
   - 取消の `InvalidOperationException`（約定済み・未知の ID）は照会へ回す（他の例外は従来どおり巡回の 1 件の失敗）。
3. **L-2b**: ガードの書き込みを `ProtectiveStopStoreUpdates.Update`（読み直して当て直す・`TrySave`）へ（決定3）。
   - 完了: 最新の行が同じレグ（`StopDecisionId`・`StopOrderId`・`Attempt`）で `Active` のときだけ完了を当てる。既に閉じていれば完了として扱う。違えば書かず、次の巡回で評価し直す。
     S1 の未到達の完了は、最新の残保護数量が 0 以下で未確定の外部要因が無いときだけ。
   - ブローカーへの操作の後（張り直し・採用・送信結果待ち・試行の前進）: 変えた列だけを**条件なしに**最新の行へ当て直す（保存を落とさない）。
   - 成行で手仕舞えた（記録あり）のに完了を当てられなかったとき: 事実（`PositionClosed`）は出さず `Unknown` を返す。次の巡回は同じ試行の記録から閉じ直し、そこで 1 回だけ出す。
   - ブローカーへの操作の後の当て直しの衝突が尽きたら、最新の行へ当てて無条件に保存する（Warning）。完了は例外のまま。
   - ［2026-10-23 追記 / PR #1450 の AI レビュー 🟡 2 件・🟢 1 件］上の 2 点と、T-10-3012 の一部約定のまま取消待ちのケースを足した（T-10-3016・T-10-3017・変異 CC6・CC7）。

## 受け入れ基準と試験

| 基準 | 試験 | 変異（外すと赤） |
| --- | --- | --- |
| アダプタだけの組み立てで決済の建玉ゲートが効く | T-10-3015 | CC5（`brokerPositions` に戻す） |
| 取消が未確認なら完了しない（非終端・照会不能） | T-10-3012 | CC1（照会の結果を見ない） |
| 取消の窓の約定を約定として扱う | T-10-3013 | CC2（約定済みの失敗を照会に回さない） |
| 並行更新で古い写しで上書きしない（完了・張り直し） | T-10-3014 (a)(b) | CC3（レグの一致を見ない）・CC4（古い写しから作る） |
| 衝突が尽きても張り直しの注文 ID を記録に残す | T-10-3016 | CC6（無条件の保存へ倒さない） |
| 手仕舞いの事実は閉じた巡回で 1 回だけ出す | T-10-3017 | CC7（閉じられなくても出す） |

変異は作業ツリーで 1 つずつ当てて `ProtectiveStopGuardTests`・`OrderExecutionServiceCloseVsBrokerPositionTests` を回し、すべて赤を確かめた（プローブはスクラッチパッド・コミットしない）。

## 検証

- `dotnet build backend/Services/OrderExecutionService/Tests/OrderExecutionService.Tests.csproj`（0 警告・0 エラー）。
- `dotnet test` 同（フィルタなし）を 2 回以上・失敗 0。
- `dotnet format --verify-no-changes`（発注執行の 2 プロジェクト）。文書系の検査（trace ブロック・知識グラフ・試験の追跡）。

## 残余

- 取消を確かめられない状態が続く間は巡回ごとに取消を送り直し、Warning を残す（通知はしない）。
- 取消の窓の約定で生じ得る反対建玉は、帰属不明の建玉の検知（次の巡回）に委ねる。
- PoC は市場クローズ後に配備する。
