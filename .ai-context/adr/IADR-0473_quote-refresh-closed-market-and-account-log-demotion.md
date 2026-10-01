---
title: IADR-0473 リスク管理の現在値の補充を閉場中は止め（閉場ごとに 1 回だけ引き、その値は次の開場から鮮度を数える）、発注執行の口座選択と通貨近似のログを初回だけ Information にして口座 ID を伏せる
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-10, FR-11, FR-03, ADR-0031, ADR-0043, IADR-0066, IADR-0068, IADR-0380, IADR-0434, IADR-0437, IADR-0354, IADR-0373, IADR-0469]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0031 (決定 2〜4: Finnhub の要求量の統制)
  - planning:projects/ai-stock-trading/07_adr/ADR-0043 (決定 3: 1 日の巡回回数は開場中の巡回で数える)
---

# IADR-0473: 閉場中の現在値の補充と、口座選択・通貨近似のログ（#1131・#1135）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-01
- 決定者: Claude Code（実装）。統制の値（リスク統制・取引ガードの既定値・保持期限）は変えない

## 起点・関連

- 関連する計画書 ID: FR-01（Finnhub の要求量）・FR-10（時価評価）・FR-11（運用の観測）・FR-03（市場監視の開場判定と同じ実体）
- 計画 ADR: ADR-0031 決定 2〜4（要求量の見積りと統制）・ADR-0043 決定 3（1 日の巡回回数は開場中の巡回で数える）
- 起票: [#1131](https://github.com/endazon/ai-stock-trading/issues/1131)・[#1135](https://github.com/endazon/ai-stock-trading/issues/1135)
- 関連する実装仕様書: [`.ai-context/specs/20261001_1131_1135_quiet-closed-market-and-account-log.md`](../specs/20261001_1131_1135_quiet-closed-market-and-account-log.md)
- 前提:
  - [IADR-0066](IADR-0066_market-valuation-supply-and-gate.md): 現在値は背景で補充し（`QuoteRefreshService`）、判定は手元の値（`QuoteCache`）を同期に読む。保持期限を超えた値は取得不可＝含み 0
  - [IADR-0380](IADR-0380_market-session-schedule-and-closed-protection-gap.md) 決定 1: 「いま開場か」の単一情報源は共有カーネルの `MarketHours`
  - [IADR-0437](IADR-0437_finnhub-cycle-fit-control-and-daily-premise-withdrawal.md): 開場中だけ巡回するプロセスは日次見積りを場中の分数で数える（リスク管理の補充は「24 時間巡回」として既定の 24 時間のまま、と記録していた。本 IADR でその前提が変わる）
  - [IADR-0354](IADR-0354_capital-baseline-from-broker-account.md) 決定 1・[IADR-0373](IADR-0373_currency-disproof-for-non-usd-single-market-account.md): 通貨の欄が無い応答は近似で採り、その旨を Information で残す（Warning にしない）

## 背景

- **#1131**: リスク管理の `QuoteRefreshService` は開場判定を持たず、2026-09-30 20:01–22:50 UTC（引け後）に Finnhub `/quote` を約 510 回（保有 3 銘柄 × 60 秒巡回）呼んだ。同じ時間帯の市場監視は 0 回。
- **#1135**: 発注執行は可用性 probe の巡回（既定 5 分）ごとに、口座選択（口座 ID の全桁つき）と通貨近似の 2 行を Information で出していた（一晩に各約 106 行）。

## 決定

### 決定 1: 補充は市場ごとに、閉場中は引かない（開場判定は `MarketHours`）

`QuoteRefreshService.RunOnceAsync` は建玉ごとに `QuoteSessionFreshness.ShouldRefresh(建玉の市場, いま, 手元の値の取得時刻)` を見る。
開場中は毎巡回引く（従来どおり）。閉場中は、手元に「この閉場の中で引いた値」が無いときだけ引く（引けの後の 1 回・閉場中の再起動・取得できなかった場合）。
開場判定は市場監視・取引判断と同じ共有カーネルの `MarketHours` を直接引く（新しい判定を作らない。構成で足す臨時休場はリスク管理には入れない＝決定 5）。

### 決定 2: 閉場中に引いた値は、次の開場の時刻から保持期限を数える（窓の両端）

鮮度の起点 `QuoteSessionFreshness.FreshFrom` = 取得時に開場していれば取得時刻、閉場していれば `MarketHours.NextOpen`（見通せなければ取得時刻）。
読む側 `CachedCurrentPriceSource` は `いま − 起点 ≤ MaxQuoteStalenessSeconds` で読む。共有物 `QuoteCache` には取得時刻を返す `GetEntry` を足すだけで、`GetFresh` の規約（取得時刻からの経過）は変えない（報告書・判断の `LastKnownQuoteSource` が使う）。

根拠: 閉場中は価格が動かないので、閉場中に引いた値は開場の瞬間に引いた値と同じだけ新しい。
- **補充を止めるだけ（引けの端だけ）では壊れる**: 保持期限（既定 300 秒）は取得時刻から数えるため、引けの 5 分後から翌朝まで手元の値がすべて取得不可になる。閉場中も現在値を読む側（`PositionCloseService` の手仕舞いの参照価格・`ObservedDrawdownRefreshService` の実DD のサンプリング〔営業日は閉場中も巡回〕・審査の含み損益・取り込みの参照価格・空売りの文脈）が価格を失う。是正前は閉場中も凍った終値を引き続けていたため、この経路は価格を持っていた。
- **起点を常に次の開場にする（寄り付きの端だけ）のも誤り**: 場中に引いた値まで翌朝まで信じる。
- 開場後は開場から保持期限で切れ、その前に開場後の最初の巡回（既定 60 秒以内）が上書きする。巡回間隔が保持期限未満なら価格は途切れない（既定 60 < 300）。寄り付き直後に前日の終値で数える時間は是正前と同じ（最大で開場後の最初の巡回まで）。

窓の表（プローブ 6 本 × 形 5 通り）は作業仕様書。

### 決定 3: 日次要求量の見積りは米国の場中 390 分で数える

`Program.cs` の起動時の評価と自己申告の 2 か所に `QuoteRefreshService.ActiveMinutesPerDay`（`MarketSessions.RegularSessionMinutes(Market.UnitedStates)`）を渡す（市場監視と同じ）。閉場ごとの 1 回（銘柄数 × 1 回/日）は数えない。

### 決定 4: 口座選択・通貨近似のログは初回だけ Information、繰り返しは Debug。口座 ID はログで伏せる

- 口座選択: 前回 Information で出した「口座 ID・種別・取扱市場」の組と同じなら Debug、違えば（初回を含む）Information。
- 通貨近似: 同じ口座で近似を報告済みなら Debug。近似でない応答（通貨の明示・内訳での反証）を受けたら報告済みを解き、次の近似は Information。Warning にはしない（IADR-0354 の判断は不変）。初回の Information は残るので T-10-621 は成り立つ。
- 口座 ID を出すログ（接続完了・口座選択・口座の食い違いの Warning）は既存の `MaskAccountId`（末尾 2 桁以外を伏せる。検証口の出力と同じ）を使う。新しい伏せ方は作らない。
- 状態はプロセス内（`MMApiMoomooTradeClient` は singleton）。`Interlocked.Exchange` で並行の呼び出しを 1 回に寄せる（崩れても Information が 1 行増えるだけ）。選ぶ口座・採る値・照会の回数は変えない。

### 決定 5: 採らなかったもの

- **リスク管理に臨時休場の構成（`Monitor:Holidays` 相当）を足す**: `deploy/` に設定は 0 件。足さない場合のずれは「臨時休場日に開場と読んで巡回する」だけで、価格を失う向きには倒れない。
- **閉場中も間隔を延ばして引く**（例: 30 分ごと）: 閉場中は価格が動かないため、引く理由が無い（決定 2 で読む側は困らない）。
- **寄り付きの瞬間に巡回を合わせる**（タイマーを `NextOpen` へ寄せる）: 決定 2 で寄り付き直後も価格を持つため、巡回間隔のままで足りる。
- **ログのサンプリング・抑制の汎用部品**: 対象は 2 行だけで、同型の要求は他に無い（検査器・規約の追加と同じく 2 回目まで待つ）。

## 結果

- 閉場中の Finnhub `/quote` はリスク管理から銘柄ごとに 1 回/閉場（是正前は約 1,050 回/夜/銘柄）。
- 閉場中・寄り付き直後に現在値を読む側の挙動は是正前と同じ（閉場中に引いた値を読む）。
- 口座選択・通貨近似の Information は初回と変化時だけ。ログに口座 ID の全桁は出ない。
- 試験 T-10-1960〜T-10-1974。
- **［2026-10-01 追記 / #1131・#1135］独立監査（PR #1147）で生き残った変異を試験で殺した（T-10-1975〜T-10-1979）:** 開場判定を取得時刻＋1 秒で行う（T-10-1975・T-10-1976＝米国の引け・東証の前場の引けの境界）、読む側を常に米国の市場で判定する（T-10-1977）、非 USD の警告の経路で報告済みを解かない（T-10-1978）、通貨近似の鍵から口座を外す（T-10-1979）。

### 残余リスク

- 引けの直後（最初の閉場の巡回、既定 60 秒以内）に引いた値が公式の終値を反映していなければ、その値が翌朝まで残る（Finnhub の終値の確定時刻は未実測）。
- 臨時休場はリスク管理の開場判定に入らない（決定 5）。
- 日次見積りは閉場ごとの 1 回を数えない。
- 繰り返しの 2 行は Debug のため既定のログ水準では見えない（初回と変化は見える。供給が止まれば既存の Warning が出る）。
- **［2026-10-01 追記 / #1131］独立監査（PR #1147）:** 鮮度の起点に使う取得時刻は**応答の受信時刻**である（`QuoteRefreshService` が応答を待った後の時計で `QuoteCache.Set` する）。したがって 15:59:59.x ET に送った要求の応答が 16:00:00.x に届けば閉場中の値として扱われ、引けの直前の価格が次の開場＋保持期限（既定 300 秒）まで残る。
- **［2026-10-01 追記 / #1135］独立監査（PR #1147）:** OpenD の `retMsg` を含む例外・ログは本件の伏せ方（末尾 2 桁）を通しておらず、口座 ID が全桁で出得る（本件の射程外。[#1148](https://github.com/endazon/ai-stock-trading/issues/1148)）。

> **［2026-10-02 追記 / #1132］** 決定 3 の渡し方（`Program.cs` 2 か所へ `QuoteRefreshService.ActiveMinutesPerDay` を渡す）は [IADR-0477](IADR-0477_finnhub-daily-estimate-from-actual-symbols.md) が改めた。
> 見積りは補充の巡回ごとに保有建玉の実数から数え、場中の分は市場ごと（米国 390 分）に渡す。`ActiveMinutesPerDay` は撤去した。決定 3 の数え方（米国の場中 390 分・閉場ごとの 1 回は数えない）は不変。
