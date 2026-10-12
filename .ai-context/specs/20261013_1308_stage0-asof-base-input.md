---
title: Stage 0 の記録器の as-of 入力の基底の供給（価格・確定日報の方針・ニュース開示・為替・サイジング）を実装し、backtest の日足を発注執行の日足の照会から取る（#1308・blocked-tasks A-3）
type: spec
status: accepted
related_ids: [FR-15, FR-04, FR-07, FR-08, FR-01, ADR-0033, ADR-0036, ADR-0044, ADR-0046, ADR-0023, ADR-0048, ADR-0054, ADR-0064, ADR-0016, ADR-0011, IADR-0528, IADR-0318, IADR-0387, IADR-0442, IADR-0451, IADR-0467, IADR-0479, IADR-0486, IADR-0498, IADR-0157, IADR-0105, IADR-0454, IADR-0474]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md (決定 2・§残るもの・フォローアップ 4)
  - planning:projects/ai-stock-trading/07_adr/ADR-0036_stage0-input-completeness-and-split-fixation.md (決定 1)
  - planning:projects/ai-stock-trading/07_adr/ADR-0044_watchlist-in-decision-prompt-and-stage0-asof.md (決定 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0046_watchlist-reconstruction-lower-bound-seeded-at.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md (決定 5 の確認 2 点)
  - planning:projects/ai-stock-trading/07_adr/ADR-0064_llm-assignment-to-5-5-family-and-stage0-rerun.md (決定 3)
---

# Stage 0 の as-of 入力の基底の供給と backtest の日足（#1308・A-3）

## 背景

- 記録器（IADR-0318）の as-of 入力の連鎖の最も内側が `NoAsOfDecisionInputProvider`（常に null）であり、
  記録を有効にして承認しても**記録 0 件・LLM 0 回で `Completed`** になる（#1308。`Program.cs:595-603`）。
- backtest の日足の源は空（`Backtest__BarData__Provider=""`・blocked-tasks A-3）で、記録があっても `NoHistoricalBars` で不合格になる。
- 再生は**数量を持つ判断を 1 件でも合否から外したら判定を組まない**（`Stage0ReplayEvaluation.cs:126-136`・`ExcludedDecisionAltersReplayPath`）。
  したがって (b)(c)(d)(e) のどれかが再構成できない日に Buy/Sell が出れば、その記録集合は評価できない。
- フェーズ 1 の調査（2026-10-13・本 issue の作業セッション）で、承認済みの窓（2026-07-01〜10-09）の前半は
  (c) 確定日報と (e) 監視銘柄が**構造的に存在しない**（システム稼働前。PoC の `SeededAt`＝2026-09-15 16:30Z。IADR-0442:112）と判明し、オーナーの裁定を仰いだ。

## 裁定（利用者裁定 2026-10-13。記録: [#1219 のコメント](https://github.com/endazon/ai-stock-trading/issues/1219#issuecomment-6134235872)）

| # | 裁定 | 本作業での扱い |
| --- | --- | --- |
| 1 | **窓は 2026-09-15 以降に縮める（〜2026-10-09）。監視銘柄と確定日報が無い期間は対象外にする。代替入力の計画 ADR は起こさない。** 承認額 1,700 円は上限として据え置き、新しい窓の見積りは値の PR（#1320）で計算し直す。値の PR は #1308 の後にマージし、PoC は両方がそろうまで記録を回さない | 値（`Stage0Recording__From`）は #1320 の担当であり本作業では触らない。供給側は「判断時点に確定日報が無い日」を **null（判断しない）** にする（本番も確定日報が無ければ取引しない。FR-07） |
| 2 | **(b) は Finnhub の company-news・SEC EDGAR・FRED（ALFRED）の過去分を、本番と同じ規則（新しい順・168h・上位 K 件）で組めば「再構成できた」とみなしてよい。欠けた源（google-news・確定報告書の RAG）は、記録に「欠けた源」として残す。** | 3 源がすべて取れた日だけ (b) を再構成済み（または当時無し）と申告し、欠けた源を申告の理由欄に残す。3 源のどれかが取れない・構成されていない日は (b) を再構成不可と申告する |
| 3 | **A-3 は、Stage 0 では前復権と費用モデルの不整合を受け入れる。借株料が抜ける点は残余リスクとして記録する。backtest の足は発注執行の `/daily-bars` 経由で取る（backtest の Pod に OpenD は配線しない）。** | backtest に過去データ源 `order-execution` を足す。既存の `moomoo` 直結は残す（選ばない） |

## 実測（origin/develop `bd59e52a`。2026-10-13）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 確定済みの報告書は**不変**である（`UpsertDraft` は確定済みに例外。改訂・作り直しは別の版のドラフトを作るだけ）。`ConfirmedAt` を持つ | `EfReportStore.cs:80-81`・`:136-139` |
| 2 | 本番の方針は「確定済みの日報のうち `PeriodStart` 最大」（`GetLatestConfirmed`） | `ReportAppService.cs:78-84`・`EfReportStore.cs:175-182` |
| 3 | 本番の参考情報は KB 検索 2〜3 本（銘柄＝`symbol` 属性・市場＝`coverage=market`・補充）。いずれも**新しい順**（`SortBy=updated`）・上位 `Retrieval:TopK`（既定 5）・**168 時間の足切り**（`PublishedAt` を持つ文書だけ）。出所タグで出典を限定（`RetrievalSourcePolicy`） | `KnowledgeBaseRetrievalContextProvider.cs`・`Program.cs:371-379` |
| 4 | KB は日付範囲で引けない（属性は単値一致・並びは新しい順）。今日引くと未来の文書が上位を埋め、未来を落とすと 0 件＝「当時無かった」と誤申告する | 同上 |
| 5 | 収集の文書は `Title`=見出し・本文=要約／書式化した値・タグ=`[種別, 源]`・`publishedAt` 属性。finnhub-news は `/company-news`（`datetime`）、sec-edgar は submissions（`acceptanceDateTime`。構成した CIK だけ）、fred は観測（観測日を発行時刻に） | `KnowledgeBaseWriterSink.cs:65-92`・`FinnhubCompanyNewsSource.cs`・`SecEdgarInformationSource.cs`・`FredInformationSource.cs` |
| 6 | values-local の収集源は `finnhub,finnhub-news,google-news,sec-edgar,fred`。finnhub は現在値の文書（銘柄つき）を 5 分ごとに書く | `values-local.yaml:175`・`FinnhubInformationSource.cs:32-38` |
| 7 | 日足は発注執行 `GET /order-execution/daily-bars`（前復権・1 回 400 暦日まで・60 秒 25 回の自制・moomoo 構成でだけ取れる。paper は `broker-not-supported`） | `QueryDailyBars/Endpoint.cs`・`DailyBarsQueryService.cs`・`OrderExecutionService/Program.cs:291-304` |
| 8 | 再生は判断日の**翌営業日の始値**で約定し、終値で評価する。初期資金は 1,000,000 | `BacktestSimulator.cs:94-131`・`Stage0EvaluationService.cs:40` |
| 9 | バックテストの費用モデルは配当・借株料の項を持たない（`BacktestService` に該当する処理 0 件） | `grep -rn "Dividend\|BorrowFee" backend/Services/BacktestService` |
| 10 | 米国株は基準通貨（USD）の市場であり、レートは定義から 1 | `IFxRateSource.cs:16-17` |
| 11 | trade-decision の values-local に `OrderExecution__BaseUrl` は無い（出来高・ATR は無効のまま） | `values-local.yaml` trade-decision 節 |

## 分割の確認（窓の中の分割は先に確かめる）

- 対象: 12 銘柄（TSLA・META・GOOGL・AMZN・AAPL・SMCI・PLTR・NVDA・COIN・MSTR・MARA・MSFT）。期間: 判断の窓（2026-09-15〜10-09）に出来高・ATR の遡り（45 暦日）を足した 2026-08-01 以降〜本日。
- **実 OpenD の `split-compare`（検証口）は本セッションから撃てない**（クラスタへの経路が無い）。代わりに公開の分割カレンダー
  （stocktitan.net の 2026-08・2026-09・2026-10）を確かめ、**12 銘柄はいずれも載っていない**ことを確かめた（2026-10-13）。
- 🔴 **二次情報であり、実機の確認に代えない。** 記録を走らせる前に PoC が `split-compare` を 12 銘柄で撃ち、前復権と無復権の比が配当相当（1% 未満）に収まることを確かめる手順を、最後の PR の本文と下の「通し試験」に置く。分割があれば記録を止めて報告する。

## 母集合（規則 9・10・11）

- 「`NoAsOfDecisionInputProvider`」を全文書で走査: `Program.cs`（置換）・`AsOfDecisionInput.cs`（型の定義と注記。既定のまま残す）・試験 `Stage0RecordingLocalProfileTests`（#1320 が持つ「基底が無ければ 0 件」の試験。**本作業の配線が入ると前提が変わる**ため、配線の PR で追随の要否を見る）・`docs/blocked-tasks.md` B-7・`values-local.yaml` の注記（#1308 の行）。
- 「`Backtest__BarData__Provider`」「`BarData:Provider`」を走査: `BarDataOptions.cs`・`HistoricalBarSourceFactory.cs`・`values.yaml`・`values-local.yaml`・`docs/blocked-tasks.md`・`docs/functional/FR-15_backtest.md`・`docs/operations/*`。
- 「A-3」「①②③④」の記述: blocked-tasks の A-3 本体・A-2 項目 7・優先順位表 7・「実装済みだが発動しない」の Stage 0 行（本 PR で是正）。
- 新たに誤りになる自分の記述: `AsOfDecisionInput.cs` の「既定実装は常に null…実供給は残件である」（配線の PR で直す）、`Program.cs:585-587` の「as-of の他の入力の実供給は無いまま」、`HistoricalBarSourceFactory.cs` の「4 点」の注記（backtest の PR で直す）。
- **窓（規則 11）**: 参考情報の窓 `[t − 168h, t]`（t＝判断時点）を「後の端だけ」「前の端だけ」「両端」の 3 形で、増える側（未来の記事）と減る側（古い記事）のプローブに当てる。

  | 形 \ プローブ | t より 1 秒後の記事（未来） | t − 168h より 1 秒前の記事（古い） | t ちょうど／t − 168h ちょうど |
  | --- | --- | --- | --- |
  | 後の端だけ（`≤ t`） | 落ちる ✅ | **残る ❌**（本番は落とす） | 残る ✅ |
  | 前の端だけ（`≥ t − 168h`） | **残る ❌**（先読み） | 落ちる ✅ | 残る ✅ |
  | 両端（採用） | 落ちる ✅ | 落ちる ✅ | 残る ✅ |

  両端を突き合わせる形だけが期待どおりになる。試験は 3 列のプローブを両端の形に当てる。

## 設計（IADR-0528）

- **判断時点** t ＝ 判断日（AsOf）の**通常取引の終値の時刻**（市場の現地時刻 → UTC。米国 16:00 ET）。再生は終値で判断して翌営業日の始値で約定するので（実測 8）、参照価格を終値に置く IADR-0451 決定 5 と一致する。
- **(a) 価格**: 発注執行の日足（前復権）を `[AsOf − 10 暦日, AsOf]` で取り、**AsOf の足の終値**を参照価格、AsOf より前の最後の足の終値を前日終値にする。AsOf の足が無い（休場）なら null。照会の失敗は数回撃ち直し、それでも取れなければ例外で記録を止める（取れなかった日を「判断しない」にすると、再生の経路が黙って変わる）。
- **(c) 方針**: 報告書サービスに `GET /reports/daily-policy/as-of?at=<t>` を足す（`ConfirmedAt ≤ t` の確定日報のうち `PeriodStart` 最大）。確定済みは不変（実測 1）なので**当時の本番の答えと一致する**。無ければ null（裁定 1・FR-07）。`Date > AsOf` も null（警告）。照会の失敗は価格と同じく例外。
- **(b) 参考情報**: 銘柄の源（finnhub-news・sec-edgar）と市場の源（fred）を、それぞれ `publishedAt ∈ [t − 168h, t]`・新しい順・上位 K 件（`Retrieval:TopK` と同じ値）で組み、出所タグで限定する（本番と同じ `RetrievalSourcePolicy`）。fred は ALFRED の版（`realtime_start = realtime_end = AsOf − 1 日`）で当時公表済みの観測だけを読む。3 源のどれかが失敗・未構成なら (b) を再構成不可と申告する。欠けた源（google-news・finnhub の現在値の文書・確定報告書の RAG）は申告の理由欄に残す（裁定 2）。
- **(d) 為替**: 基準通貨の市場だけを扱う（レート 1）。他の市場は null（未対応。警告）。
- **サイジング**: 当時の残枠は再構成できない（記録は保有なしで固定・数量は再生で再計算しない）。資金 1,000,000（再生の初期資金と同じ）・段階残枠＝資金・当日残枠＝資金 × 1 日の発注上限比・連敗 0・DD 0・SIMULATE・統制値は `TradingDefaults`、損切りの実行機構は未供給（null）。
- **(e) 監視銘柄・出来高・ATR**: 既存のデコレータ（IADR-0442・0479・0486）をそのまま外側に巻く。
- **配線**: `Reports:BaseUrl` と `OrderExecution:BaseUrl` がともに絶対 URL のときだけ基底の供給を使い、他は従来の `NoAsOfDecisionInputProvider`（警告）。記録そのものは従来どおり `Stage0Recording`（無効・未承認・期間なし・出力先なしのいずれでも LLM を呼ばない）が止める。
- **backtest**: 過去データ源 `order-execution`（`Backtest:BarData:Provider=order-execution`・`Backtest:BarData:OrderExecution:BaseUrl`・サービスの資格）。1 銘柄 1 回の照会（400 暦日以内に分割）・`Status=Available` 以外は欠測。

## PR の分割（1 PR 1 論理単位・base は develop）

| PR | 内容 | 依存 |
| --- | --- | --- |
| 1 | 本仕様書・IADR-0528・blocked-tasks の A-3／B-7 の是正・FR-15 のテスト仕様書の採番（T-15-125〜T-15-131） | なし |
| 2 | 報告書サービスの as-of の確定日報の照会口 | なし（文書は PR 1） |
| 3 | 取引判断: 基底の供給（純粋な部分・(b) の組み立て規則・サイジング・判断時点） | なし |
| 4 | 取引判断: 外部の口（日足・方針・Finnhub・EDGAR・FRED）と配線・values-local の配線の欄 | PR 3 |
| 5 | backtest: 過去データ源 `order-execution`・評価の窓の終端の構成（IADR-0528 決定 9）・values-local の backtest の配線の欄 | なし |

`Stage0Recording` の欄は #1320 が持つ。本作業は触らない。

### PR ごとの受け入れ基準（チェック項目）

**PR 2（報告書サービス）**
- [ ] `GET /reports/daily-policy/as-of?at=` が `ConfirmedAt ≤ at` の確定日報のうち `PeriodStart` 最大を返す。`at` より後に確定した日報・ドラフトは返さない。無ければ 404。`at` が無い・オフセットが無い時刻は 400（T-15-125）
- [ ] 認可は `GET /reports/daily-policy` と同じ `OwnerOrService`（未認証 401）

**PR 3（基底の供給の純粋な部分）**
- [ ] 判断時点 t は判断日の通常取引の終値の時刻（米国は 16:00 ET。夏時間の切り替えをまたいでも正しい）（T-15-126）
- [ ] 参考情報の窓 `[t − MaxAge, t]` を両端で切る（仕様書「母集合」の窓の表の 3 列のプローブ）・新しい順・上位 K 件・銘柄と市場を別々に数える・出所タグで限定する（T-15-127）
- [ ] 3 源がそろえば (b) は `Reconstructed`／`AbsentAtAsOf` で、理由欄に欠けた源が残る。どれかが失敗・未構成なら `NotReconstructable`（理由欄に失敗した源と欠けた源）（T-15-128）
- [ ] 休場（判断日の足が無い）・確定日報なし・方針の日付が判断日より後・基準通貨でない市場は null。価格・方針の照会の失敗は例外（記録を止める）（T-15-129）
- [ ] サイジングは IADR-0528 決定 6 の固定値。基底の供給の出力を記録器に通すと、本番と同じ組み立てで記録が 1 件作られる（T-15-129）

**PR 4（外部の口と配線）**
- [ ] 日足の照会は `[AsOf − 10 日, AsOf]`・`Available` 以外は撃ち直してから例外。方針の照会は t を UTC の ISO 8601 で渡す。Finnhub は `from/to` を判断日で切る・EDGAR は受理時刻で切る・FRED は ALFRED の前日の版（T-15-130）
- [ ] 配線: `Reports:BaseUrl` と `OrderExecution:BaseUrl` がそろったときだけ基底の供給。そうでなければ `NoAsOfDecisionInputProvider`（T-15-130）
- [ ] 🔴 新たに誤りになる注記を直す: `Program.cs` の「as-of の他の入力の実供給は無いまま」・`AsOfDecisionInput.cs` の「既定実装は常に null…実供給は残件である」・`values-local.yaml` の「as-of 入力の基底の供給は未実装（#1308）」
- [ ] 🔴 試験 `Stage0RecordingLocalProfileTests.欄を埋めて承認しても_as_of_の基底の供給が無ければ記録は_0_件で_LLM_は_0_回` を、配線後の姿（values-local の配線の欄で基底の供給が選ばれ、記録が作られる側）へ書き換える（試験の注記が求めている）
- [ ] 🔴 配備手順: #1320 は `Stage0Recording__Enabled=true` で入る。本 PR が入った版を配備すると、起動した時点で記録が LLM を呼び始める。PR 本文と B-7 に「配備の前に `Enabled` を確かめ、通し試験の値で切り替える」手順を書く
- [ ] 否定形: `Stage0Recording` が無効・未承認・期間なし・出力先なしなら LLM 0 回（既存試験のまま緑）

**PR 5（backtest）**
- [ ] 過去データ源 `order-execution`: 銘柄ごとに `/order-execution/daily-bars` を呼び、`Available` の足だけを使う。それ以外は `Gaps`。400 暦日を超える期間は分けて呼ぶ。未知・不正な構成は no-op（T-15-131）
- [ ] 評価の窓の終端の構成 `Backtest:Stage0:EvaluationEnd`（空は今日・不正な値は起動を止めずに今日へ倒し警告）（T-15-131）
- [ ] 🔴 新たに誤りになる注記を直す: `HistoricalBarSourceFactory.cs`・`BarDataOptions.cs` の「既定は none・4 点」の注記、`values-local.yaml` の backtest 節の「過去データ源は空のまま」
- [ ] 最後の PR の本文に、通し試験（1 銘柄 × 数日）の手順と承認額の見積りを書く

### 評価の窓の終端（#1320 の AI レビューの指摘）

- 評価の窓の終端は今日で、再生は記録の `To` ≥ 終端を求めるため、`To=2026-10-09` の記録は 10-10 以降の評価で `RecordingMismatch` になる。
- **方針**: PR 5 で評価の窓の終端を固定する任意の構成を足し（IADR-0528 決定 9）、経路B では 2026-10-09 に固定する。`LookbackDays` は 24（2026-09-15〜10-09）。値の欄は #1320 か PoC の手順で入れる。

## 受け入れ基準 → 試験

| # | 受け入れ基準（#1308） | 試験 |
| --- | --- | --- |
| 1 | 判断時点ごとに参照価格・方針・サイジング・参考情報・為替を、その時点までの分だけで組む | PR 3: 基底の供給の試験（未来の足・未来の記事・`t` より後に確定した方針を渡さない）。PR 4: 各口の試験（要求の期間・ALFRED の版・発行時刻での切り方） |
| 2 | 一部が再構成できないときは null を返さず、種別を申告する | PR 3: 3 源のどれかの失敗で (b) が `NotReconstructable`・欠けた源が理由欄に残る |
| 3 | 非取引日・参照価格が無い日は null | PR 3: AsOf の足が無い日・方針が無い日は null |
| 4 | 本番と同じ組み立て（`TradeDecisionPromptBuilder`） | 既存の記録器の試験（記録器は変えない）。PR 3 で基底の供給の出力を記録器に通す試験 |
| 5 | 否定形: 無効・未承認・期間なし・出力先なしなら LLM 0 回 | 既存の `Stage0DecisionRecorderTests`（記録器を変えないことを PR 4 で示す） |
| 6 | 日足が A-3 で使えないなら何が欠けるかを IADR に書く | IADR-0528 決定 1・決定 8（裁定 3 で backtest の源を決めた） |

## 通し試験（PoC が行う。最後の PR の本文に手順と承認額の見積りを置く）

1. 発注執行が moomoo 構成であること（`/order-execution/daily-bars` が `broker-not-supported` を返さない）を確かめる。
2. 12 銘柄で `split-compare` を撃ち、窓の中に分割が無いことを確かめる（あれば止める）。
3. 1 銘柄 × 数日の少額の承認で記録を回し、記録の (b)(c)(e) の申告と欠けた源の理由欄を確かめる。
4. 本番の記録（窓 2026-09-15〜、#1320 の値）。

## 残余リスク

- 前復権: 取得時点の基準で過去が調整される（分割が窓の後にあれば先読みの手掛かりになり得る）。上の分割の確認で抑える。
- 借株料: バックテストの費用モデルに無い（裁定 3 で受け入れた）。空売りを含む判断の成績は借株料のぶん良く見える。
- 本番の参考情報との差: 本番の KB は finnhub の現在値の文書・google-news・確定報告書も載せ、並びは KB の更新時刻である。記録は 3 源・発行時刻で並べる（裁定 2 で受け入れた。欠けた源は記録に残る）。
- Finnhub の過去分の深さ（無料枠で約 1 年）は文書の記述であり、実測していない。取れなければ (b) は再構成不可になる（合格へは倒れない）。
- サイジングの残枠は固定値であり、当時の本番の残枠ではない（記録は保有なしの枝だけを検証する。IADR-0351 の残る制約と同じ）。
