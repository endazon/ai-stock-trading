---
title: 費用統制の 200 OK で isHalted / intervalMultiplier が欠けた・倍率が非正の応答を 0× と写さず Normal へ倒し、明示された停止は尊重する（#915）
type: spec
status: accepted
related_ids: [FR-01, FR-02, NFR, IADR-0031, IADR-0027, IADR-0367]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-01 情報収集 / NFR 費用)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§6 LLM 月次上限・80% で間隔延長・100% で停止)
---

# 仕様書: 費用統制ゲートの「200 だが項目欠落」を既定値 0 で読まない（#915）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01（情報収集 poller）、FR-02（定時サイクル）、NFR（費用・LLM 月次上限）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: なし（計画 ADR の決定は変えない）
- 関連 IADR: IADR-0031（費用統制 poller の同期照会・フェイルセーフは Normal）、IADR-0027（`CostControlDecision`・Halted の倍率は 0）、
  IADR-0367（#901。`{}` が `0×` と写ることを見つけ、別 issue とした）。**本作業の記録は IADR-0031 への日付つき追記**（新 IADR は作らない。
  割り当ての IADR-0404 は使わない——決定は IADR-0031「不正応答は Normal」の適用範囲の明確化であり、新しい方針ではないため）
- 関連 issue: #915（本件）、#957（B の費用統制の行＝送り手の型による契約テスト。本件の射程外）、#943（走査表の 7 行目）

## 現物で確認した（是正前・`origin/develop` 69d098c）

| 事実 | 出典 |
| --- | --- |
| `CostStateDto(bool IsHalted, decimal IntervalMultiplier)`（非 nullable の位置レコード）。本文 `{}` は `(false, 0)` になり、そのまま `CostControlGate(false, 0)` を返す | `InformationCollectionService/Infrastructure/ExternalServices/HttpCostControlGate.cs` |
| 空ボディ・非 2xx・404・例外・タイムアウトは `CostControlGate.Normal`（`(false, 1)`） | 同上・既存テスト |
| 送り手は `Results.Ok(CostControlDecision)`。`CostControlDecision(State, IntervalMultiplier)` ＋計算プロパティ `IsHalted`。**Halted では倍率 0（無効値）** を返すのが正常 | `CostControlService/Domain/CostGovernor.cs`・`Features/CostControl/GetCostState/Endpoint.cs` |
| 非テストで `CostControlGate.IntervalMultiplier` を読むのは `CollectionPollingService.EffectiveInterval` の 1 箇所で、`Math.Max(1m, …)` の下限がある（現状 0× は巡回を速めない） | `InformationCollectionService/Hosted/CollectionPollingService.cs` |
| 🔴 **是正前は `{"isHalted":true,"intervalMultiplier":null}` が逆シリアル化の `JsonException` → catch → Normal になり、明示された停止が落ちていた**（本作業の変異注入＝是正前コードでの実測で判明） | 下記「検証」 |

## 決定（写像規則）

`200 OK` の本文を読んだ後の写像を次のとおりとする（DTO は `bool? IsHalted, decimal? IntervalMultiplier`）。

| isHalted | intervalMultiplier | 結果 | 理由 |
| --- | --- | --- | --- |
| `true` | 何でも（欠落・null・0・正） | **停止**（`Halted=true`・倍率は値があればそれ、無ければ 0） | 送り手は Halted で倍率 0 を返すのが正常。倍率の検査を停止より先に当てると「費用上限 100% でも収集を続ける」へ倒れる。停止は明示された最も強い信号であり、倍率は消費側が停止時に参照しない（`HaltedRecheckMultiplier`） |
| `false` | 正 | そのまま（`(false, m)`） | 従来どおり |
| `false` | 欠落・null・0・負 | **Normal**（`(false, 1)`） | 「費用統制は何も言っていない」を 0× / 負倍と読まない。空ボディ・非 2xx と同じ安全既定（IADR-0031） |
| 欠落・null | 何でも | **Normal** | 停止か否かを判定できない不正応答。IADR-0031 の「不正応答は Normal」。`state` による補完はしない（下記「射程外」） |

**isHalted=true かつ倍率欠落の扱いの判断**: 停止を尊重する。依頼時の懸念どおり、Normal へ落とすと既存の意味（明示の停止は止まる）を壊し、
しかも費用の上限を無視する方向（fail open）へ倒れる。一方、停止を尊重して誤る場合（送り手が壊れて `isHalted:true` を誤送）の帰結は
「収集を止めて 2× 間隔で再照会する」であり、IADR-0031 がフェイルセーフに Normal を選んだ理由（一時障害で全体を止めるのは過大）は
「照会できなかった」場合の話で、「停止と明示された」場合には当たらない。

## 受け入れ基準

