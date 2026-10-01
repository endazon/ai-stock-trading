---
title: Stage 0 の記録へも判断時点の前営業日までの確定足から出来高と 20 日平均比を渡し、DecisionVolume を有効にしたときに本番と入力を揃える（#1139）
type: spec
status: accepted
related_ids: [FR-02, FR-04, FR-15, UC-01, ADR-0048, ADR-0033, ADR-0036, ADR-0044, IADR-0479, IADR-0467, IADR-0451, IADR-0442, IADR-0387, IADR-0318]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0048_decision-volume-from-daily-kline-within-existing-source.md (決定 2「Stage 0 では、判断時点の前営業日までの確定足から同じ値を計算して渡す」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md (決定 2: その時点までの情報だけ)
---

# Stage 0 の記録へ出来高と 20 日平均比を渡す（#1139）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04（判断の材料）・FR-02（取引サイクル）・FR-15（Stage 0 の記録・日足の履歴源）・UC-01（手順 3）
- 計画 ADR: ADR-0048 決定 2（Stage 0 では判断時点の前営業日までの確定足から同じ値を計算して渡す。当日の足は使わない）・ADR-0033 決定 2（その時点までの情報だけ）・ADR-0036 決定 1（復元できない項目）
- 関連する実装ADR: IADR-0467（本番の出来高の経路）・IADR-0451 決定 5（Stage 0 の値動きの行）・IADR-0442（as-of の監視銘柄のデコレータ）・IADR-0387（再構成可否の申告）・IADR-0318（記録器）
- 新規 IADR: IADR-0479
- 起票: [#1139](https://github.com/endazon/ai-stock-trading/issues/1139)（監査 medium）

🔴 **issue 本文は「ADR-0048 決定 1」と書くが、Stage 0 の文言は決定 2 にある**（隣接クローンの `origin/main` で原文を確認。決定 1 は「既存源の内側」）。規則 10 により他人の引用を検証せず転記しない。本件の引用は決定 2 とする。

## 問題

`Stage0DecisionRecorder.RecordOneAsync` は `TradeDecisionPromptBuilder.Build(... intraday: input.Intraday)` で `volume:` を渡さない。
`DecisionVolume:Enabled=true` にすると、本番の判断は「出来高: 前営業日（…）の確定値 … / 20 日平均比 …」の行を持ち、Stage 0 は常に従来の
「出来高: 未提供」の行（`VolumeNotProvidedLine`）で記録する。**指紋（`InputFingerprint`）と戦略 ID が、本番と同じ入力の判断を表さなくなる**（記録器のコメントが ADR-0011 の前提とする「検証したものと本番で走るものの一致」）。

## 設計（IADR-0479 に記録）

1. **as-of の日足の供給は既存の口 `IDailyBarsProvider` に足す**（`GetConfirmedBarsAsOfAsync(symbol, market, tradingDay)`）。本番の口と同じ実装
   （`CachedDailyBarsProvider`）が、同じ取得元（`IDailyBarsSource`）・同じ期間の求め方（前営業日の 45 暦日前〜取引日の前日）・同じ確定足の切り方
   （取引日以降を捨てる・重複は先の 1 本・昇順）で返す。期間と切り方は 2 つのメソッドが共有する 1 つの静的関数に寄せる（重複実装しない）。
   - as-of の取得は**キャッシュしない・本番のキャッシュを読まない／書かない**（取引日の鍵が違う。本番のキャッシュを過去日で汚さない）。
   - 有効／無効の切り替えは本番と同じ `IsEnabled`（`DecisionVolume:Enabled` と `OrderExecution:BaseUrl` の 1 か所の選択）を使う。**2 つ目の設定を作らない。**
2. **値の計算は本番と同じ純関数 `DailyVolumeContext.From`**。表示も同じ `TradeDecisionPromptBuilder.VolumeLine`（`Build(volume:)` 経由）。
3. **供給はデコレータ `DailyVolumeAsOfDecisionInputProvider`**（`WatchlistAsOfDecisionInputProvider` と同じ形）。内側が入力を返し、
   `IsEnabled` かつ入力に出来高がまだ無いときだけ引き、`AsOfDecisionInput.WithVolume` で載せる。
   - 無効 → 引かない・`Volume` は null・プロンプトは develop と一字一句同じ。
   - 取得できない（null）・例外 → `DailyVolumeContext.Unavailable`（本番の `GetDailyBarsSafeAsync` と同じ「未提供」の別の文）。キャンセルは伝える。
   - 足りない（20 本未満）→ 出来高は出し比だけ「不明」（純関数の規則のまま）。日本株 → 要求せず「未提供」（本番と同じ）。
4. **`AsOfDecisionInput` に `volume` を持たせ、型の側で as-of を守る**: `volume.PreviousDay >= AsOf` は例外（前日終値の `previousClose` と同じ規律）。
   `WithWatchlist` は出来高を保ち、`WithVolume` は監視銘柄とその理由を保つ（デコレータの順序に依らない）。
5. **再構成可否の申告（as-of 入力 4 種）に出来高の種別は足さない**。ADR-0048 決定 2 は「前日までの値は復元できるため、ADR-0036 決定 1 の
   『復元できない項目』には当たらない」と定める。取得できない日は本番と同じく「未提供」と書いて続ける（本番の判断も同じ入力で動く）。
6. 組み立て: `Program.cs` の as-of 供給を `Watchlist(Volume(No…))` にし、出来高のデコレータへ本番と同じ singleton の `IDailyBarsProvider` を渡す。

採らなかった案: (a) as-of 用に別の口・別の設定（2 か所目の切り替えができ、本番と Stage 0 が食い違い得る）／(b) as-of でも本番のキャッシュを使う（鍵が
取引日で、過去日を書くと本番の今日の取得を奪う・逆も）／(c) 期間全体を 1 回で取って日ごとに切る（前復権の基準が揃い取得回数も減るが、送り手の上限
400 日と期間の長さの扱いが増える。Stage 0 は既定で無効で、記録は銘柄 × 平日ごとに 1 回で足りる）／(d) 取れない日を (b)〜(e) と並ぶ種別で
「再構成不可」と申告する（ADR-0048 決定 2 と食い違う）。

## 母集合（規則 9。origin/develop e3928438）

`git grep -n "PromptBuilder.Build(\|PromptBuilder.BuildScreening(\|PriceContextLines(" -- 'backend/Services/*.cs' ':!*/Tests/*'`、
`git grep -n "DailyVolumeContext.From\|GetConfirmedBarsAsync\|new AsOfDecisionInput(\|WithWatchlist(\|IAsOfDecisionInputProvider>" -- 'backend/*.cs' ':!*/Tests/*'`、
`git grep -n "出来高" -- docs backend .ai-context/adr` のうち Stage 0・as-of に触れる行。

### 出来高を渡す／計算する経路と Stage 0 の入力の組み立て

| # | 箇所 | 中身 | 扱い |
| --- | --- | --- | --- |
| 1 | `TradeDecisionAppService.cs:422-429` | 本番: `IsEnabled` なら `DailyVolumeContext.From(GetDailyBarsSafeAsync)` → `Build(volume:)` | 変えない（基準） |
| 2 | `TradeDecisionAppService.cs:445-450` | 本番の一次 `BuildScreening(volume:)` | 変えない（Stage 0 は一次を記録しない） |
| 3 | `TradeDecisionAppService.cs:878-889` | 本番の fail-safe（例外→null、キャンセルは伝える） | 変えない。Stage 0 のデコレータで同じ扱いを書く |
| 4 | `TradeDecisionPromptBuilder.cs:249/278/291/397/424/457/473` | `volume` → `PriceContextLines` → `VolumeLine` | 変えない（共有） |
| 5 | `DailyVolumeContext.cs:22` | 純関数 `From` | 変えない（共有） |
| 6 | `IDailyBarsProvider.cs:22` | 本番の口 | **as-of のメソッドを足す** |
| 7 | `CachedDailyBarsProvider.cs:46` | 本番の取得・期間・切り方 | **期間と切り方を共有関数へ出し、as-of のメソッドを足す** |
| 8 | `NoOpDailyBarsProvider.cs:12` | 無効の口 | as-of も null（要求しない） |
| 9 | `Stage0DecisionRecorder.cs:256` | Stage 0 の `Build(...)`（`volume:` なし） | **`volume: input.Volume` を足す** |
| 10 | `AsOfDecisionInput.cs`（ctor・`WithWatchlist`） | as-of 入力の型 | **`volume` を足し、as-of の例外と `WithVolume` を足す** |
| 11 | `WatchlistAsOfDecisionInputProvider.cs:31` | `WithWatchlist` で組み直す | 変えない（`WithWatchlist` 側で出来高を保つ） |
| 12 | `Program.cs:500-501` | as-of 供給の組み立て | **出来高のデコレータを挟む** |
| 13 | `Program.cs:293-313` | 本番の口の選択 | 変えない（同じ singleton を渡す） |
| 14 | `Tests/.../DailyVolumeInPromptTests.cs:172` | 口の試験用の偽物 | 新メソッドを実装（null） |

ReportService の `PolicyRevisionPromptBuilder.Build` は方針の改訂の材料の一覧で、判断のプロンプトではない（対象外）。

### 誤りになる記述（規則 9・10）

| # | 箇所 | 記述 | 扱い |
| --- | --- | --- | --- |
| A | `IADR-0467` 残余リスク（187 行） | 「Stage 0 の記録は出来高を『未提供』のまま…残件」 | 凍結。日付つき追記で解消を記録 |
| B | `IADR-0451` 結果（152 行） | 「Stage 0 の記録の出来高は『未提供』のまま（残件）」 | 凍結。日付つき追記 |
| C | 索引 `README.md` の IADR-0467・IADR-0451 の行 | 「Stage 0 は未提供のまま」 | 日付つき追記を行末に足す（行を書き換えない） |
| D | `docs/tests/FR-10_risk-controls-tests.md:4394` | 「Stage 0 の記録は出来高を『未提供』のまま記録する。」 | 生きた文書。直す（本件の節を参照する形へ） |
| E | `.ai-context/specs/20260930_1118_daily-volume-from-kline.md:45,194` | 範囲外・残余 | point-in-time の記録。**据え置く** |
| F | `docs/operations/kline-quota-probe-runbook.md`「判断への出来高の有効化」 | Stage 0 に触れていない（issue は「前提条件に本件の完了を加える」） | 本件で解消するので、前提条件ではなく**有効化した後の挙動**へ Stage 0 も同じ値で記録する旨と取得の回数を書く |
| G | 自分の新しい記述: IADR-0467 決定 3「1 取引日の取得は（監視銘柄の数）＋（撃ち直し）回」 | Stage 0 の記録を走らせるとその分の取得が加わる | IADR-0479 と runbook に書く（IADR-0467 は追記で参照） |
| H | `AsOfDecisionInput.cs` の `previousClose` の説明・`Stage0DecisionRecorder.cs:254` のコメント | 出来高に触れない（誤りではない） | コメントに出来高の 1 行を足す |

## 窓の表（規則 11）

窓 = 「日足が確定する時刻（その日の引け）」と「判断がその足を使う時刻（判断時点 AsOf の場中）」の間。
後の端 = 判断時点（AsOf 以降の足を使わない）、前の端 = 判断時点の直前の確定足（前営業日の足があること）。

- **P1（増える側・先読み）**: 応答に AsOf 当日（境界ちょうど）と AsOf より後の足が混ざる（過去日を今取ると当日も確定済みで返る）。混ざれば当日の全体の出来高を判断時点で知っていることになる。
- **P2（減る側・痩せ）**: P1 と同じ応答で、前営業日までの確定足から値を出せるのに「未提供」に倒す（未来の足を最後の足と読み、前営業日と食い違うとする）。
- **P3（古い足を前日と書く）**: 最後の確定足が前営業日より古い（停止・休場の扱い・欠け）。
- **P4（境界の 1 つ手前）**: 前営業日（AsOf の前営業日）の足を落とす（`< 前営業日` の書き違い・要求の終わりを前営業日の前日にする）。

| 形 | P1 | P2 | P3 | P4 |
| --- | --- | --- | --- | --- |
| 後の端だけ（AsOf 以降の足を捨てるが、最後の足と前営業日を突き合わせない） | ○ | ○ | ✕（古い足を前日と書く） | ○ |
| 前の端だけ（最後の足が前営業日かだけ見て、AsOf 以降を捨てない） | ○（未来が最後に来て「未提供」になり、漏れはしない） | ✕（値が出せる日が「未提供」） | ○ | ○ |
| 両端を `<=` で切る（境界ちょうどを残す） | ✕（AsOf の足が最後に来る→前営業日でないので「未提供」、ただし突き合わせが無ければ漏れる） | ✕ | ○ | ○ |
| **両端（採用）**: 要求は前営業日の 45 暦日前〜AsOf の前日、応答は AsOf より前だけを残し、最後の足が前営業日と一致するときだけ値を出す。型の側でも `PreviousDay >= AsOf` を例外にする | ○ | ○ | ○（「未提供」） | ○ |

採用の形は本番の `CachedDailyBarsProvider` の両端（取引日でのキャッシュ区切り＋当日以降を捨てる）と同じ関数で切る（Stage 0 はキャッシュしないので前の端はキャッシュではなく前営業日との突き合わせ）。

試験: P1 = T-10-2032・T-10-2033、P2 = T-10-2032、P3 = T-10-2035、P4 = T-10-2032（要求の範囲と前営業日の値）。

## 実装

- `IDailyBarsProvider.GetConfirmedBarsAsOfAsync`・`CachedDailyBarsProvider`（共有関数 `RequestWindow` / `Confirm`）・`NoOpDailyBarsProvider`。
- `AsOfDecisionInput`（`volume` 引数・`Volume`・`WithVolume`・as-of の例外・`WithWatchlist` が出来高を保つ）。
- `DailyVolumeAsOfDecisionInputProvider`（新規）。
- `Stage0DecisionRecorder`（`volume: input.Volume`）。
- `Program.cs`（`Watchlist(Volume(No…))`）。

## 試験（T-10-2030〜T-10-2039）

| ID | 内容 |
| --- | --- |
| T-10-2030 | 有効: Stage 0 のプロンプトに本番と同じ出来高の行（確定値・20 日平均比）が載り、指紋は同じ入力の本番の組み立てと一致する |
| T-10-2031 | 本番の取得（取引日 D の場中）と as-of の取得（AsOf=D）が同じ取得元から同じ値（`DailyVolumeContext`）を作る |
| T-10-2032 | 先読みなし: 応答に AsOf 当日（境界ちょうど）と後の足が混ざっても捨て、前営業日の値を出す。要求は前営業日の 45 暦日前〜AsOf の前日 |
| T-10-2033 | 型: `volume.PreviousDay` が AsOf ちょうど・後なら例外、前なら受ける |
| T-10-2034 | 無効: 要求 0 回・`Volume` null・プロンプトと指紋はデコレータの無い組み立て（develop）と一字一句同じ |
| T-10-2035 | 取れない（null）・例外・最後の足が古い → 「未提供」の別の文で記録を続ける／19 本 → 比だけ不明／日本株 → 要求せず未提供／キャンセルは伝える |
| T-10-2036 | 組み直し: `WithWatchlist` が出来高を保ち、`WithVolume` が監視銘柄と理由を保つ。既に出来高があれば引かない |
| T-10-2037 | 組み立て: as-of 供給は `Watchlist(Volume(No…))` で、出来高のデコレータには判断サービスと同じ singleton の口が渡る（既定 NoOp・有効なら Cached） |
| T-10-2038 | as-of の取得は本番のキャッシュを読まない・書かない |

T-10-2039 は使っていない（欠番。割り当ての範囲の末尾）。

## 自己変異（6 個以上）

変異を 1 つずつ入れ、`dotnet test --filter "FullyQualifiedName~Stage0DecisionVolume|FullyQualifiedName~CachedDailyBars"` を走らせ、元へ戻した
（退避は作業ツリーの外へ `cp`。git の操作で戻していない）。**12 件すべて赤。**

| 変異 | 結果 | 落ちた試験 |
| --- | --- | --- |
| M1 記録器が `volume: input.Volume` を渡さない（是正前） | 赤 | T-10-2030・T-10-2035（取れない 4 通り・口の例外・19 本） |
| M2 確定足の切り方を `<=`（AsOf ちょうどを残す） | 赤 | T-10-2030・T-10-2031・T-10-2032・T-10-2036・T-10-1830（本番の当日足） |
| M3 無効でも引く（`IsEnabled` を見ない） | 赤 | T-10-2034 |
| M4 口の例外を握らない（キャンセルだけ握る形へ反転） | 赤 | T-10-2035（口の例外・キャンセル） |
| M5 as-of で本番の今日の口（`GetConfirmedBarsAsync`）を呼ぶ | 赤 | T-10-2030・T-10-2035・T-10-2036 |
| M6 `WithWatchlist` が出来高を落とす | 赤 | T-10-2036（2 件） |
| M7 型の検査を `>`（AsOf ちょうどを通す） | 赤 | T-10-2033（AsOf ちょうど・差し替え） |
| M8 `Program.cs` で出来高のデコレータを挟まない | 赤 | T-10-2037（4 通り） |
| M9 取得できない（null）なら入力を変えない（「無効」と同じ行） | 赤 | T-10-2035（null・例外・口の例外・日本株） |
| M10 要求の期間の終わりを AsOf にする | 赤 | T-10-2032・T-10-2038・T-10-1830（本番の要求の範囲） |
| M11 `WithVolume` が監視銘柄の理由を落とす | 赤 | T-10-2036 |
| M12 as-of の期間を今の取引日から求める | 赤 | T-10-2030・T-10-2031・T-10-2032・T-10-2035・T-10-2036・T-10-2038 |

注: M10 の 1 回目は置換の文字列が式の途中で切れてコンパイルエラーになった（赤ではなく不成立）。文字列を `LookbackCalendarDays), tradingDay.AddDays(-1));` に
広げて入れ直し、赤を確かめた。各変異の後に退避した 5 ファイルと `cmp` で一致を確かめた。

## 残余リスク

- 本番の as-of 入力の実供給（方針・価格・参考情報）は依然として無い（`NoAsOfDecisionInputProvider`）。本件で Stage 0 の出来高の経路は揃うが、記録が作られるのは実供給が入ってから。
- Stage 0 の記録は銘柄 × 平日ごとに日足を 1 回取る（キャッシュしない）。取得枠は銘柄単位で増えないが、発注執行の自制（60 秒に 25 回）に掛かり記録が遅くなる。
- 過去日の足は「今の時点の」前復権で取る。比は分割の基準に依らない（同じ取得の中で揃う）が、出来高の絶対値は当時の表示と違い得る（本番も同じ前復権の値）。
