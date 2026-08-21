---
title: IADR-0137 Stage 1 の営業日は観測入力として受け取り、期間×件数の 2 条件と 120 営業日打ち切りを機械判定にする
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-12, FR-15, FR-11, UC-06, ADR-0008, ADR-0016, ADR-0022, IADR-0041, IADR-0070, IADR-0085, IADR-0127, IADR-0136]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - planning:projects/ai-stock-trading/INDEX.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md
---

# IADR-0137: Stage 1 の営業日は観測入力として受け取り、期間×件数の 2 条件と 120 営業日打ち切りを機械判定にする

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: 実装（Claude Code）／ 起点 issue [#333](https://github.com/endazon/ai-stock-trading/issues/333)（親 [#344](https://github.com/endazon/ai-stock-trading/issues/344)）
- 作業仕様書: [20260804_333_stage-gate](../specs/20260804_333_stage-gate.md)

## コンテキストと課題

計画は Stage 1（moomoo `SIMULATE`）の合格条件を数値まで確定した
（06_daytrading-review §4.1〜§4.3（計画リポ）・
INDEX 決定 34・42）。実装は [#20](https://github.com/endazon/ai-stock-trading/issues/20)（IADR-0041/0070）の
骨格のままで、Stage 1→2 の機械判定は「バックテストとの乖離が説明可能」＋「統制違反 0 件」の 2 つだけだった。

確定した規則のうち、実装判断を要したのは次の 4 点である。

1. **期間の分母をどこから得るか。** §4.2 は分母を「その日の**実際の**通常取引時間（通常日 6.5 時間／
   **半日取引日 3.5 時間**）」「判定の基準時刻は**米国東部時間**（サマータイムの切替・半日取引日に対応する）」と
   定める。しかし**ある日が半日取引日かをどこから知るかを述べていない。**
2. **旧基準「バックテストとの乖離が説明可能な範囲」をどう扱うか。** §4.1 は
   **「旧基準は合格基準から削除した」**と明記した。実装はこれを機械判定の昇格条件かつ Stage 1 の
   機械判定の撤退条件として持っていた。
3. **打ち切り（§4.3）をどの経路に載せるか。** 昇格の否定なのか、撤退（差し戻し）なのか。
4. **統制違反の件数がクラス C 限定であることを、どこで担保するか。**

## 検討した選択肢

### 論点 1（期間の分母）

1. **半日取引日カレンダーを実装が持つ** — 判定は自己完結するが、**カレンダーの誤りがそのまま昇格判定の誤りに
   なる**。米国の半日取引日は年ごとに変わり（感謝祭翌日・クリスマスイブ等）、祝日が週末に当たると振替も生じる。
   計画のどこにも出典が無い表を実装が抱えることになる。
   ADR-0022 決定3（計画リポ） は
   別件（為替の鮮度判定）で**「営業日カレンダーを保持しない。カレンダーを持たないため、カレンダーの誤りに
   起因する誤判定が原理的に起きない」**と裁定しており、その向きにも反する。
2. **ET のタイムゾーン変換と固定 6.5 時間で計算する** — DST は `TimeZoneInfo` で扱えるが、
   **半日取引日を落とす**。半日取引日に 105 分稼働した日は 105 ÷ 390 ＝ 26.9% となり算入されない。
   §4.2 が「固定の 6.5 時間を用いない」と明記した理由がここにある。
3. **その日の実際の通常取引時間を観測値として受け取る** — 実装はカレンダーも TZ 変換も持たない。
   供給側（OpenD の稼働監視・ブローカーの取引時間照会）が記録した事実をそのまま judge する。

### 論点 3（打ち切りの載せ先）

1. **昇格の否定としてだけ扱う** — 「昇格できない」は表現できるが、**Stage 0 へ差し戻す**という計画の指示が
   どこにも現れない。誰も気づかないまま Stage 1 が延々と続く。
2. **撤退（`AssessWithdrawal`）に載せる** — 既存の撤退経路は「自動＝停止・承認＝段階変更」（IADR-0041）で
   あり、非停止の降格提案（Stage 1 の旧・乖離経路）と通知の重複排除（[IADR-0085](IADR-0085_paper-withdrawal-notification-dedup.md)）が
   既に用意されている。打ち切りはこの型にそのまま収まる。
3. **両方に載せる** — 昇格判定でも理由として列挙し、撤退でも差し戻しを提案する。

## 決定

### 決定 1: その日の通常取引時間は**観測入力**として受け取り、カレンダーを実装が持たない

論点 1 の選択肢 3 を採る。`Stage1TradingDayObservation(SessionDateEasternTime, RegularSessionMinutes,
OperationalMinutes)` を観測記録として受け、`Stage1DayQualification.Qualifies` は
`OperationalMinutes / RegularSessionMinutes >= 0.50` だけを判定する。

- **計画が沈黙している論点で値を発明しない。** 半日取引日カレンダーの出どころは計画に無い。
  → feedback/20260804_fr20-stage1-session-calendar.md（環流記録） で計画へ環流する。
- **DST も同じ理由で吸収される。** 日付は「米国東部時間での取引日」として受け、実装はタイムゾーン変換を
  行わない。§4.2 が ET を要求した理由（日本時間の固定時刻で集計すると切替時に 1 時間の誤差が出る）は、
  供給側が ET の取引日で記録することで満たされる。
- **除外事由ごとの専用フラグは設けない。** §4.2 の除外 3 事由のうち、市場休場は
  `RegularSessionMinutes == 0`、OpenD 停止・ブローカー障害は `OperationalMinutes` の減少として自然に表れる。
  事由ごとのフラグを設けると「どの事由なら除外か」の解釈が実装に入り込み、計画の規則から離れる。
- **稼働分数は分母で上限を切る**（`min(1.0, 稼働 ÷ 通常取引時間)`）。時間外（プレ／アフターマーケット）の
  稼働で算入を買えないようにするためである（§4.2「プレ／アフターマーケットは含めない」）。

### 決定 2: 旧基準「乖離が説明可能」は**削除**し、機械判定から外す

計画 §4.1 が「検証不能な条件を機械判定のゲートに据えると、判定できないか恣意的に判定されるかの
どちらかになる」として合格基準から削除したのに従い、`StagePerformance.PaperDeviationExplained` と
`StageGateCriterion.PaperDeviationUnexplained` / `WithdrawalReason.PaperDeviationUnexplained` を**廃止する**。

**列とメンバを残さない。** 供給元の無いフィールドが残ると「まだ使う値」に見え、次の実装者が
判定へ結線し直す余地が残る。DB 列は `20260804090000_AddStage1Progress` で落とす。

**ただし enum の序数 1 は再利用しない**（`RejectionReason` と同じ規律・IADR-0134 決定2）。
拒否理由・撤退理由は HTTP 経路で整数として往来し、Discord 側でラベルへ写像される。序数を詰めると
**過去の記録の意味が変わる**。Discord の表示ラベルには「（廃止済みの基準）」を添えて 1 を残した。

Stage 1 の差し戻しは計画上「月報の三者比較を利用者が読み、乖離が大きいと判断した場合」に行う
**機械判定ではない**操作である。承認付きの `RequestTransition`（降格方向は合格基準不問）で従来どおり行える。

### 決定 3: 打ち切りは**昇格の否定と撤退の両方**に載せる

論点 3 の選択肢 3 を採る。

- `AssessPromotion` は `Stage1ExtensionExhausted` を未充足基準として列挙する。
  `Stage1TradeCountInsufficient` と併記されるが、**両者は意味が違う**——前者は「もう延長しない」、
  後者は「まだ足りない」である。監査で区別できることに実益がある。
- `AssessWithdrawal` は `WithdrawalReason.Stage1ExtensionExhausted` で **Stage 0 差し戻しを提案**する。
  `HaltNewEntries: false`（SIMULATE のため実弾の即時停止は不要）。段階の実降格は提案に留め、
  確定は承認付き遷移を要する（IADR-0041 の「自動＝停止・承認＝段階変更」を崩さない）。
  IADR-0085 の非停止経路の通知重複排除がそのまま効く。

**打ち切りの判定を件数充足より先に行わない。** §4.3 の表は「120 営業日を**経ても 100 件に届かない**」を
打ち切り事由としており、**期間の超過そのものは打ち切り事由ではない**。120 営業日を超えていても件数を
満たしていれば昇格できる。この読み違いは否定形テストで塞いだ。

### 決定 4: 閾値は `StageGatePolicy` が保持し、テストは設定から引く

`Stage1GateCriteria(TargetTradingDays: 60, MinimumTradeCount: 100, MaximumTradingDays: 120)` を
`StageGatePolicy.Stage1Criteria` として持つ（テスト仕様書の「閾値をマジックナンバーで書かず設定から引く」に従う）。
値そのものの計画適合は別途 `PlanConformance.Tests` の担当領域だが、**Stage 1 の 3 閾値は現時点で
計画確定値テーブルに登録されていない**（同テーブルの対象は §5 と ADR-0008/0016/0018 の値である）。
そのため本 IADR の範囲では `Stage1ProgressTests.合格条件の閾値は計画の確定値である` が固定する。

### 決定 5: クラス C 限定は既存の単一情報源に委ね、否定形テストで結線を証明する

「統制違反 0 件」がクラス C 限定であることは `RejectionReasonClassification`（#329・#374）が既に単一情報源として
実装している。`StagePerformance.ControlViolationCount` はその集計結果を受ける口である。
本 issue で追加したのは**否定形テスト**——「クラス A の拒否が 100 回積み上がっても計上は 0 件であり昇格が
止まらない」「1 回の拒否に複数理由が含まれてもクラス C を含めば 1 件として昇格が止まる」を固定した。

## 結果

- **良い影響**: 計画 §4.1〜§4.3 の規則が機械判定として表現された。カレンダーを持たないため、
  カレンダーの誤りに起因する誤判定が原理的に起きない。検証不能な条件（乖離の説明可能性）が
  ゲートから消え、恣意的な判定の余地が無くなった。
- **悪い影響 / トレードオフ**:
  - **`StagePerformance` は破壊的変更である**（`PaperDeviationExplained` の削除・2 フィールドの追加）。
    DB 列の削除を伴うマイグレーションであり、`Down` で復元はできるが値は失われる。運用前のため許容した。
  - **その日の通常取引時間の正しさは供給側に委ねられる。** 供給側が誤った分母（例: 半日取引日に 390 分）を
    記録すれば、判定は誤る。実装内で検算する術は無い——これは決定 1 のトレードオフそのものである。
    観測値の妥当性の目安として `RegularSessionMinutesFullDay` / `RegularSessionMinutesHalfDay` を
    定数で公開した（供給側が参照できるようにするため。判定には用いない）。
- **残余リスク（実装したが発動しない）**: **`Stage1QualifiedTradingDays` と `Stage1TradeCount` の供給元が
  存在しない。** 日次の稼働分数を記録するドライバも、SIMULATE の約定件数を集計する経路も未実装である。
  既定 0 は fail-safe（昇格しない）であり統制が緩む向きの不発では無いが、**本判定は実運用では
  現時点で一度も発火しない。** 供給元の実装は本 issue の範囲外であり、PR 本文に明記した。

## 関連

- 計画: 06_daytrading-review §4.1〜§4.3（計画リポ）／
  INDEX 決定 34・42（計画リポ）／
  ADR-0022 決定3（計画リポ）（カレンダーを持たない裁定の前例）
- 実装 ADR: [IADR-0041](IADR-0041_stage-gate-transitions.md)（承認＝段階変更・自動＝停止）／
  [IADR-0070](IADR-0070_stage-gate-persistence-and-approval.md)（段階別実績の単一行ストア）／
  [IADR-0085](IADR-0085_paper-withdrawal-notification-dedup.md)（非停止の降格提案の通知冪等）／
  [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定2（序数は再利用しない）／
  [IADR-0136](IADR-0136_stage-orderable-cap-ratio.md)
- 環流: feedback/20260804_fr20-stage1-session-calendar.md（環流記録）
- 仕様書: [作業仕様書 20260804_333](../specs/20260804_333_stage-gate.md)／
  [FR-20 機能仕様書](../../docs/functional/FR-20_staged-gates.md)／[FR-20 テスト仕様書](../../docs/tests/FR-20_staged-gates-tests.md)
