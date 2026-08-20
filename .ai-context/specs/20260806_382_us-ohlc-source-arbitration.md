---
title: Stooq が取得不能である裁定への追随 — 米国株日足 OHLC 履歴源の現況を実装側に可視化する（ADR-0023 決定1〜4）
type: spec
status: approved
related_ids: [FR-15, FR-20, ADR-0023, ADR-0019, ADR-0004, ADR-0005, ADR-0008, IADR-0105, IADR-0110, IADR-0138, IADR-0156]
author: Claude Code
created: 2026-08-06
updated: 2026-08-06
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
related_specs:
  - ../adr/IADR-0156_us-ohlc-history-source-absence.md
  - ../../docs/functional/FR-15_backtest.md
  - ../../docs/tests/FR-15_backtest-tests.md
  - 20260726_backtest-historical-bar-source.md
  - 20260805_342_moomoo-poc-plan.md
---

# 仕様書: Stooq が取得不能である裁定への追随（#382 / ADR-0023）

> 本仕様書は実装着手前に作成した。以降の作業は本書に沿って進める。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-15**（バックテスト＝Stage 0 の前提）・FR-20（段階ゲート）
- ユースケース（UC）: UC-06（要求トレーサビリティ表の `FR-15, FR-20 | UC-06`）
- 関連 ADR: **ADR-0023**（本作業の直接の起点。決定1〜4）・ADR-0019 決定1 項目7／決定2 工程 ⑤・
  ADR-0004（検証・学習用の情報源。ADR-0023 が部分改定）・ADR-0005（有料情報源の判断プロセス）・ADR-0008（Stage 0）
- 関連 IADR: [IADR-0105](../adr/IADR-0105_backtest-historical-bar-source.md)（実過去データ源の構造・安全既定）・
  [IADR-0110](../adr/IADR-0110_stage0-criteria-calibration.md)（閾値較正）・
  [IADR-0138](../adr/IADR-0138_stage0-drawdown-tolerance-tightening.md)（Stage 0 の最大 DD 厳格化。実効は本件に依存）・
  **IADR-0156**（本作業で新規作成）