1. `200 OK` ＋ `{}`・`{"isHalted":false}`・`{"isHalted":false,"intervalMultiplier":null}`・`{"state":"Throttled","isHalted":false}` は `CostControlGate.Normal`。
2. `200 OK` ＋ `isHalted:false` で倍率 `0`・`-1`・`-0.5` は `CostControlGate.Normal`。
3. `200 OK` ＋ isHalted 欠落・null（倍率 2.0 があっても）は `CostControlGate.Normal`。
4. `200 OK` ＋ `isHalted:true` は倍率が欠落・0・null でも `Halted=true`。
5. 既存 `HttpCostControlGateTests`（Throttled 写像・Halted 写像・404・非 2xx・例外・空ボディ・タイムアウト）は緑のまま。
6. 変異注入: 是正前の製品コードに戻すと 1〜4 のうち是正で変わる行が赤、倍率の検査を停止より先に当てる変異で 4 と既存の Halted 写像が赤。

テスト ID: FR-01 は ID 付きのテスト仕様書を持たない（`T-01-` の使用 0 件・機能/テスト仕様書の必須範囲外＝網羅裁定 #211）。
既存テスト名の慣習（`不正_…_は_Normal_停止せず` 等の日本語名＋コメントに起点 ID）に従い、ID は振らない。

## 母集合（規則 9〜11）

- **規則 9（同型の受け手の走査）**: `git grep '/costs/state'` → 非テストの受け手は `HttpCostControlGate` の 1 箇所のみ
  （他は費用統制の提供側・テスト・IntegrationTests・設定コメント・確定済み仕様書/IADR）。念のため `git grep 'costs/'`（`backend/*.cs`・
  CostControlService とテストを除く）と `CostState|GetCostState|CostControlGate`（CostControlService 外）も引いた——`/costs/record` は
  メッセージング（`LlmCostIncurred`）で置き換えられていてコメントのみ、他の HTTP の読み手は無い。gRPC の費用状態クライアントも無い。
  フロントエンドに `/costs/` の読み手は無い。**直す対象は本件の 1 箇所だけ。**
- **規則 10（この変更で新たに誤りになる記述）**: 走査語 `IntervalMultiplier = 0`・`0×`・`{}`・`不正応答`・`CostStateDto`。
  - `IADR-0367` 本文・索引行の「`{}` を `0×` と写す製品の挙動は別 issue」: 当時の記録として正しい（凍結）。本件の決着は IADR-0031 の追記に書き、IADR-0367 は変えない。
  - `HttpCostControlGateTests` のタイムアウト試験のコメント「本文 `{}` がそのまま写って `IntervalMultiplier = 0`」: 是正前の機序の説明として正しい（過去形で書かれている）。変えない。
  - `20260925_943_cross-service-read-contracts.md` の走査表 7 行目（`IntervalMultiplier`→0）: 確定済みの作業仕様書（point-in-time）。変えない。改名の帰結は本件後「Normal」になるが、`IsHalted` の改名が停止を落とす（fail open）ことは本件後も変わらない（欠落は Normal＝停止しない）。
  - `ICostControlGate.cs` のコメント「依存先障害時は Normal」・`CollectionPollingService` の下限 1 のコメント: 本件後も正しい（下限は残す）。
- **規則 11（窓）**: 該当しない（時間差を扱う是正ではない）。

## 射程外（見送り）

- **送り手の型による契約テスト**（`CostControlDecision` を費用統制の実際の JSON 設定〔`JsonStringEnumConverter`〕で直列化して読ませる）は #957 の B に残す。
- **`isHalted` の改名・欠落で停止が落ちる（fail open）こと**は本件後も残る——欠落は判定不能として Normal に倒すため。`state`（`"Halted"`）から補完する案は、
  受け手が送り手の列挙の文字列表現に結合し直す変更であり、契約テスト（#957）と一緒に設計するのが筋なので見送る。
- 消費側 `EffectiveInterval` の下限 `Math.Max(1m, …)` は残す（二重の守り。倍率 0 < m < 1 の正の値は本件でも写すが、消費側で 1× に丸まる）。

## 検証

- `dotnet test InformationCollectionService.Tests --filter HttpCostControlGateTests`: 19/19 合格。
- 変異注入（是正前の製品コードへ戻す）: 8 件赤（受け入れ基準 1〜4 のうち是正で結果が変わるもの）。
  緑のまま残ったのは `{"isHalted":false,"intervalMultiplier":null}`・`{"isHalted":null,…}`（是正前も JsonException → Normal）と、`isHalted:true` で倍率が 0／欠落の 2 件（是正前も停止）。
- 変異注入（倍率の検査を停止より先に当てる）: 4 件赤（受け入れ基準 4 の 3 件と既存 `停止_Halted_応答を写像する`）。
