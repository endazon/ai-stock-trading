---
title: IADR-0539 判断由来の決済の指値が発注から N 分（既定 15 分）未約定なら、約定追跡の常駐が利用者の取消と同じ口で取り消すだけにする（既定は無効・1 建玉 1 市場の現地取引日 1 回・有効時の N は相場操縦の検知の窓より長く 390 分以下・回数は 1〜3・一部約定も残りを取り消す・印を先に書く）。moomoo の Disabled（失効）を失注（Expired）へ写し、通知・監査・ログで拒否と区別して表示する
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, FR-11, FR-19, UC-01, UC-06, ADR-0003, ADR-0007, ADR-0040, ADR-0050, ADR-0051, ADR-0067, IADR-0040, IADR-0067, IADR-0117, IADR-0357, IADR-0461, IADR-0515, IADR-0535, IADR-0536]
author: claude (Claude Code)
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0067_decision-exit-limit-unfilled-cancel-only.md (決定 1〜7・フォローアップ 1・2)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5「判断由来の決済の指値の未約定時の取消（N）」「同 回数の上限」「発注パターン禁止」)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 失注の定義・FR-19)
related_specs:
  - ../specs/20261016_1339_decision-close-unfilled-cancel.md
---

# IADR-0539: 判断由来の決済の指値の未約定時の取消（既定は無効）と、失効の「失注」表示（計画 ADR-0067・#1339）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-16
- 決定者: 計画 ADR-0067（利用者裁定 2026-10-16・planning#797）に沿って claude が起案（起点 [#1339](https://github.com/endazon/ai-stock-trading/issues/1339)。レビューは PR で受ける）

> 🔴 **本 IADR の取消は既定で無効である。有効にするのは #1214（保護逆指値の GTC）の実弾確認と、有効化の PoC の確認が済んでから、利用者が決める**（計画 ADR-0067 決定 6）。配備（Helm values）にも有効化を書かない。

## 起点・関連

- 計画: ADR-0067（決定 1〜7）。ADR-0050 決定 1 の補完。ADR-0051 決定 3（置き直し・成行を採らない理由）、ADR-0040 決定 4（#1214）、FR-05（失注）、FR-19（全注文に検知を適用）。
- 前提として覆さない IADR: [IADR-0536](IADR-0536_close-limit-day-tif-and-release-abandoned-close-on-broker-terminal.md)（DAY・終端を確かめた解放・15 分の WRN）、[IADR-0535](IADR-0535_skip-llm-while-close-in-flight-covers-position.md)、[IADR-0461](IADR-0461_close-quantity-subtracts-in-flight-closes.md)、[IADR-0040](IADR-0040_manipulation-detection-algorithm.md)（検知の窓 5 分）、[IADR-0357](IADR-0357_owner-close-market-order-cancel-path-and-expiry-notice.md)（取消の口）、[IADR-0515](IADR-0515_order-approval-origin-s1-nets-owner-closes.md)（出どころ）、[IADR-0117](IADR-0117_owner-position-close-path.md)（不明を拒否へ畳まない）。
- 作業仕様書: [20261016_1339_decision-close-unfilled-cancel](../specs/20261016_1339_decision-close-unfilled-cancel.md)（母集合・窓の表・受け入れ基準・変異）。

## コンテキスト

1. 判断由来の決済の指値が約定しないと、DAY の失効まで（最長で閉場まで）、その建玉の判断が止まる（IADR-0461 のゲート・IADR-0535 の見送り）。IADR-0536 は取消を入れなかった（最小是正）。
2. 計画 ADR-0067 は、取引判断由来の決済の指値が発注から N 分未約定なら系が取り消すだけにし、既定は無効、1 建玉 1 日 1 回、有効時の N は検知の窓より長いと決めた。N の上端・回数の上下端・一部約定の扱いは実装が定めて IADR に記録する。
3. 取消の口（`OrderAmendmentService.CancelAsync`）は既にあり、実 OpenD に配線済みである。取消はライフサイクル台帳に残り、確認できた取消は `OrderCancelled` としてリスク管理の注文アクティビティ射影（相場操縦の検知の母集団）へ入る。
4. 検知の窓（5 分）はリスク管理のコードの既定値で、発注執行からは読めない。
5. moomoo の `Disabled`（失効）は `Failed` → `OrderStatus.Rejected`（拒否）と表示されていた。