- 対象 Issue: [#382](https://github.com/endazon/ai-stock-trading/issues/382)。依存: [#342](https://github.com/endazon/ai-stock-trading/issues/342)（moomoo PoC）

## 目的・背景

計画 ADR-0023（計画リポ）（2026-08-04）が
**Stooq を取得不能として扱い、ボット検知チャレンジの回避実装を明示的に禁じた**（決定1）。同 ADR は
「本件が解消するまでは Stage 0 の合格判定を実施できない」とも明言している（決定2）。

**実装は壊れていない。** 既定は `none`（no-op）であり、バーが取れなければ Stage 0 は不合格へ倒れる
（IADR-0105 決定2 の安全側の縮退）。問題は、**その縮退が「一時的な設定漏れ」ではなく「恒久の状態」に
変わったことが実装側のどこにも書かれていない**ことである。現行のコメントは Stooq を有効な選択肢として
説明しており、読んだ人は「設定すれば使える」と受け取る。

これは #330 / #374 と同じ「**実装した ≠ 効いている**」の型である。

## 現況の正確な記述（本作業の中核・誤読を作らないための 4 点）

**issue #382 の受け入れ基準は「使える履歴源が無く Stage 0 の合格判定を実施できない」と書くことを求めているが、
その文言は起票（2026-08-04）後の実測により既に古い。** 2026-08-05 に moomoo が代替源になり得ることが実測された
（[#382 のコメント](https://github.com/endazon/ai-stock-trading/issues/382#issuecomment-5192654759)・
[作業仕様書 20260805_342](20260805_342_moomoo-poc-plan.md) §項目 7・[blocked-tasks](../../docs/blocked-tasks.md) A-3）。

**「使える履歴源が無い」と単純化すると、実測で判明した事実（代替源の存在）を否定する誤った記述になる。**
逆に「moomoo で解決した」と書けば、裁定も実装も無いのに解決したように読める。**本作業はどちらの誤読も作らない。**
以下の 4 点が同時に読み取れる書き方を、コード・IADR・仕様書・`blocked-tasks` のすべてで統一して用いる。

> **米国株の日足 OHLC 履歴の現況（2026-08-06 時点）**
>
> 1. **実装済みの履歴源は Stooq のみであり、その Stooq は取得不能である。** JavaScript proof-of-work の
>    ボット検知チャレンジを返す。ADR-0023 決定1 は回避実装を禁じたため、**実装側で取得可能にする手段は無い**。
> 2. **既定は `none`（no-op）であり、バーが 1 本も取れなければ Stage 0 は不合格へ倒れる**
>    （安全側の縮退。IADR-0105 決定2・誤ったデータで昇格することはない）。
> 3. **moomoo OpenAPI が代替源の候補として実測された**（`QotRequestHistoryKL`・AAPL で 2006-07-24 まで・
>    追加費用なし・2026-08-05）。**ただし採用には ADR-0023 の改定裁定と moomoo アダプタの実装の両方が要り、
>    いずれも未了である。**
> 4. したがって **Stage 0 の合格判定は現時点で一度も発火し得ない。** これは一時的な設定漏れではなく、
>    上記 1〜3 が解けるまで続く**恒久の状態**である。

## 対象範囲

- **対象**（ADR-0023 決定1〜4 は裁定済みであり、追随するだけで済む範囲）
  1. `HistoricalBarSourceFactory` / `StooqHistoricalBarSource` / `NoOpHistoricalBarSource` / `BarDataOptions` の
     コメント・警告文に上記 4 点を残す。**Stooq を候補から削除しない**（提供側の仕様が戻れば再び使える可能性がある）
  2. **IADR-0105 決定2 の前提の読み替えを新 IADR（IADR-0156）に残す**——「provider 未設定は差し替え漏れ」から
     「そもそも差し替え先が無い」へ。IADR-0105 本文には日付つきの追記のみ行い、決定そのものは書き換えない
  3. **FR-15 の機能仕様書・テスト仕様書**へ現況を明記する（[#211](https://github.com/endazon/ai-stock-trading/issues/211)
     の網羅裁定における**必須範囲**の FR）
  4. **PoC 項目 7 を #342 の追跡へ反映する**（コメント投稿）。**期限の起算が他 6 項目と異なることを潰さない**
  5. 環流記録 `feedback/20260805_adr0023_us-ohlc-source-moomoo.md` に ADR-0023 の裁定結果を追記する
  6. `docs/blocked-tasks.md` を追随させる
  7. 上記のうち**機械で固定できるものをテストで固定する**（既定 provider・未採用源の扱い）
- **対象外**（計画側の裁定待ち。本 PR では手を付けない）
  - **moomoo を米国株日足 OHLC の履歴源として採用すること。** ADR-0023 は代替源を定めておらず、採用は
    計画側の改定裁定を要する。環流は PR [#395](https://github.com/endazon/ai-stock-trading/pull/395) で送付済みであり、
    [blocked-tasks](../../docs/blocked-tasks.md) B-4 に「ADR-0023 の改定裁定待ち」として登録済みである。**アダプタを実装しない**
  - **Stooq のボット検知チャレンジを回避する実装**（ADR-0023 決定1 が明示的に禁じた）
  - 代替データ源の独断での採用（有料源は ADR-0005 のプロセス、無料源は ADR-0004 の改定を要する）
  - **既存の Stooq テストの削除**（#382 の受け入れ基準に明記。パーサ・シンボル写像の検証は価値を保つ）
  - `LiveTradingGate.LiveTradingReleased = false` の変更（一切触れない）
  - Stage 0 の閾値・判定ロジックの変更

## 設計

### 1. コードのコメント・警告文（追随のみ・振る舞いは不変）

| ファイル | 変更 |
| --- | --- |
| `HistoricalBarSourceFactory.cs` | ヘッダコメントへ現況 4 点。既定 `none` の意味が「設定漏れ」から「差し替え先の不在」へ変わったことを明記 |
| `StooqHistoricalBarSource.cs` | ヘッダコメントへ「現状取得不能・回避実装は書かない（ADR-0023 決定1）・削除もしない」 |
| `NoOpHistoricalBarSource.cs` | 「差し替え漏れ検知のため警告する」というコメントと**警告文そのもの**を現況に合わせる（運用時に読む唯一の signal） |
| `BarDataOptions.cs` | `Provider` の XML doc へ「実装済みは `stooq` のみ・それは取得不能」を追記 |

**振る舞いは変えない。** provider の選択規則・レート制御・欠測記録はいずれも現行のままである。

### 2. IADR-0156（新規）と IADR-0105 への追記

IADR-0105 は `Accepted` であり、本文の実質変更は行わない（`.claude/rules/traceability.md` / 計画側 ADR 規約と同じ作法）。
**決定2 の節に日付つきの追記**を置き、現行の読み方は IADR-0156 を正とする旨を書く。索引（`docs/adr/README.md`）にも
IADR-0105 の行への追記と IADR-0156 の行の追加を行う。

### 3. テストで固定するもの

| # | 固定する事実 | 壊れる変異 |
| --- | --- | --- |
| A | **構成を何も与えなければ実効 provider は `none`**（`BarDataOptions` の既定値そのものを使う） | `BarDataOptions.Provider` の既定を `"stooq"` 等へ変える |
| B | `null` / 空 / 空白 / `none` / 未知の値はすべて実効 `none` | `ResolveProvider` の判定を緩める |
| C | **未採用の代替源 `moomoo` を指定しても no-op へ倒れる**（ADR-0023 の改定裁定待ち） | 裁定前に moomoo アダプタを結線する |
| D | `stooq` は**明示指定でのみ**選ばれる（既存 `provider_stooq_で実データ源を組み立てる` が担保） | 既定を stooq にする |

C は [IADR-0154](../adr/IADR-0154_supply-availability-declared-by-server.md) 決定7（契約フィクスチャで「供給が無いという宣言」
そのものを固定する）と同じ形である。**採用が入った日にテストが落ち、本書・IADR・`blocked-tasks` の追随が強制される。**

### 4. 文書の追随

- `docs/functional/FR-15_backtest.md`: 「過去データの供給」節へ現況 4 点の節を新設。実アダプタ行の備考を是正
- `docs/tests/FR-15_backtest-tests.md`: 実過去データ源の節へ現況を明記し、新規テストを T-15-63 として写像。
  「未カバー・実施予定」表を現況に合わせて是正（「代替は資格情報が必要」は moomoo の実測により不正確）
- `docs/blocked-tasks.md`: A-3 と「実装済みだが発動しない機能」の Stage 0 行を 2026-08-06 時点へ追随
- `feedback/20260805_adr0023_us-ohlc-source-moomoo.md`: ADR-0023 の裁定結果（決定1〜4）と、本 PR が実装した範囲／
  裁定待ちとして残した範囲を追記。**`status: open` は変えない**（ADR-0023 の改定裁定は未了）

### 5. #342 へのコメント（PoC 項目 7 の反映）

ADR-0019 決定2 は **⑤（項目 7）を ①→④ の連鎖に含めない**と明記し、期限の起算を
「基盤・可変機能ユニット双方の実装完了（go-live 相当）を起算日とし 1 か月以内」と定めている。
**#342 へのコメントでは、項目 1〜6（2026-08-31）と項目 7（go-live 起算 1 か月）を必ず別の行として書き、
「PoC の期限は 8/31」という単一の期限へ畳まない。**

## 受け入れ基準

issue #382 の受け入れ基準を転記し、§現況の 4 点に照らして文言を是正したうえで満たす。

- [ ] Stooq が現状取得不能である旨と、**回避を行わない**方針が実装側のコメント・IADR に残る
- [ ] FR-15 の機能/テスト仕様書に「**Stage 0 の合格判定を実施できない**」旨が、§現況の 4 点が同時に
      読み取れる形で書かれる（「使える履歴源が無い」とも「moomoo で解決した」とも読ませない）
- [ ] PoC 項目 7 が #342 に反映され、**期限の起算が他 6 項目と異なる**ことが読み取れる
- [ ] **既存の Stooq テストを削除しない**（パーサ・シンボル写像・取得の検証はすべて残す）
- [ ] 既定 provider が `none` であること・`stooq` が明示指定でしか選ばれないこと・**未採用の `moomoo` は
      no-op へ倒れること**がテストで固定される
- [ ] `dotnet build`（警告 0）／`dotnet test`（`Category!=Integration`）／`dotnet format --verify-no-changes`／
      `check-doc-links` / `check-commit-messages` / `check-banned-libraries` / `check-test-traceability` が通る

## テスト方針

- `BacktestService.Infrastructure.Tests/HistoricalBarSourceFactoryTests` へ §設計 3 の A・B・C を追加する
  （B は既存 Theory の拡張ではなく `ResolveProvider` の直接検証として置く。`Create` と `ResolveProvider` が
  同じ答えを返すことは IADR-0105 決定 5.1 の要請であり、両方を固定する）。
- **既存テストは 1 件も削除しない。** Stooq パーサ（ボット検知チャレンジ応答＝解析失敗を含む）・シンボル写像・
  取得・レート制御の全テストを維持する。
- 変異検査（自己確認）: 既定を `stooq` へ変える／`moomoo` を実アダプタへ結線する変異でテストが落ちることを実測する。

## 計画書との差異

- 差異: **なし（実装は ADR-0023 決定1〜4 に追随するのみ）。** ただし **issue #382 の受け入れ基準の文言**は
  起票後の実測により古くなっており、本書 §現況 の 4 点へ読み替えて満たす（内容の後退ではなく精緻化である）。
- ADR-0023 は代替源を定めていない。moomoo の採用は計画側の改定裁定を要するため、本 PR では実装しない。

## 未決事項

| # | 論点 | 状態 |
| --- | --- | --- |
| 1 | **moomoo を米国株日足 OHLC の履歴源として採用するか** | **ADR-0023 の改定裁定待ち**（blocked-tasks B-4）。環流済み（PR #395） |
| 2 | moomoo の取得枠（`remainQuota: 300`）の単位と回復周期 | 実機確認が要る（blocked-tasks A-3）。採用が決まってから |
| 3 | 前復権（`RehabType_Forward`）とバックテスト費用モデル（ADR-0016 決定14）の整合 | 同上 |
| 4 | ADR-0023 の状態が `Proposed` であること | **待ちの根拠にならない**（計画リポ `.claude/rules/adr.md`「`Proposed` は決定の効力を停止しない」）。決定1〜4 は現行値として追随する |
