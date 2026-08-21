---
title: IADR-0148 クラス C 統制違反件数を発注審査の観測から集計し、「未供給」を 0 件と別の状態として判定する
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-11, FR-10, FR-12, UC-06, SC-03, ADR-0008, ADR-0016, IADR-0134, IADR-0137, IADR-0142]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
---

# IADR-0148: クラス C 統制違反件数を発注審査の観測から集計し、「未供給」を 0 件と別の状態として判定する

- 状態: Accepted
- 日付: 2026-08-05
- 決定者: 実装（Claude Code）／ 起点 issue [#387](https://github.com/endazon/ai-stock-trading/issues/387)
- 作業仕様書: [20260805_387_class-c-violation-count](../specs/20260805_387_class-c-violation-count.md)

## 起点・関連

- 関連する計画書 ID: **FR-20**（Stage 1 の合格判定は `SIMULATE` のみで集計）／ **FR-11** ／
  06_daytrading-review §4.1 条件1（計画リポ）
- 先行する実装 ADR: [IADR-0137](IADR-0137_stage1-trading-day-counting.md)（観測入力の系譜）／
  [IADR-0142](IADR-0142_stage1-simulate-only-aggregation.md)（観測は発注先を必須で伴う・算入は許可制）

## コンテキストと課題

計画 §4.1 条件1 は Stage 1 → 2 の合格条件として「**統制違反 0 件**」（クラス C 限定＝
`BannedSymbol` / `ManipulativeOrderPattern` を含む発注拒否）を定める。
[#333](https://github.com/endazon/ai-stock-trading/issues/333) は判定側を実装したが、件数を供給する経路が無く、
`StagePerformance.ControlViolationCount` は `public int`（非 nullable・既定 0）だった。

```csharp
if (performance.ControlViolationCount > 0)          // 供給が無い → 常に 0 → 条件1 は常に「充足」
    unmet.Add(StageGateCriterion.ControlViolationsPresent);
```

**この 0 は段階ゲートの入力で唯一 fail-safe でない既定である。** 営業日数
（[#385](https://github.com/endazon/ai-stock-trading/issues/385)）・取引件数
（[#386](https://github.com/endazon/ai-stock-trading/issues/386)）の 0 は「条件未充足＝昇格しない」に倒れるが、
違反件数の 0 は「違反が無い＝条件充足」を意味する。現在は他の 2 条件が 0 で止めているだけであり、
**#385 / #386 が供給を実装した瞬間、この 0 が「無条件で条件1 を通す」**。

決めるべきは 2 点である。

1. **「供給が無い」をどう表現し、どう判定するか**（本 issue の実質）
2. **件数をどこから、どの単位で集計するか**

## 検討した選択肢

### 論点1: 「未供給」の表現

1. **`int` のまま、既定を大きな値（例: `int.MaxValue`）にして「未供給＝違反あり」に倒す** —
   判定は fail-safe になるが、**未供給と「違反が 21 億件」が同じ形**になる。監査で
   「集計が来ていない」と「違反があった」を区別できず、画面にも意味不明な件数が出る。
2. **`int?` にして `null` を未供給とする** — 表現は正しいが、**判定の書き方が守られない**。
   `performance.ControlViolationCount > 0` は `int?` でもコンパイルが通り、
   **C# の持ち上げ比較は `null > 0` を `false` にする**——つまり #387 とまったく同じ fail-open が、
   型を変えたのに再現する。「直したつもりで直っていない」形として最も危険である。
3. **件数を第一級の値（`ControlViolationTally`）にし、判定関数の必須引数として渡す（採用）** —
   `null` は未供給、値があれば「観測された件数」。合否は型のプロパティ
   （`BlocksPromotion`）に持たせ、**未供給かを先に判定しないと件数へ到達できない**形にする。
   引数を必須にすることで、供給の結線を忘れた呼び出しはコンパイルが通らない
   （[IADR-0142](IADR-0142_stage1-simulate-only-aggregation.md) 決定1「既定値を与えない」の踏襲）。

### 論点2: 件数の供給元

1. **拒否イベント（`OrderRejected`）だけを購読して数える** — 違反は数えられるが、
   **「0 件」を主張する根拠が無い**。拒否が 1 件も無い状態は「違反が無かった」のか
   「購読が動いていない」のか区別できず、論点1 の解決が形だけになる。
2. **発注審査の結果（承認・拒否の両方）を観測として記録し、そこから数える（採用）** —
   算入対象の発注先で審査が動いていること自体が「集計が供給されている」根拠になる。
   違反件数はそのうちクラス C を含む拒否の数である。

## 決定

### 決定1: 件数は `ControlViolationTally` として持ち、`null` を未供給とする

- `ControlViolationTally(int Count)`。**値が存在すること自体が「集計が供給された」ことを意味する。**
- 合否判定は `BlocksPromotion`（`Count > 0`）としてこの型に持たせる。`tally?.Count > 0` のような
  持ち上げ比較を書く動機を消す（論点1 の選択肢2 の罠）。
- 未充足理由は `StageGateCriterion.ControlViolationCountUnavailable = 12` を**新設**する。
  `ControlViolationsPresent`（= 2）に潰さない——監査で「集計が来ていない」（供給経路の欠落）と
  「違反があった」（AI が禁止事項へ抵触した記録）を取り違えると、打つ手を間違える。
  **序数は末尾に足す**（[IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定2・
  既存の序数を詰めない）。

### 決定2: 件数は `StagePerformance` から外し、判定関数の**必須引数**にする

`StageGate.AssessPromotion(current, performance, controlViolations, policy)` /
`RequestTransition(..., controlViolations, ...)`。

`StagePerformance` の init プロパティに置くと「書かなければ 0（＝合格）」に戻ってしまう——
本レコードの他のフィールドは既定が fail-safe だが、違反件数だけはそうではない。
必須引数にすれば、**供給の結線を忘れた呼び出しはコンパイルが通らない**。
実際、本 PR ではこの変更によりすべての呼び出し箇所（サービス・テスト）がコンパイルエラーになり、
明示的に「何を渡すか」を書かせた。

あわせて `stage_performance.ControlViolationCount` 列を**削除**する。供給元が別テーブルへ移った以上
この列は死ぬ。死んだ列を残すと「まだ使う値」に見え、次の実装者が判定へ結線し直す余地が残る
（[IADR-0137](IADR-0137_stage1-trading-day-counting.md) 決定2 と同じ規律）。

### 決定3: 供給は発注審査の観測ログとし、**承認された審査も記録する**

`OrderScreeningObservation(DecisionId, Provider, RejectionReasons)` を
`OrderScreeningService.Screen` が**承認・拒否のいずれでも**返し（`ScreeningOutcome.Observation` は `required`）、
`TradeDecisionMadeHandler` が観測ログへ記録する（`order_screening_observations`）。

- **`DecisionId` が主キーであることが計上単位そのものである。** 計画は「計上単位は 1 回の発注拒否につき
  1 件（1 回の拒否で複数理由が返っても 1 件）」と定める。1 審査 1 行なら、複数のクラス C 理由が返っても
  1 件であり、メッセージ再送でも二重計上しない。
- **クラス分けは再実装しない。** 単一情報源は `RejectionReasonClassification`（#329 / #374）であり、
  集計は `RejectionReasonClassification.CountsAsControlViolation(IEnumerable<RejectionReason>)` を呼ぶ。
- **算入する発注先は許可制**（`MoomooSimulate` のみ。[IADR-0142](IADR-0142_stage1-simulate-only-aggregation.md) 決定2 を再利用）。
  FR-20 は「経過営業日数・取引件数・**統制違反件数**のいずれも `SIMULATE` の約定のみで数え」ると定める。
  3 指標で算入規則が食い違うと、「Stage 1 の実績」という言葉が指すものが指標ごとに変わる。
- 記録は**イベント発行より先**に行う。逆順にすると「発行できたが観測が落ちた」拒否が生まれ、
  件数が過小になる（緩い側）。冪等なので再送で壊れない。

### 決定4: 観測窓は**受理された段階遷移**で区切る

計画は「集計期間は **Stage 1 の全期間**」と定める。段階遷移が受理された時点で窓を区切ると、
Stage 1 に居る間の窓は「Stage 1 へ入った時点から現在まで」＝計画の期間そのものになる。

- **実DD のリセット（差し戻しのみ・[IADR-0103](IADR-0103_observed-drawdown-supply.md)）と条件が違うのは意図的である。**
  実DD は「撤退の証拠を消さない」ため昇格では区切らないが、統制違反は前段階（例: Stage 0）の記録を
  Stage 1 の合格証跡へ持ち込んではならないため昇格でも区切る。
- 区切った直後は未供給＝**昇格しない**（fail-safe）。「昇格した直後は必ず条件1 が未充足になる」ことは
  望ましい性質である——新しい Stage 1 の期間について、実際に審査が動いた証拠が集まるまで次へ進めない。
- **受理されなかった遷移要求は窓を区切らない**（拒否された要求で証跡を洗えてはならない）。否定形テストで固定した。

## 結果

- **良い影響**: 段階ゲートで唯一 fail-safe でなかった既定が塞がった。#385 / #386 が期間・件数を供給しても、
  統制違反の集計が供給されない限り Stage 1 → 2 は昇格しない。判定の未充足理由が
  「未供給」と「違反あり」に分かれ、SC-03 と監査で区別できる。
- **悪い影響 / トレードオフ**:
  - **`StagePerformance` は破壊的変更である**（`ControlViolationCount` の削除）。DB 列の削除を伴い、
    `Down` で列は復元できるが値は 0 に戻る。**もっとも、この列に意味のある値が入る経路は一度も存在しなかった**
    ため、実質的な情報の損失は無い。運用前のため許容した。
  - **審査 1 回につき 1 行が増える。** 計画の想定件数（100 件 / 60 営業日）では問題にならないが、
    保持期間の規定は計画に無い（作業仕様書の未決事項に記録した）。
  - **`GetTally()` は段階ゲートの照会のたびに COUNT を 2 回発行する。** 単一行のキャッシュを持たないのは、
    キャッシュの更新漏れが「古い 0 件」を合格として返す向きに倒れるためである。
- **残余リスク**:
  - **内蔵 `paper` で発生したクラス C の拒否は件数に計上されない。** これは計画の明示的な裁定
    （FR-20 受け入れ基準「同一期間を `paper` で稼働させても 3 指標がいずれも増えない」）に従った結果であり、
    「AI が禁止事項を犯そうとした」事実を昇格判定へ反映しない向き＝統制としては緩い側である。
    ただし `paper` 稼働は「供給あり」も作らないため、**`paper` だけで条件1 を通すことはできない**
    （未供給として止まる）。

    > **【2026-08-07 裁定・案 A（現行動作の追認）で確定した】** 環流
    > [planning#238](https://github.com/endazon/project-planning/issues/238)（クローズ済み）／
    > 追跡 [#404](https://github.com/endazon/ai-stock-trading/issues/404)。
    > **上記の「必要なら計画側へ確認する論点」は、確認され、決着した。**
    >
    > **裁定**: FR-20 の受け入れ基準は**条件1 にも適用する**。3 指標の算入対象はいずれも
    > `MoomooSimulate` 限定であり、**実装の現行動作をそのまま追認する**（コード変更は不要）。
    >
    > **理由（結論より重要なので写す）**: 条件1 だけを発注先を問わず計上すると、
    > **同じ「Stage 1 の実績」という語が指標ごとに別の母集団を指す**ことになり、昇格判定を読む人間が
    > 母集団を取り違える。**取り違えは抜け道より広い範囲に効く。** 規則を 1 つに保つことを優先した。
    >
    > **受け入れた抜け道（計画が明示的に受容した）**: **内蔵 `paper` では禁止銘柄への発注を
    > 繰り返していたが `SIMULATE` では違反しなかった AI が、条件1 を満たして昇格し得る。**
    > クラス C の定義（「AI が法令・計画上の禁止事項を犯そうとした件数」・planning#58）は
    > 発注先を問わないとも読めるため、**この扱いは定義と厳密には整合しない**。
    > 実害の範囲が狭いこと（`paper` だけでは条件1 を通せない・昇格には利用者承認が要る）を理由に受容した。
    >
    > ⚠️ **裁定は「抜け道が無い」と判断したものではない。** 計画は明示的にそう書いている。
    >
    > ⚠️ **本追記が引く計画の記述は、現在の submodule pin（`a4616a8`）には含まれていない**
    > （裁定は planning `main` にあり、pin は裁定前である。実測: pin 内の
    > `06_daytrading-review.md` に該当文字列は 0 件）。**pin の更新は別 issue の担当**である。

  - 🔴 **裁定が実装側へ申し送った検討事項が未着手である。** 計画は
    「**`paper` での違反が観測された場合に人間が気づける経路（報告書への発注先別の記録）を持つかは
    別途の検討事項とし、実装側の残余リスクとして記録を残す**」と定めた。
    **受容された抜け道は、気づける経路があって初めて「受容」として成立する** —— 現状は
    **`paper` の違反がどこにも現れない**（件数にも報告書にも）。記録するだけで実装はしていない。
  - **観測は発注審査の経路だけを通る。** 損切りの機械執行（`StopLossExecutionService`）のように
    審査を経ずに承認を出す経路は観測されない。手仕舞い・損切りはクラス C の拒否を生まないため
    件数には影響しないが、「供給あり」の根拠にもならない。

## 関連

- 計画: 06_daytrading-review §4.1 条件1（計画リポ）／
  02_requirements FR-20（計画リポ）
- 実装 ADR: [IADR-0137](IADR-0137_stage1-trading-day-counting.md)（観測入力・打ち切り）／
  [IADR-0142](IADR-0142_stage1-simulate-only-aggregation.md)（観測は発注先を必須で伴う・許可制）／
  [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定2（序数は再利用しない）／
  [IADR-0103](IADR-0103_observed-drawdown-supply.md)（差し戻しでの観測窓リセットの前例）／
  [IADR-0146](IADR-0146_backend-response-contract-fixtures.md)（契約フィクスチャ）
- 仕様書: [作業仕様書 20260805_387](../specs/20260805_387_class-c-violation-count.md)／
  [FR-20 機能仕様書](../../docs/functional/FR-20_staged-gates.md)／[FR-20 テスト仕様書](../../docs/tests/FR-20_staged-gates-tests.md)
