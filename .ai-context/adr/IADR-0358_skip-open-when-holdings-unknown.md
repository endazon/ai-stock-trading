---
title: IADR-0358 実結線のもとで保有状況が不明なら新規建て（Open）を見送る — 手仕舞い（Close）は止めず、未結線の既定構成は変えない
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-10, FR-05, FR-11, UC-01, UC-02, ADR-0003, IADR-0119, IADR-0351, IADR-0099, IADR-0163, IADR-0197, IADR-0390]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-24
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
related_specs:
  - ../specs/20260919_865_skip-open-when-holdings-unknown.md
---

# IADR-0358: 実結線のもとで保有状況が不明なら新規建てを見送る

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-09-19
- 決定者: endazon（[#865](https://github.com/endazon/ai-stock-trading/issues/865)）/ Claude Code（起案）

## 起点・関連

- [#865](https://github.com/endazon/ai-stock-trading/issues/865)。[#860](https://github.com/endazon/ai-stock-trading/issues/860)（[IADR-0351](IADR-0351_held-position-in-decision-prompt.md)）の監査が挙げた保留点の追随。
- IADR-0351「残る制約」: 🔴 **保有が不明のもとでの Buy をコードでは止めていない**（[IADR-0119](IADR-0119_decision-derived-close.md) 決定2 のまま）。
  プロンプトは「保有: 不明」と明示し「Hold を選びます」と述べるが、**これは LLM への依頼であってコードの統制ではない**。
- 計画 ADR-0003:「方針の範囲外・不確実な場合は必ず Hold」。計画 FR-10:「手仕舞いは止めない」。
- 作業仕様書: `.ai-context/specs/20260919_865_skip-open-when-holdings-unknown.md`。

## コンテキスト

`PositionEffectResolver.Resolve` は、保有が不明（`null`）のとき **Sell は見送る**（裸の新規ショート建てを出さない）が、
**Buy は従来どおり `PositionEffect.Open` を返す**（IADR-0119 決定2）。#854 の実測は「保有を知らないまま当日枠を
使い切るまで買い増した」形であり、リスク管理の照会が落ちている間はそれがそのまま再現し得る。金額系の統制
（1 注文上限・当日残枠・段階残枠）は効くが、それらは `sizing-context` の照会が生きていることが前提である。

一方、**不明での Buy を一律に止めることはできない**。既定構成（`RiskManagement:BaseUrl` 未設定＝`NoOpHeldPositionProvider`）は
**常に不明**を返すため、一律に止めると単体テスト・内蔵 paper の検証を含む全経路で新規建てが一切できなくなる
（#860 が本件を保留点として切り出した理由でもある）。

## 決定

### 決定1: 「不明」を「照会していない」と「照会したが分からなかった」に割る

`IHeldPositionProvider` へ `bool IsEnabled` を足す（`ICurrentPriceProvider.IsEnabled` と同じ形。IADR-0099 決定3）。

| 実装 | `IsEnabled` | 「不明」の意味 | 不明のもとでの Open |
| --- | --- | --- | --- |
| `NoOpHeldPositionProvider`（`RiskManagement:BaseUrl` 未設定） | `false` | 照会していない | 従来どおり通す |
| `HttpHeldPositionProvider`（実結線） | `true` | 照会したが答えが得られなかった（非 2xx・例外・タイムアウト・不正応答） | **見送る** |

**新しい構成キーは作らない。** 判別は既に在る配線（`RiskManagement:BaseUrl` の有無）から導く。

🔴 **未結線（NoOp）は「構成しないことが正当な依存」である**（IADR-0163 決定2 第 2 節。同決定が
`patternDetector` を省略可能のままにしたのと同じ区別）。既定構成（`RiskManagement:BaseUrl` 未設定）は
単体テスト・内蔵 paper の検証で常用されており、そこで新規建てが一切できなくなると検証が成立しない。
したがって**未結線の不明では止めない**。止めるのは実結線の不明（照会したが答えが得られなかった）だけである。

### 決定2: 止めるのは Open だけ。判定は建玉効果が確定する地点に置き、LLM 呼び出しの前へ倒さない

`PositionEffectResolver.Resolve` へ `requireKnownHoldingForOpen` を**必須引数**として足し
（IADR-0163 決定2 第 1 節「不在が統制の無効を意味する依存は必須引数にする」——省略できるようにすると
**渡し忘れが「統制なし」を意味し**、配線を削った側は静かに緩んでテストは全緑のままになる。必須なら
渡さなくなった瞬間に**コンパイルエラー**になる）、判断サービスが `_heldPosition.IsEnabled` を渡す。
`false` が渡るのは未結線という正当な構成のときだけで、そのとき従来どおり（IADR-0119 決定2）の挙動になる。
**決済（Close）の 2 分岐はこの判定より前に在り**、
保有が判っている限り常に通る（FR-10「手仕舞いは止めない」）。不明のときは決済の分岐に入りようがないため、
**この判定が出口を塞ぐことは構造上ない**。

🔴 **「不明なら LLM を呼ばずに見送る」（#865 の射程案）は採らない。** 発注に使う保有数は LLM 判断の**後**に
引き直すからである（IADR-0351 決定6。LLM 呼び出しの間に逆指値が約定し得るため）。プロンプト用の照会が
落ちていても、**引き直しで保有が判れば手仕舞いは通る**——前で一律に見送ると、その手仕舞いが消える。
代償は、実結線の照会が落ちている間だけ LLM 呼び出しが従来どおり走ること（費用は減らない）。
**「止められない」より「閉じられない」ほうが危険である**（#506 と同じ線引き）。回帰テスト
`実結線でプロンプト時に保有が不明でも引き直しで判れば手仕舞いは通る` が肯定形で固定する。

### 決定3: 縮退制御の経路（一次スクリーニング）にも同じ判定が効く

一次スクリーニング（IADR-0351 決定4）は**門**であって発注点ではない。判定は門の後・建玉効果の確定点に
1 箇所だけ置くため、縮退制御の有無（`Decision:ScreeningContextBudgetChars` の設定／未設定）にかかわらず効く。
**判定を経路ごとに複製しない**（複製すると片方だけ落ちる）。テストは両方の構成で否定形を固定する。

### 決定4: 見送りの表現は既存の語彙に載せる（新しいイベント・通知経路を作らない）

取引判断側のスキップ（構造化 WARN ログ ＋ 既存の `ast.trade_cycle.decisions{action=no-trade}` の計上）で残す。
`OrderDispatchForgone` は**採らない**。理由:

- 同イベントは**発注執行の見送り**であり、`DecisionId` と `OrderIntent` を要求する。本件は `TradeDecisionMade` を
  発行する前に倒れるため、どちらも存在しない（作れば監査のサイクル突合＝`AuditCycleCompleteness` に
  実在しない判断 ID が混じる）。
- 同イベントを発行するのは発注執行であり、その配下は #864 が触っている（越境しない）。
- `TradeDecisionSkipped` も**採らない**。通知本文が「割当モデルが利用できません」で固定されており
  （ADR-0017 決定2 / #335）、誤帰属を再生産する。既存テストが「別の理由で出さないこと」を陰性対照として
  固定している。

姉妹の見送り（不明・保有なしの Sell＝IADR-0119 決定2、鮮度切れの Open＝#506、サイジング 0、採算不成立）も
すべてログである。**同じ種類の事実を別の経路へ流さない。**

🔴 **その代償として、見送りの理由は観測から区別できない**（`action=no-trade` の 1 種類）。
理由を区別して観測・通知できるようにするのは **[#891](https://github.com/endazon/ai-stock-trading/issues/891)**
であり、本 IADR では**語彙を 1 件だけ特別扱いしない**——先に全種類を洗い出してから決める作業である。

## 棄却した案

| 案 | 棄却の理由 |
| --- | --- |
| 不明での Open を一律に（`IsEnabled` を見ずに）止める | 既定構成（NoOp＝常に不明）の全経路で新規建てができなくなる。#860 が保留点とした理由そのもの |
| 不明なら LLM を呼ばずに見送る | 決定2 のとおり、プロンプト用の照会が落ちていても引き直しで手仕舞いが通る経路を消す。出口を塞ぐ |
| `requireKnownHoldingForOpen` を省略可能引数（既定 `false`）にする | 渡し忘れが「統制なし」になる形であり、IADR-0163 決定2 第 1 節が必須引数にせよと定める側に当たる（当初は省略可能で起草し、#877 の監査の指摘で必須へ改めた）。呼び出し元は本番 1・テスト 12 と少なく、明示の代償は小さい |
| 判定を `TradeDecisionAppService` の if 文だけに置く（純関数へ足さない） | 建玉効果の決定は純関数に閉じている（IADR-0119 決定1）。分岐を 2 箇所に分けると、決済の分岐との前後関係がテストから見えなくなる |
| `OrderDispatchForgone` / `TradeDecisionSkipped` を流用する | 決定4 のとおり（存在しない `DecisionId`／誤帰属する通知本文） |
| 新しい見送りイベント・通知ポートを作る | #865 の射程外。同型の見送りはすべてログであり、1 件だけ別格にすると運用の読み方が割れる |
| 実結線の判定を `RiskManagement:BaseUrl` の構成値から判断サービスが直接読む | 判断サービスが構成キーを知ることになる（現在は Program.cs だけが知っている）。ポートの性質として `IsEnabled` を持たせる形は `ICurrentPriceProvider` に前例がある |

## 結果

- 良い影響: 保有を知らないままの新規建てがコードで止まる。プロンプト上の歯止め（IADR-0351 決定2）が
  LLM に従われなかった場合の受け皿ができ、IADR-0351「残る制約」の 1 点が閉じる。
- 悪い影響 / トレードオフ: リスク管理の `open-positions` が落ちている間、実結線の構成では**新規建てが出なくなる**
  （手仕舞いは出る）。取引機会を落とすが、保有を知らない建玉を積むより安全側である（ADR-0003）。
  費用は減らない（決定2 のとおり LLM 呼び出しの前で倒さない）。
- 既定構成（NoOp）の挙動・プロンプトの文面・決済の経路は変わらない。

## 残る制約

- 🔴 **「実結線」はアダプタの選択であって、照会先が健全であることではない。** `IsEnabled=true` は
  `RiskManagement:BaseUrl` が設定されていることしか意味しない。設定が誤ったまま常に失敗する構成では、
  新規建てが恒久的に止まる（手仕舞いは通る）。見送りは WARN で残るが、**「止まり続けている」ことを
  能動的に知らせる経路は無い**（同型の見送りと同じ扱い）。
  **追随: [#891](https://github.com/endazon/ai-stock-trading/issues/891)**（#877 の監査の指摘。
  `BusinessMetrics.RecordTradeDecision(trigger, side: null)` は理由を区別せず `action=no-trade` の 1 種類へ
  まとめるため、Hold・方針なし・鮮度切れ・サイジング 0 と同じ枠に入る。加えて `deploy/observability/` には
  ダッシュボードしか無く**アラートルールが 1 件も無い**——見送りの理由を区別して観測・通知できるようにする）。
- 🔴 **不明の判定は台帳の射影に対するものであり、ブローカーの事実ではない**（IADR-0351 決定6 の残る制約と同じ）。
  台帳が答えれば「判っている」として Open を通す。台帳とブローカーの乖離は #849 / #864 の射程である。
  ［2026-09-24 追記 / [#934](https://github.com/endazon/ai-stock-trading/issues/934)］**「判っている」は約定済みの建玉についてであり、
  未約定の新規建て注文は含んでいなかった。** [IADR-0390](IADR-0390_working-entries-in-decision-input.md) 決定5 が同じ形の判定を
  未約定にも足した——実結線のもとで未約定の照会が不明なら Open を見送る（手仕舞いは止めない・未結線は従来どおり）。
- **損切りライン到達中の買い増しはコードでは止めていない**（IADR-0351 決定3 の 4 のまま）。本 IADR が
  止めるのは「保有が**不明**のときの Open」だけであり、「保有が**判っていて**到達中のときの Open」ではない。