## 決定

### 決定 1: 約定追跡の常駐が巡回ごとに、利用者の取消と同じ口で取り消すだけにする

- 新しい部品 `DecisionCloseUnfilledCanceller`（`Features/OrderExecution/CancelUnfilledDecisionCloses/`）。約定追跡の常駐（`OrderFillPollingService.PollOnceAsync`）が巡回の最後に呼ぶ。**新しい常駐は足さない**（moomoo 構成だけで動く。内蔵 paper は即時終端で対象が生まれない）。
- 対象（`IExecutedOrderStore.FindUnfilledDecisionCloses`）: `PositionEffect=Close` かつ **`ApprovalOrigin=TradeDecision`** かつ 非終端（Accepted / PartiallyFilled）かつ 発注（`ExecutedAt`）≦ 今 − N かつ 系がまだ取消を求めていない（`UnfilledCancelRequestedAt` が null）。古い順・上限 `BatchSize`（既定 50）。
  - 判断由来の決済は常に指値である（`MarketOrder` を立てるのは利用者の手仕舞いだけ。全文検索で確認）。記録に注文の種別は無いが、出どころで指値に限られる。
  - **対象外**: 利用者の手仕舞い（`OwnerClose`）・維持率割れの自動縮小（`MaintenanceMarginReduction`）＝計画 ADR-0067 決定 4。**出どころ不明（`Unknown`）も対象外**とする——`Unknown` は「分からない」であり、S1 の譲り合い（IADR-0515）では損切りを止めない側＝取り消す側へ倒すが、本件の取消は損切りではなく系の能動的な操作なので、分からないものは取り消さない側へ倒す。出どころなし（保護の機構のレグ）も対象外。
- 取消は `OrderAmendmentService.CancelAsync`（理由の接頭辞 `判断由来の決済の指値が未約定のため系が取消（ADR-0067）`）。確認できた取消だけ `OrderCancelled` を常駐が発行する（IADR-0357 と同じ規律）。確認できないときは発行せず、**解放は既存の約定追跡が終端を確かめてから行う**（IADR-0536 決定 3 を変えない）。本部品は記録の状態を書き換えない。
- **置き直し・訂正・成行への切り替えはしない**。取消の後に発注は一切しない（試験は発注の口が呼ばれたら落ちる偽物で固定する）。

### 決定 2: 印を先に書く（取消の反復を作らない側へ倒す）

- 取消の**前に** `UnfilledCancelRequestedAt`（新しい列。timestamptz NULL・列の追加だけ）へ今の時刻を書く（`MarkUnfilledCancelRequested`。非終端かつ未印のときだけ書ける）。書けなければ取り消さない。
- 取消が例外で失敗しても印は残し、**再試行しない**。注文は DAY で失効する。

| 倒し方 | 起こり得る害 | 害の大きさ |
| --- | --- | --- |
| 取消の後に印を書く / 失敗したら印を消して再試行する | 取消の送信の後に落ちると、再起動後に同じ注文へ取消をもう一度送り、回数の上限も数え漏らす。取消の反復は相場操縦の検知（過剰な取消）の型そのものである | **中**（検知に掛かり得る・計画が「構造的に起きない」とした前提を壊す） |
| **印を先に書き、失敗しても再試行しない** | 取消が届かなかった注文は DAY の失効まで残る（判断が止まる時間が IADR-0536 の現状に戻るだけ） | **小** |

### 決定 3: 一部約定の決済も、残りを取り消す

