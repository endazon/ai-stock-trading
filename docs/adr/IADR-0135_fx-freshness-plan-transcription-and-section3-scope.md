---
title: IADR-0135 計画適合レジストリの対象化は節単位ではなく行単位とし、ADR-0022 の為替鮮度は実装修正ではなく登録簿で受ける。抽出経路の不在は登録簿では受容できない
type: impl-adr
status: Accepted
related_ids: [NFR, FR-10, FR-15, FR-17, ADR-0004, ADR-0008, ADR-0022, ADR-0023, IADR-0107, IADR-0112, IADR-0127, IADR-0128, IADR-0134]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
---

# IADR-0135: 計画適合レジストリの行単位の対象化と、為替鮮度の乖離を登録簿で受ける判断

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: endazon（利用者。#381 の準備作業として）

## 起点・関連

- 関連する計画書 ID: **ADR-0022**（為替レートの情報源と鮮度。決定1〜5）／ ADR-0023（米国株の日足 OHLC 履歴源）／
  **FR-10**（リスク統制。30 日超での新規建て停止）／ FR-17（全体前提条件）／ FR-15（バックテスト。ADR-0023 の関係先）／
  `06_technical/05_trading-assumptions.md` **§3**（為替・通貨）・**§5**（為替レートの鮮度による縮退）
