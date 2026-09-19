---
title: 実結線のもとで保有状況が不明なら新規建て（Open）を見送る
type: spec
status: accepted
related_ids: [FR-04, FR-10, FR-05, FR-11, UC-01, UC-02, ADR-0003, IADR-0358, IADR-0351, IADR-0119, IADR-0099, IADR-0197]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04・FR-10)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (「方針の範囲外・不確実な場合は必ず Hold」)
---

# 仕様書: 実結線のもとで保有が不明なら新規建てを見送る（#865）

## 起点

- #865。#860（IADR-0351）の監査が挙げた保留点の追随。IADR-0351「残る制約」の
  **「保有が不明のもとでの Buy をコードでは止めていない」** を実装で閉じる。
- #860 はプロンプトへ「保有: 不明」を載せ「Hold を選びます」と述べたが、**それは LLM への依頼であって
  コードの統制ではない**。LLM が従わなければ、保有を知らないままの新規買いが従来どおり発注される
  —— #854 の実測（保有を知らずに当日枠を使い切るまで買い増した）と同じ形が、リスク管理の照会が
  落ちている間だけ再現し得る。

## 現物で確認した（是正前）

| 経路 | 保有が不明のときの結果 |
| --- | --- |
| `PositionEffectResolver.Resolve(Sell, null)` | 見送り（裸の新規ショート建てを出さない。IADR-0119 決定2） |
| `PositionEffectResolver.Resolve(Buy, null)` | **`PositionEffect.Open`（通る）** |
| 金額系の統制（1 注文上限・当日残枠・段階残枠） | 効く。ただし `sizing-context` の照会が生きていることが前提 |

- 再現テスト（是正前は落ちる）: `実結線で保有が不明ならLLMがBuyを返しても新規建てを発注しない` /
  `一次スクリーニングの経路でも実結線の不明な新規建ては止まる(False/True)` /
  `実結線で建玉照会が失敗すれば買い判断は見送る`（従来は「建玉照会が失敗しても買い判断は従来どおり成立する」として
  旧挙動を固定していたテスト。本 issue の改定で期待を反転させた）。

## 決定（詳細は IADR-0358）

1. **「不明」を 2 つに割る。** `IHeldPositionProvider.IsEnabled`（`ICurrentPriceProvider` と同じ形・IADR-0099 決定3）を足し、
   未結線（`NoOpHeldPositionProvider`＝「照会していない」）と実結線（`HttpHeldPositionProvider`＝「照会したが
   分からなかった」）を区別する。既定構成（`RiskManagement:BaseUrl` 未設定）の挙動は変えない。
2. **止めるのは Open だけ。** `PositionEffectResolver.Resolve` に `requireKnownHoldingForOpen` を**必須引数**で足し
   （IADR-0163 決定2 第 1 節。省略可能にすると渡し忘れが「統制なし」になる。［2026-09-19 追記 / #877 の監査］
   当初は既定 `false` の省略可能引数で起草し、指摘を受けて必須へ改めた）、実結線かつ不明のときだけ Open を見送る。
   決済（Close）の分岐はこの判定より**前**にあり、保有が判っている限り常に通る（FR-10「手仕舞いは止めない」）。
3. **見送りは LLM 呼び出しの前に倒さない。** 発注に使う保有数は LLM 判断の**後**に引き直す（IADR-0351 決定6）。
   プロンプト用の照会が落ちていても引き直しで保有が判れば手仕舞いは通るため、**前で一律に見送ると出口を塞ぐ**。
4. **見送りの表現は既存の語彙に載せる。** 新しいイベント・通知経路は作らず、取引判断側のスキップ（構造化 WARN ログ
   ＋ 既存の `ast.trade_cycle.decisions{action=no-trade}`）で残す。

## 母集合（着手前に引いた）

**規則 9**（誤りの側の文字列で全文書を走査）で引いた。走査語: `IADR-0119 決定2` / `不明でも Buy` / `不明での Buy` /
`不明のもとでの Buy` / `不明なら Open` / `不明でも買い`。対象は追跡下の `*.md` と `*.cs`。

| 箇所 | 扱い |
| --- | --- |
| `.ai-context/adr/IADR-0119_decision-derived-close.md` 決定 2（「`Buy` は不明でも従来どおり `Open`」） | 日付つき追記で改定を記す（決定を変える追記） |
| `.ai-context/adr/IADR-0351_...md` 残る制約（「保有が不明のもとでの Buy をコードでは止めていない」）・棄却した案の行 | 日付つき追記で追随の完了を記す |
| `.ai-context/specs/20260919_854_...md`「変更しないもの」 | 経過追記（`.ai-context/specs/` は追記可・`superpowers/` は不可） |
| `backend/.../PositionEffectResolver.cs` / `TradeDecisionAppService.cs` / `Program.cs` / `IHeldPositionProvider.cs` | コード本文とコメントを是正 |
| `TradeDecisionServiceTests.cs` / `PositionEffectResolverTests.cs` / `HttpHeldPositionProviderTests.cs` | 期待を反転・肯定形と否定形を追加 |

**規則 10**（この変更で新たに誤りになる自分の記述）: `NoOpHeldPositionProvider` の「不明のもとでは売り判断が
見送りへ倒れる」というコメントは、実結線では買いも倒れるようになる —— NoOp 側の記述は未結線に閉じているため
そのまま正しい。`IHeldPositionProvider` の XML コメント（不明と 0 の区別）は意味を変えないが、区別の使い道が
増えたため `IsEnabled` の節でその旨を書いた。

**除外**: `docs/` 配下の走査ヒットは 0 件（`保有建玉` 等の語は FR-10 / FR-19 のリスク統制側の文書に在るが、
判断由来の建玉効果の解決には触れていない）。`src/ai-stock-trading` は submodule ではなく本リポジトリが実装本体。

## 受け入れ基準

1. 実結線（`IsEnabled=true`）で保有状況が不明のとき、LLM が Buy を返しても `TradeDecisionMade` が発行されない。
2. 実結線で保有状況が不明でも、**発注直前の引き直しで保有が判れば `PositionEffect.Close` は通る**（肯定形）。
3. 保有が判っているとき（あり 100 / なし 0）は挙動が変わらない（Open がそのまま出る）。
4. 未結線（NoOp）の既定構成は従来どおり（既存テストが緑のまま）。
5. 縮退制御の経路（一次スクリーニング。予算あり／なしの両方）でも同じ判定が効く。
6. 見送ったことが記録に残る（構造化 WARN ログ）。
   ［2026-09-19 追記 / #877 の監査］見送りの**理由**は観測から区別できない（`action=no-trade` の 1 種類に
   まとまる）。`deploy/observability/` にアラートルールが 1 件も無いことと併せて、追随 issue **#891** で扱う。

## 変更しないもの

- 決済（Close）の経路・数量（保有全量・IADR-0119 決定1 / IADR-0351 決定5）。
- 保有なし（0）・保有ありでの Open の挙動、金額系の統制、プロンプトの文面（#860 の全文一致テストはそのまま緑）。
- LLM 呼び出しの回数（不明でも一次・本判断は従来どおり走る。前倒しの見送りをしない）。
- リスク管理・発注執行の各サービス（本 issue は `TradeDecisionService` で完結。#864 / #847 / #852 / #869 と衝突しない）。

## 作業手順

1. 失敗するテストを先に書く（不明のときに Open が通ってしまうことの再現）。
2. `IsEnabled` を足す → `requireKnownHoldingForOpen`（必須引数）を足す → 判断サービスで配線し、
   全呼び出し元（本番 1・テスト 12）で明示する。
3. `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` / `node scripts/check-*.js`。