- 一部約定（PartiallyFilled）のまま N 分たった判断由来の決済も取り消す。moomoo は残りだけを取り消し（`Cancelled_Part`）、約定した分は残る。`OrderCancelled.ObservedFilledQuantity` は照会で得た累積約定数を運ぶ（IADR-0357）。
- 理由: 残りが生きている間は、全量未約定のときと同じく建玉の判断が止まる（IADR-0461 のゲートは残りの数量を処理中として数える）。

### 決定 4: 回数の上限は、同じ建玉の判断由来の決済の印を市場の現地取引日で数える

- 建玉の識別は **銘柄・市場・決済の方向**（売りの決済＝買い建玉）。商品種別（現物・信用）は分けない（少なく取り消す側）。
- 「1 日」は**今の時刻が属する市場の現地取引日**（米国株は米国東部の暦日・日本株は JST の暦日。`MarketHours.ZoneOf`。サマータイムは `TimeZoneInfo` が吸収）。その日の範囲 [始まり, 次の日の始まり) に `UnfilledCancelRequestedAt` がある件数が上限以上なら取り消さず、DAY の失効に任せる（INF を注文ごとにプロセス内で 1 回）。
- 取り消した後に終端した記録も数える（状態を問わない）。

### 決定 5: 構成と範囲（有効なときだけ検証し、起動で止める）

| 構成（`DecisionCloseUnfilledCancel:`） | 既定 | 有効時の範囲 | 根拠 |
| --- | --- | --- | --- |
| `Enabled` | **false** | — | 計画 ADR-0067 決定 2・6 |
| `UnfilledMinutes`（N） | 15 | **6〜390**（検知の窓 5 分より長い・上端 390） | 下端は決定 2（窓は発注時刻で切るので、窓より長い N の取消は窓の外）。上端は米国の通常取引時間（6.5 時間）。これより長い N は DAY の失効より後になり、取り消す機会が無い |
| `MaxCancelsPerPositionPerDay` | 1 | **1〜3** | 下端は §5「正の整数」。上端は取消の反復を構造的に起こさない（決定 3）ための小さな値。検知の最小標本数（5）に 1 建玉で届かない |
| `BatchSize` | 50 | 1〜1000 にクランプ | 暴走の歯止め（保有建玉の数で頭打ち） |

- 範囲外は `IValidateOptions` + `ValidateOnStart` で**起動を止める**（黙ってクランプしない＝統制値を書き間違えたまま動かさない）。**無効なら検証しない**（下端は「有効時の N」に掛かる）。
- 検知の窓と既定値は `Shared.Kernel` の `DecisionCloseUnfilledCancelDefaults` に置く。リスク管理の `TradingDefaults.CreateManipulationDetectionSettings` は窓をこの定数から作り、`TradingDefaults` は取消の既定値を同じ定数から公開する（`TradingDefaultsTests` が計画の値で固定し、既定の N が窓より長いことも確かめる）。**窓を変えると N の下端も一緒に動く**（計画 ADR-0067 決定 2「窓の値を変える場合は N との関係を確かめ直す」の機械化）。発注執行は `Shared.Kernel` を新たに参照する（他の 7 サービスと同じ。依存規則〔IADR-0256〕の許す共有物）。

### 決定 6: moomoo の Disabled（失効）を失注（Expired）へ写し、表示で拒否と区別する