- 関連する実装仕様書: [作業仕様書 20260804（#381）](../specs/20260804_381_fx-rate-plan-transcription.md)
- 関連 issue: [#381](https://github.com/endazon/ai-stock-trading/issues/381)（為替源の日銀第一化・鮮度 3/30 日）／
  [#382](https://github.com/endazon/ai-stock-trading/issues/382)（Stooq 取得不能・FR-15）／
  由来 [project-planning#59](https://github.com/endazon/project-planning/issues/59)・
  [project-planning#57](https://github.com/endazon/project-planning/issues/57)
- 先行 IADR: [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定3（本 IADR が実行する運用規律）／
  [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)（計画適合レジストリ）／
  [IADR-0112](IADR-0112_fx-rate-freshness-publication-cadence.md)（`DefaultMaxRateAgeDays = 14` の一次記録。本 IADR で根拠が計画側へ移る）／
  [IADR-0107](IADR-0107_base-currency-conversion.md)（為替レート源の安全既定）／
  [IADR-0128](IADR-0128_standard-project-layout.md)（実装型を `internal` に据え置く方針）

## コンテキストと課題

planning submodule のピンを `4cbd3e2` → `d980a01` へ進めると、**ADR-0022（為替レートの情報源と鮮度）**と
**ADR-0023（米国株の日足 OHLC 履歴源）**が新設され、`05_trading-assumptions` の §3・§5 に確定値が入る。

[IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定3 は
「ピン更新と同じ PR の中で計画差分を読み、`PlanRiskDefaults` の対象範囲に触れる変更があれば表を追随させる」
という運用規律を定めた。**本作業はその規律の初回の実行**であり、実行の過程で 4 つの論点が生じた。

1. `PlanRiskDefaults` のコメントが「**§2/§3 は「要確認」のため確定値を持たず対象外**」と述べていたが、
   ADR-0022 が §3 へ確定値を入れたことでこの説明が現状と食い違う。**§3 全体を対象化するのか、
   確定した行だけを対象化するのか**。
2. ADR-0022 の決定 1〜5 のうち、**どこまでが表に載せる「値」か**。
3. 計画と実装の乖離（`DefaultMaxRateAgeDays = 14` vs 計画の 30 日、FRED 単独 vs 日銀第一）を
   **この PR で直すのか、登録簿へ登録して担当 issue へ引き渡すのか**。
4. 転記だけでは想定した赤（値の不一致）にならず、**別の赤（抽出漏れ）**が出た。どう扱うか。

## 検討した選択肢

### 論点 A: §3 の対象化の粒度

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A-1 | **§3 全体を対象**とし、5 行すべてを転記する | 「基準通貨（判定）＝USD」は `Capital.Initial` の通貨として**既に検知対象**であり二重管理になる。「基準通貨（表示）」「為替評価方法」は表示・計算の**規則**であって値ではなく、[IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md) 決定4 が表から外した種類のものである |
| A-2 | コメントだけ「§3 は一部確定」と直し、**転記はしない** | 表と計画の乖離を残したまま説明だけ整える。最悪の形（本作業の目的そのものを果たさない） |
| **A-3** | **行単位で対象化する。**「確定していて、かつ機械的に抽出できる値」だけを載せ、節単位の除外をやめる | 節単位の除外は「節の中の 1 行が確定するたびに前提が崩れる」構造を持つ。実際、旧コメントは ADR-0022 以前から既に不正確であった（§3 の「基準通貨（判定）＝USD」は 2026-07-31 の利用者決定） |

### 論点 B: どこまでを「値」とするか

[IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md) 決定4 は、表が収録するのは
**実装から機械的に抽出できる値**（定数・設定フィールド・enum・型の有無）であり、
**振る舞いの規則は扱わない**（テスト仕様書の 3 点セットで担当 issue が検証する）と定めている。
ADR-0022 の決定はこの線で割れる。

| 決定 | 内容 | 判定 |
| --- | --- | --- |
| 決定1・2 | 日銀を第一・FRED をフォールバック | **情報源の識別子の集合は値**。ただし**順位・切り替え条件・記録・通知は振る舞い** |
| 決定3 | 営業日カレンダーを持たない | **不在の宣言**。型の不在で書けはするが、実装に対応する型が元から無いため常に一致し検知力がゼロ |
| 決定4 | 警告しきい値 3 日 | **値** |
| 決定5 | 絶対上限 30 日／縮退の段階 | **30 日は値**、縮退の段階（続行・警告・新規建て停止）は**振る舞い** |

### 論点 C: 乖離を実装で消すか、登録簿で受けるか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C-1 | この PR で `FxOptions` を直す（14 → 30・警告 3 日・日銀アダプタ） | 日銀アダプタの新設は**新しい外部情報源の結線**であり、系列 ID すら未確定（ADR-0022 決定1）。本 PR（#372・維持率割れの自動縮小）の範囲を壊す。値だけ 30 へ直して源を FRED のまま残すと、**週次公表の源に 30 日上限**という計画のどの決定とも一致しない中間状態になる |
| **C-2** | **転記し、乖離を担当 #381 として登録簿へ登録する** | [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md) の設計どおりの使い方である。乖離が**消える**のではなく**可視化されたまま担当へ渡る**。#381 が直せば検査3／検査4 が赤くなり登録簿の更新が強制される |

### 論点 D: 抽出経路の不在

転記だけを行った状態の実測は、期待していた**検査1（値の不一致）ではなく検査2（抽出漏れ）**の赤であった。

```
Failed: 計画確定値の全キーが実装側スナップショットに存在する
  欠落キー: Fx.RateSourceProviders, Fx.StaleRateWarningDays, Fx.MaxRateAgeDays
```

`FxOptions` は `TradeDecisionService.Infrastructure` の `internal` 型であり、計画適合テストの
プロジェクト参照に入っていなかった。**実装は 14 という値を持っているのに、計画適合検査からは
「無い」ようにしか見えない状態**である。

| 案 | 内容 | 評価 |
| --- | --- | --- |
| D-1 | 抽出を足さず、キーごと転記を見送る | 計画が確定させた統制値が表から消える。**#374 で見つかった穴（`StopOrderRequired` が実装済みなのに候補配列に無く見えていなかった）と同じ失敗を繰り返す** |
| D-2 | 型の不在を値として扱う（`(type FxOptions not found)`） | **嘘になる。** 型は存在し 14 を持っている。「実装に無い」と「実装に有るが見えていない」を同一視すると、#381 が値を直しても検査3 が反応しない |
| D-3 | `InternalsVisibleTo` に計画適合テストを足して直接参照する | [IADR-0128](IADR-0128_standard-project-layout.md) が絞った公開面を、テストの都合で広げることになる |
| **D-4** | **プロジェクト参照を足し、`internal` 型は完全名＋リフレクションで読む** | 公開面を広げない（`Assembly.GetType` は `internal` 型も返す）。既存の `FindType` / `DescribeTypeWithMembers` と同じ方式であり、表の書き方も揃う |

## 決定

### 決定 1: 計画適合レジストリの対象化は**節単位ではなく行単位**とする（案 A-3）

`PlanRiskDefaults` のコメントから「§2/§3 は『要確認』のため確定値を持たず対象外」という
**節単位の除外**を削除し、行単位の扱いを明記する。

- **§2（手数料・取引諸費用）**: 全行が「要確認」（口座開設後に登録）であり、**対象となる行が現時点で無い**。
  節として除外しているのではなく、**確定した行が 1 つも無い**ことを述べる。
- **§3（為替・通貨）**: ADR-0022 の 3 行を対象化する。残りのうち「基準通貨（判定）＝USD」は
  `Capital.Initial` の通貨（`TradingDefaults.EquityCurrency`）として**既に検知対象**であり二重に持たない。
  「基準通貨（表示）＝JPY」「為替評価方法」は表示・計算の規則であって値ではない。

**§3 全体の対象化（案 A-1）は採らない。** 採らない理由は「§3 が確定していないから」ではなく、
**残りの行が「値」ではないか、既に別のキーで検知されているから**である。この区別は重要で、
前者の理由づけ（＝旧コメント）は計画が動くたびに嘘になるが、後者は計画が動いても成り立つ。

> **旧コメントは ADR-0022 以前から既に不正確であった。** §3 の「基準通貨（判定）＝USD」は
> 2026-07-31 の利用者決定であり、「要確認」ではない。**節を単位に除外したことが誤りの原因**であって、
> ADR-0022 はその誤りを露見させただけである。同じ形の除外を他の節へ書かない。

### 決定 2: 転記するのは値だけとし、順位・通知・縮退の段階は #381 のテスト仕様へ委ねる（論点 B）

転記する 3 行:

| キー | 計画値（正規化） | 出典 |
| --- | --- | --- |
| `Fx.RateSourceProviders` | `boj, fred` | §3 / ADR-0022 決定1・2 |
| `Fx.StaleRateWarningDays` | `3 days` | §3 / §5 / ADR-0022 決定4 |
| `Fx.MaxRateAgeDays` | `30 days` | §3 / §5 / ADR-0022 決定5 |

- 日数は**単位つき**で正規化する（`3` ではなく `3 days`）。無次元の件数
  （`RiskLimits.MaxOpenPositions` の `3`）との取り違えを防ぐ。単位・基準を必ず含めるのは
  [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md) の `PlanDefault` の規約どおりである。
- **優先順位そのものは表に載せない。** 「第一が日銀・フォールバックが FRED」は、どちらが先かという
  順序だけでなく「いつ切り替えるか」「切り替えた事実をどこへ記録し誰へ通知するか」（決定2）を
  含む**振る舞い**である。表は**集合**として持ち、順位・通知は #381 のテスト仕様書で担保する。
- 転記しないもの: 決定3（カレンダー不在＝検知力ゼロの行になる）、日銀の系列 ID（**計画側が未確定**）、
  日銀のクレジット表記（報告書テンプレートの担当）、`FxOptions.MaxAllowedRateAgeDays = 31`
  （計画に対応する値が無い。構成クランプであり計画の「絶対上限」は既定値側に対応する）。

### 決定 3: 乖離は実装ではなく**登録簿**で受け、担当 #381 へ引き渡す（案 C-2）

3 件を `KnownPlanDeviations` へ担当 #381 で登録する。

| キー | 実装の現状 | 計画 |
| --- | --- | --- |
| `Fx.RateSourceProviders` | `fred`（FRED 単独） | `boj, fred` |
| `Fx.StaleRateWarningDays` | 定数が無い | `3 days` |
| `Fx.MaxRateAgeDays` | `14 days` | `30 days` |

**この PR で実装を直さない理由**は範囲の話だけではない。値だけ 30 へ直して情報源を FRED のまま
残すと、**週次公表の源に 30 日上限**という、計画のどの決定とも一致しない中間状態が生まれる。
ADR-0022 の 30 日は**日銀・FRED の二段構成を前提とした値**であり（決定5 の理由:
「実測で市場が沈黙した事象は FRED 単独・14 日上限という組み合わせによる」）、
**情報源と鮮度は分けて直せない**。分けて直すと、統制が緩んだだけで冗長化が入っていない
最も悪い組み合わせを一時的に作ることになる。

なお `DefaultMaxRateAgeDays = 14` の一次記録である
[IADR-0112](IADR-0112_fx-rate-freshness-publication-cadence.md) は**本 IADR では改めない**。
同 IADR の決定は「公表周期から鮮度上限を導く」という導出方法であり、その導出は
FRED 単独という前提のもとで正しかった。**計画が前提（情報源）ごと置き換えたため根拠が計画側へ移る**
という変化であり、これを記録するのは実装を実際に差し替える #381 の担当である。

### 決定 4: 抽出経路の不在は**登録簿では受容できない**。リフレクションで抽出を追加する（案 D-4）

検査2（計画確定値の全キーが実装側スナップショットに存在する）は**登録簿を参照しない**。
したがって抽出経路が無いキーは、既知逸脱として登録しても緑に戻らない。これは設計どおりであり、
「抽出漏れ」を逸脱として受容できてしまうと、**転記した値が実装と一度も突き合わされないまま
登録簿に載る**という抜け道になる。

- `AiStockTrading.PlanConformance.Tests.csproj` に `TradeDecisionService.Infrastructure` の
  プロジェクト参照を足す。
- **`InternalsVisibleTo` は増やさない。** `internal` 型は完全名 ＋ `Assembly.GetType` ＋
  `BindingFlags.Public | BindingFlags.Static` で読む（`public const` は `internal` 型のメンバでも
  リフレクションから読める）。[IADR-0128](IADR-0128_standard-project-layout.md) が絞った公開面を
  テストの都合で広げない。
- 定数が**無いこと**（`Fx.StaleRateWarningDays`）は `(FxOptions.DefaultStaleRateWarningDays not found)`
  という値として抽出する。#381 が定数を足した時点で抽出値が変わり、登録簿の更新が強制される。

**これは [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定3 が
指摘した穴の 2 例目である。** 1 例目は「候補配列に無い名前は抽出値に現れない」、2 例目は
「参照アセンブリに無い型は抽出値に現れない」であり、**どちらも `ActualDefaults` の到達範囲が
実装の全体ではないことに起因する**。転記の際は、計画側の行を足すだけでなく
**その値が実装のどこから抽出されるかを毎回確かめる**。

### 決定 5: ADR-0023（Stooq・#382）は `PlanRiskDefaults` の対象範囲に入らないため登録しない

対象範囲は **05_trading-assumptions §1/§4/§5/§6 ＋ ADR-0008 / ADR-0016 / ADR-0018** である。

- ADR-0023 が改定するのは **ADR-0004 §決定 の「検証・学習用」**であり、ADR-0008 の**値**
  （Stage 0 合格判定の DD 許容値・出金の DD 倍率）は 1 つも変えていない。ADR-0008 は
  「Stage 0 の判定が実施できない」という**帰結**の説明として参照されているだけである。
- `git -C planning diff 4cbd3e2..d980a01` の実測でも、`05_trading-assumptions` への変更は
  §3・§5（いずれも ADR-0022 由来）のみである。
- 情報源が取得できるかどうかは**値ではなく情報源の選定**であり、表が収録する既定値ではない。

**範囲外の行を足すと登録簿が「未解決の宿題一覧」へ退化する。** 登録簿の価値は
「ここに載っている＝計画確定値からの乖離であり、担当が直せば機械が検知する」という
狭い意味が保たれていることにある。

## 理由

- 決定1 の核心は、**除外の理由づけが計画の変化に対して安定であること**である。
  「その節はまだ確定していないから」は計画が動けば嘘になるが、
  「その行は値ではない／別のキーで既に見ている」は計画が動いても成り立つ。
- 決定3 の核心は、**部分的な追随が全体としては後退になり得る**ことである。統制値だけを緩めて
  冗長化を入れないのは、計画が意図した状態のどれでもない。
- 決定4 の核心は、**検査2 が登録簿を見ない設計は正しい**ということである。抽出漏れを受容可能に
  すると、転記した値が実装と一度も突き合わされないまま登録簿に載る経路ができる。

## 結果

- 良い影響:
  - ADR-0022 の確定値が計画適合検査の対象になり、**#381 が実装を直した瞬間に登録簿の更新が強制される**。
  - `PlanRiskDefaults` の対象範囲の説明が実態と一致し、**節単位の除外という壊れやすい形が消えた**。
  - `ActualDefaults` の到達範囲が `TradeDecisionService.Infrastructure` まで広がり、
    為替まわりの他の値も同じ方式で転記できるようになった。
- 悪い影響 / トレードオフ:
  - 計画適合テストが `TradeDecisionService.Infrastructure` に依存する。ビルド時間が増え、
    同アセンブリの型名変更がテストを壊し得る（リフレクションのため**コンパイルエラーではなく
    実行時の値の変化**として現れる。`(type FxOptions not found)` が出たら型名の移動を疑うこと）。
  - `Fx.RateSourceProviders` は集合であり**順位を検知しない**。日銀を足したうえで順位を逆にした
    実装は本表を通過する。順位は #381 のテスト仕様で塞ぐ必要がある。
  - 登録簿が 9 件へ増えた。件数そのものは問題ではないが、#381 の完了まで為替の乖離が
    「受容されている」状態が続く。
- フォローアップ:
  - **#381**: 日銀アダプタの新設・系列 ID の確定・`DefaultMaxRateAgeDays` の 30 化・警告 3 日の実装・
    フォールバック切り替えの記録／通知・30 日超の新規建て停止と
    [ADR-0009](../../planning/projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md)
    の停止状態への対応づけ。あわせて `MaxAllowedRateAgeDays = 31`（計画の絶対上限 30 を上回る）の見直し。
  - **#382**: 米国株の日足 OHLC 履歴源（ADR-0023）。**Stage 0 の合格判定が実施できない状態**が続いている。
  - 計画側の未決: 日銀の系列 ID（ADR-0022 フォローアップ）。確定するまで
    `Fx.RateSourceProviders` は provider 識別子の粒度に留める。

## 関連

- 実行した運用規律: [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定3
- 機構: [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)（計画適合レジストリ）
- 根拠が計画側へ移る対象: [IADR-0112](IADR-0112_fx-rate-freshness-publication-cadence.md)
  （実際の差し替えと同 IADR の改訂は #381 の担当）
- 公開面の方針: [IADR-0128](IADR-0128_standard-project-layout.md)（`InternalsVisibleTo` を増やさない根拠）
- 計画への環流: **`feedback/` に project-planning#57 / #59 に対応する控えは存在しない**（本作業で grep により確認。
  同ディレクトリ最古の記録は 2026-07-08 であり、#57 / #59 はそれ以前に **Issue 経路**で起票され控えが残らなかった）。
  裁定結果の一次記録は planning 側の [ADR-0022](../../planning/projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md)・
  [ADR-0023](../../planning/projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md) であり、
  実装側の記録は本 IADR と [作業仕様書](../specs/20260804_381_fx-rate-plan-transcription.md) が担う。
  **存在しない控えを遡って作らない**（送っていない記録を送ったことにすると環流の履歴が信用できなくなる）
