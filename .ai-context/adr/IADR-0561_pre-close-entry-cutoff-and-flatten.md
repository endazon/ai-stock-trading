---
title: IADR-0561 閉場前の新規建ての締め切り（閉場の 60 分前以降の新規建てを審査で PreCloseEntryCutoff として拒否）と閉場前の手仕舞い（既定は無効。閉場の 15 分前以降に保有を成行で全量決済する判断を常駐が発行し、方針の「持ち越し可:」行の銘柄だけを残す）。閉場は共有カーネルの暦で求め、判断のプロンプトへ市場の現地時刻と閉場までの残り時間を渡す
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-10, FR-19, UC-01, UC-06, ADR-0003, ADR-0009, ADR-0040, IADR-0163, IADR-0357, IADR-0380, IADR-0470, IADR-0515, IADR-0529, IADR-0535, IADR-0558]
author: claude (Claude Code)
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md（FR-10・FR-19・FR-04）
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md（§5。本件の 2 行は反映待ち）
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md
---

# IADR-0561: 閉場前の新規建ての締め切りと閉場前の手仕舞い（#1396）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-20
- 決定者: claude が起案（起点 [#1396](https://github.com/endazon/ai-stock-trading/issues/1396)・裁定 planning#808。レビューは PR で受ける）

## 起点・関連

- 計画: FR-10（リスク統制）、FR-19（取引ガード）、FR-04（方針の範囲で判断）。ADR-0009（手仕舞いは統制で止めない）。
- 裁定: planning#808（2026-10-20、利用者）。締め切り N = 60 分・手仕舞い M = 15 分（JP・US とも。市場ごとに設定可）・手仕舞いの既定は無効・
  持ち越しは方針で銘柄を指定・プロンプトに時刻を渡す。
- 前提として覆さない IADR: [IADR-0163](IADR-0163_allow-list-and-required-dependency-scope.md) 決定2（不在が統制の無効を意味する依存は必須）、
  [IADR-0380](IADR-0380_market-session-schedule-and-closed-protection-gap.md)（市場の暦の単一情報源・夜間は S1 が保護しない）、
  [IADR-0357](IADR-0357_owner-close-market-order-cancel-path-and-expiry-notice.md)（成行の手仕舞いの口）、
  [IADR-0515](IADR-0515_order-approval-origin-s1-nets-owner-closes.md)（S1 は出どころ TradeDecision の決済を損切りの前に取り消す）、
  [IADR-0529](IADR-0529_serialize-entry-screening-with-reservations.md)（新規建ての審査の直列化と予約）、
  [IADR-0535](IADR-0535_skip-llm-while-close-in-flight-covers-position.md)（処理中の決済のゲート）、
  [IADR-0470](IADR-0470_policy-numeric-take-profit-warning-and-reached-hint.md)・[IADR-0558](IADR-0558_entry-basis-per-lot-recorded-and-handed-to-held-decisions.md)（方針は決まった書式の行だけを読む）。
- 仕様書: `.ai-context/specs/20261020_1396_pre-close-entry-cutoff-and-flatten.md`

## コンテキスト

方針（daily-2026-10-17 版 5）の「保有は原則日中のみとし、取引終盤は新規建てを控えて手仕舞いを優先する」が効かなかった（PoC 2026-10-19:
MARA を閉場の約 1 時間前に新規建て、NVDA・MARA を夜間に持ち越し）。判断のプロンプトには時刻が無い。夜間は S1 が保護せず、持ち越しは寄り付きの
窓開けの損失に直結する（10-13 の TSLA）。

## 決定

1. **閉場は共有カーネルで求める**: `MarketHours.SessionCloseOn(market, instant, extraHolidays, extraHalfDays)` を足す（市場ローカルの日付の最後の閉場。
   半日取引日は 13:00 ET へ前倒し・東証は 15:30 JST で昼休みは閉場ではない・週末と休場日は null）。時刻の定数は `MarketSessions.FinalClose`（場中の判定と同じ定数）。
   「N 分前以降か」は `PreCloseWindow.IsAtOrAfter`（区間は **[閉場 − N, その市場ローカルの日の終わり)**。閉場の後も同じ日のうちは入る。翌取引日の寄り付き前は入らない）。
   取引判断は `IMarketCalendar.SessionClose`（本番の `MarketCalendar` は構成の臨時休場・臨時の半日を足す）を引く。リスク管理は規則計算だけを使う（下の残余）。
2. **締め切り（リスク管理の審査）**: 拒否理由 `PreCloseEntryCutoff`（序数 33・クラス A）。`RiskEvaluator` が新規建て（`PositionEffect.Open`。買い増し・売り増し・空売りを含む）
   で判定し、**手仕舞いは止めない**（ADR-0009）。供給は `PreCloseEntryCutoffSupply(Now, SessionClose, Minutes)`（判定コアは時刻を読まない）。
   `OrderScreeningService` は構成 `PreCloseEntryCutoffSettings` を**必須の引数**で受ける（IADR-0163 決定2）。予約からの組み直し（IADR-0529 決定6）でも判定し、
   締め切りの前に承認した予約を締め切りの後に発行しない（予約を無効にして拒否する）。
   構成 `PreCloseEntryCutoff:Minutes:UnitedStates` / `:Japan`（既定 60。範囲 1〜場中の長さ〔390 / 330〕）。**統制を外す値（0）は持たない**。範囲外・読めない値・未知の市場は起動を止める。
3. **手仕舞い（取引判断の常駐）**: `PreCloseFlattenService`（PeriodicTimer、既定 60 秒）が巡回ごとに `PreCloseFlattener` を呼び、閉場の M 分前以降の**場中**に、
   保有の全量を成行で決済する `TradeDecisionMade`（`PositionEffect.Close`・`MarketOrder: true`・`ProductType.Cash`・根拠文の頭「閉場前の手仕舞い（統制）」）を Wolverine で発行する。
   - **決済の経路は既存のもの**: 審査（決済は統制で止めない）→ `OrderApproved`（出どころ TradeDecision）→ 発注執行の成行の口（IADR-0357）。新しい発注の経路を作らない。
   - **重複**: 処理中の決済が建玉を覆うと照会で分かれば出さない（IADR-0535 の口）。不明なら出す（発注執行のゲートが見送る）。DecisionId は巡回ごとに新しい
     （最初の決済が見送られても次の巡回でやり直せる）。
   - 出さない: 保有の照会が不明・全注文が審査で拒否される銘柄（IADR-0521 決定4 の口）・換算レートが無い（#506）・参照価格（現在値 → 平均取得単価）が無い。
   - 構成 `PreCloseFlatten:Enabled`（既定 false）・`PreCloseFlatten:Minutes:<Market>`（既定 15・範囲 1〜60）・`PreCloseFlatten:IntervalSeconds`（既定 60・範囲 10〜300）。
   - 定時サイクル（LLM の判断）に載せない理由: 起点は情報収集に従属し、LLM の所要で遅れ、古い起点は捨てられる（IADR-0490）。時刻に効く統制を判断の巡回に従属させない。
4. **方針の書式の行（共有カーネル `PolicyPreCloseLines`）**: 「閉場前の手仕舞い: 有効」の行で有効化（構成と OR。方針で無効にはできない。「有効」以外の値は読まない）。
   「持ち越し可: NVDA, AAPL」の行の銘柄を残す（区切り「,」「、」「・」「/」・空白。大文字小文字は区別しない。英数字と「.」「-」の語だけ）。自由文は読まない。
   方針が読めない（未確定・照会の失敗）ときは、構成で有効なら全建玉を手仕舞う（持ち越しの指定を確かめられない＝持ち越さない側）。
   方針の改訂の AI（日報）へ、締め切りはコードが強制することと 2 つの書式の行を案内する（`PolicyRevisionPromptBuilder.PreCloseRule`）。
5. **判断のプロンプト**: 本判断・一次の両方に「- 市場の現地時刻: yyyy-MM-dd HH:mm（米国東部時間｜日本時間） / 本日の閉場: HH:mm（残り N 分）｜HH:mm（閉場後）｜なし（休場日）」を載せる。
   一次の縮退の保護分 `ScreeningContextAssembler.MarketClockReserveChars`（100）。Stage 0 の記録器は as-of が日付（`DateOnly`）だけで時刻が無いため渡さない（行は出ない）。
6. 既定値は `TradingDefaults.PreCloseEntryCutoffMinutes`（60）・`PreCloseFlattenMinutes`（15）・`PreCloseFlattenEnabled`（false）。計画 §5 は反映待ちで、値は裁定の値である。

## 検討した選択肢

| 選択肢 | 採否 | 理由 |
| --- | --- | --- |
| 締め切りを審査で強制し、手仕舞いを常駐が既存の決済の経路で出す（本決定） | 採用 | 審査は新規建ての唯一の関門。手仕舞いは判断の決済と同じ経路で、統制の判定・S1 の優先・処理中の決済のゲートをそのまま使える |
| 手仕舞いをリスク管理の常駐から `OrderApproved` で直接出す（維持率の自動縮小と同じ形） | 不採用 | 方針（持ち越し可・有効化の行）はリスク管理に無い。方針の照会をリスク管理へ足すと依存が増える |
| 手仕舞いを定時サイクルの中（LLM の前）に入れる | 不採用 | 起点が収集に従属し、遅れ・捨てられる（決定 3） |
| 締め切りの区間を [閉場 − N, 閉場) に限る | 不採用 | 閉場の後の新規建て（遅れて届いた判断・再配送）を通す。裁定の「以降」を素直に読む |
| 0 分で締め切りを外せるようにする | 不採用 | 統制を外す値を持たない（裁定が外すことを定めていない） |
| LLM の前の口（`EntryStateBlockers`）に締め切りを足す | 今回は不採用 | 口の値域と gRPC の `EntryBlocker` を変える。プロンプトの時刻と審査で強制は足りる。LLM の費用は残余 |

## 結果

- 良い影響: 閉場の 60 分前以降の新規建てが方針の文言に依らず止まる。手仕舞いを有効にすれば、持ち越しは方針で明記した銘柄だけになる。判断は時刻の条件を読める。
- 悪い影響 / トレードオフ: 締め切りの間も判断の LLM は呼ばれる（新規建ての結論は審査で拒否される。費用の残余）。プロンプトが 1 行長くなる。
- 残余リスク:
  - **リスク管理の暦は規則計算だけ**（構成の臨時休場・臨時の半日を足さない）。臨時の半日取引日は取引判断側の手仕舞い・プロンプトは前倒しされるが、審査の締め切りは通常の閉場で判定する（遅い側）。
  - 判断の決済の指値が板に残って建玉を覆っていると、閉場前の成行は処理中の決済のゲートで見送られる（指値は DAY で閉場に失効し、建玉は持ち越され得る）。
  - 常駐の巡回の間隔（60 秒）だけ手仕舞いの開始は遅れ得る。閉場までに約定しない成行（板が薄い）は持ち越しになる。
  - S1 が同じ建玉を損切りすると、S1 が判断の決済（出どころ TradeDecision）を取り消して自分の決済を出す（IADR-0515。損切りが優先し二重決済しない）。
- 計画との差異: なし（裁定どおり）。**計画 §5 へ 2 行（締め切り・手仕舞い）の反映が要る**（未反映。PR 本文に「計画 §5 の反映待ち」と書く）。