- `MoomooOrderState.Expired`（列挙の末尾に足す）。`MMApiMoomooTradeClient.MapState(22)` → `Expired`、`MoomooBrokerAdapter.MapState(Expired)` → `OrderStatus.Expired`。両方の `IsTerminal` に足す。
- `SubmitFailed`（3）・`Failed`（21）・`Deleted`（23）は `Failed` → `Rejected` のまま（計画 ADR-0067 決定 5 が決めない）。
- **終端としての扱いは変わらない**: `OrderStatusLifecycle` は `Expired` も `Rejected` も未約定の終端とする（在庫の解放・S1 の再武装・手仕舞いの未約定残の通知が同じに動く）。変わるのは相場操縦の検知の母集団に失効が入ること（`IsCancelledWithoutFill`）だけで、計画 ADR-0067 決定 5 の想定どおりである（閉場時の失効は発注から時間がたっており窓の外）。母集合は作業仕様書 §母集合。
- 表示: `Shared.Contracts` の `OrderStatusDisplay.Describe`（例 `Expired（失注: 期限切れで失効）`・`Rejected（拒否: 証券会社が受理せず）`）を、通知（取引実行・手仕舞いが約定せず終了）・監査の要約（`OrderExecuted`）・約定追跡のログ（解放・未約定の WRN）・本件の取消のログで使う。**列挙名は残す**（ログの grep・既存の運用手順を壊さない）。注文状態を出す画面（SC）は無いので、画面の変更は無い。

## 窓の形の比較（規則 11。実測）

| 窓 | 形 | 結果 |
| --- | --- | --- |
| 取消（発注＝前の端・終端の観測＝後の端） | 前の端だけ | 終端の注文へ取消を送る（変異 MT-C2 で T-10-2677 が赤） |
| 同上 | 後の端だけ | 発注直後に取り消す（変異 MT-C1 で T-10-2671・T-10-2677 が赤） |
| 同上 | **両端** | 採用 |
| 回数（1 日） | 上限を見ない | 変異 MT-C4 で T-10-2672・T-10-2674 が赤 |
| 同上 | UTC の暦日 | 変異 MT-C5 で T-10-2672 が赤（UTC では翌日・米国東部では同じ日に 2 本目を取り消す） |
| 同上 | 過去 24 時間 | 翌取引日の 1 本目を取り消せない（T-10-2672 の D− が期待に反する） |
| 同上 | **市場の現地取引日** | 採用 |

## 却下した案

- **出どころ不明（`Unknown`）も取り消す**: 利用者の手仕舞いを取り違えて取り消し得る（決定 4 に反する）。
- **取消の後に印を書く・失敗したら再試行する**: 決定 2 の表。
- **N を巡回の回数で持つ**: 計画 ADR-0067 決定 2 が分で書くと決めた。
- **範囲外の値をクランプする**: 統制値の書き間違いが黙って別の値で動く。起動で止める。
- **検知の窓を発注執行の構成に複写する**: 2 か所に置くと片方だけ変わる。`Shared.Kernel` の 1 か所に置く。
- **取消の後に Discord へ通知する**: 計画は「取り消した後は何も出さない（次の巡回に任せる）」とした。WRN ログと監査（ライフサイクル台帳・`OrderCancelled`）に残る。

## 帰結

- 既定（無効）では挙動は 1 バイトも変わらない（ストアも読まない）。変わるのは、失効が `Expired（失注…）` と表示されることと、相場操縦の検知の母集団に失効が入ることだけである。
- 有効にすると、判断由来の決済の指値が N 分約定しないとき、その日のうちに次の巡回の判断（LLM）が現在値で判断し直せる（取消を確かめた後）。
- 照会・取消の量: 有効時、取消は保有建玉の数 × 1 日あたり上限で頭打ち。

## 残余リスク

1. **有効化は #1214 の実弾確認と PoC の確認の後**である。それまで計画 ADR-0067 決定 7 の「現在の実現手段」は「実装済み・既定で無効」であり、統制は配備で働かない（暫定手段は DAY の失効と 15 分の WRN を見た利用者の手での取消のまま）。
2. 取消と約定が競合して、取消を送った後に約定し切ることがある。その場合は約定として記録される（既存の約定追跡）。回数には数える。
3. SIMULATE で `TrdModifyOrder`（取消）が本経路から正しく働くかは、有効化の PoC で確かめる（取消の口は利用者の取消で配線済み）。
4. 取消の確認が取れないまま（`Cancelling_*`）残る注文は、約定追跡が終端を確かめるまで処理中として数えられる（IADR-0536 決定 3 と同じ）。
