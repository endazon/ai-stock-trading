---
title: 空文字の構成値で 5 サービスが起動時にクラッシュする欠陥の是正
issue: "#679"
plan_refs:
  - FR-01
  - NFR
adr_refs:
  - IADR-0292
  - IADR-0294
status: done
created: 2026-09-03
---

# 作業仕様書: 空文字の構成値で 5 サービスが起動時にクラッシュする欠陥の是正（#679）

## 背景

2026-09-03 の実配備（Helm revision 15・develop `967104b8`）で RiskManagementService が
`Failed to convert configuration value '' at 'Finnhub:ProvisionalDailyLimit' to type 'System.Int32'` で
CrashLoopBackOff（19 回再起動）へ落ちた。#668（ADR-0031 追随・IADR-0294）が入れた設定点が、
chart の「キーは書くが値は空にして既定へ委ねる」規約と噛み合っていない。

🔴 **同じ空文字でも読み方で落ち方が変わる。**

| 読み方 | 空文字の扱い |
| --- | --- |
| `services.Configure<T>(section)`（内部は `Bind`） | 黙って読み飛ばす（既定が残る） |
| `section.Get<T>()` | `InvalidOperationException` を投げる |

姉妹の `MarketDataOptions.EstimatedSymbolCount` は同じ `int`・同じ `value: ""` でありながら
`Configure<T>` で読まれているため落ちない。**この非対称が欠陥の見つけにくさの本体**である。

## なぜ CI が通ったか

**CI は配備しない。** `dotnet build` / `dotnet test` / `helm template` / `helm lint` のいずれも、
「chart が与える空文字を実際に `int` へバインドする」実行時経路を通らない。

## 受け入れ基準

- [x] `Finnhub__ProvisionalDailyLimit: ""` で 5 サービス（InformationCollection / MarketMonitor /
      Report / RiskManagement / TradeDecision）が起動できる
- [x] 空・未設定・不正値・非正値がすべて既定 300 へ倒れることをテストで固定する
- [x] 正の整数はそのまま採られる
- [ ] 実配備で 5 サービスすべてが Running になる（オーケストレータが再配備で確認する）

## 実装

- `FinnhubDailyVolumeGuardOptions.Read(IConfiguration)` を新設。`int.TryParse` ＋ 安全側フォールバック。
  既存の `DecisionOptionsLoader`（`Decision:ScreeningContextBudgetChars` を `TryParse` して
  不正値は安全側へ倒す）が確立している作法へ揃えた。
- 5 サービスの `Program.cs` の `.Get<FinnhubDailyVolumeGuardOptions>() ?? new()` を `Read(...)` へ差し替え。
- `FinnhubDailyVolumeGuardOptionsReadTests`（9 件）で空・未設定・正の整数・不正値・非正値を固定。

**非正値も既定へ倒す。** 上限 0 だと「常に超過」になり警告が常時鳴り、統制の信号が雑音に埋もれるため
（`ProvisionalDailyLimit` は超過時に警告とメトリクスを出す統制の閾値である）。

## 選ばなかった案

| 案 | 却下の理由 |
| --- | --- |
| `values.yaml` から空文字の行を消す | 設定点が文書から消える。chart の「キーを書いて設定点を示す」規約に反する |
| chart のテンプレートで空値の env を出力しない | `Fx__Provider: ""` のように**空文字自体が「no-op」という意味を持つキー**が多数あり、それらの挙動を変えてしまう |
| `ProvisionalDailyLimit` を `int?` にする | 呼び出し側すべてに null 合体が要り、既定 300 の在り処が分散する |

## 残余リスク

**同型の欠陥は `Get<T>()` で `int` / `bool` を読む他の箇所にも起こり得る。** 本 PR では検査器を足していない
（規約: 検査器の追加は同型の事故が 2 回起きてから）。2 回目が起きたら「chart が `value: ""` を渡すキーの型」を
突き合わせる検査を入れる。
