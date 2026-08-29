---
title: TradeDecisionService の ILlmGovernanceReporter 二重登録を解消する
type: spec
status: approved
related_ids: [NFR, FR-04, FR-09, FR-11, ADR-0017, IADR-0216, IADR-0217]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: `ILlmGovernanceReporter` の二重登録の解消（#595）

## 事象

`backend/Services/TradeDecisionService/Program.cs` が `ILlmGovernanceReporter` を **2 回登録**している。
85-90 行目と 92-97 行目が、**コメントを含めてバイト一致の同一ブロック**である。

```
// FR-04, FR-09, FR-11, ADR-0017 決定2/決定4, #335, IADR-0216/0217: 割当統制の可観測性。
// フォールバック発火（LlmFallbackFired）と取引判断の見送り（TradeDecisionSkipped）を publish する。
builder.Services.AddScoped<ILlmGovernanceReporter>(sp => new PublishingLlmGovernanceReporter(
    sp.GetRequiredService<IMessageBus>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<ILogger<PublishingLlmGovernanceReporter>>()));
```

## 現時点の実害と、将来の実害

- **現時点の実害は無い。** 唯一の消費点は `Program.cs:117` の
  `sp.GetRequiredService<ILlmGovernanceReporter>()` であり、`GetRequiredService` は
  **最後の登録**を 1 つだけ解決する。**列挙（`IEnumerable<ILlmGovernanceReporter>` /
  `GetServices<ILlmGovernanceReporter>()`）はリポジトリ全体で 0 件**である（母集合 軸 1・軸 4）。
- 🔴 **将来の実害はある。** `PublishingLlmGovernanceReporter` は **publish する実装**である。
  いつか列挙で全実装へ配る形（通知の多重化・監査の複線化はこのリポジトリで実際に採られている形）に
  なったとき、**`LlmFallbackFired` / `TradeDecisionSkipped` が 2 通ずつ発行される**。
  発行の重複は下流（監査台帳・通知・月報の件数）を静かに 2 倍にするため、**発見が遅れる型**である。

## 母集合の引き直し（規則 9・10）

**「誤りの側の文字列」で引く**（規則 9）。誤りは「同一インタフェースが複数回登録されている」ことなので、
**登録の呼び出し形そのもの**を走査語にした。**軸を 1 本で終わらせない**（規則 5）。

| 軸 | 走査 | 結果 |
| --- | --- | --- |
| 1 | `GetServices<` の全出現（追跡下の `.cs`・`bin`/`obj` 除外） | **2 件**。いずれも `ReportService/Tests/ReportAutoGenerationWiringTests.cs` の `IHostedService` 列挙。**`ILlmGovernanceReporter` を列挙するものは無い** |
| 2 | 11 サービスの `Program.cs` について `Add(Scoped\|Singleton\|Transient\|HostedService)<...>` を抽出して重複を取る | **1 件のみ**（`TradeDecisionService` の `AddScoped<ILlmGovernanceReporter>`） |
| 3 | 軸 2 を `Program.cs` に限らず**サービス配下の全本番 `.cs`**（`Tests/`・`bin`・`obj` を除外）へ広げる | **同じ 1 件のみ**。拡張メソッド側に隠れた重複登録は無い |
| 4 | `ILlmGovernanceReporter` の全参照 | 21 件。登録は TradeDecision 2 件（重複）＋ Report 1 件（`AddSingleton`）。消費は `GetRequiredService` の 2 件のみ |

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `ReportService/Program.cs:82` が `AddSingleton`、`TradeDecisionService/Program.cs` が `AddScoped` である**寿命の不一致** | **重複登録とは別の論点**であり、サービスごとに composition root が独立している以上、不一致それ自体は欠陥ではない。本 PR の射程（#595 = 二重登録）を広げない |
| `if` / `else` の各分岐で同じインタフェースを登録している形 | **重複ではない**（実行時にどちらか一方しか登録されない）。軸 2・3 の走査は分岐を区別しないため、ヒットした 1 件について**前後の分岐構造を読んで**真の重複であることを確認した。過去に同じ走査で `IDailyPolicyUnconfirmedNotifier` / `IReservationBrokerProbe` を誤検出した経緯がある |
| 他リポジトリ（`microservices-platform`） | 本リポジトリの composition root の問題であり、基盤は無改修（`CLAUDE.md` 技術スタック別ルール） |

### 🔴 親から渡された前提の訂正（数え直した結果）

**「`GetServices<T>` の列挙は全リポジトリで 0 件」という前提は誤りだった。実測は 2 件**である。
ただし**いずれも `IHostedService` の列挙**（`ReportService/Tests/ReportAutoGenerationWiringTests.cs`）であり、
**`ILlmGovernanceReporter` を列挙するものは 1 件も無い**という結論は変わらない。
**「0 件」ではなく「本件の型を列挙するものが 0 件」が正しい**。値を黙って合わせず、訂正として記録する。

## なぜ機械検査をすり抜けたか

- `dotnet build` は通る（重複登録は言語仕様上まったく正当である）。
- `TradeDecisionService.Tests` には composition root を起こすテストが 16 ファイルあるが、**いずれも
  `GetRequiredService` で 1 つ解決して型を確かめる形**であり、**登録の個数を見ているものは 1 つも無い**。
  `GetRequiredService` は重複があっても最後の 1 つを返すため、**二重登録があっても全部緑になる**。
- 🔴 これは本リポジトリが繰り返し踏んでいる「**『呼ばれたこと』と『結果が出口へ出たこと』は別の事実**」の
  変種である。ここでは「**解決できること**」と「**登録が 1 つであること**」が別の事実だった。

## 変更内容

1. **`Program.cs` の重複ブロック（92-97 行目）を削除する。** 残すのは 85-90 行目の 1 つ。
   コメントごと完全に同一なので、**どちらを残しても差分の意味は同じ**。前に出るほうを残す。
2. **回帰テストを 1 本置く。** `TradeDecisionService/Tests/LlmGovernanceReporterRegistrationTests.cs`。
   - **肯定形**: `GetRequiredService<ILlmGovernanceReporter>()` が `PublishingLlmGovernanceReporter` である
     （既存の配線が壊れていないこと。`ReportService/Tests/LlmGovernanceWiringTests.cs` と同型）。
   - 🔴 **本体（否定形にあたる）**: `GetServices<ILlmGovernanceReporter>()` が **ちょうど 1 件**であること。
     **重複を再導入すると 2 件になって落ちる。** これが `GetRequiredService` 型のテストでは
     捕まえられなかった性質そのものである。
   - **不在の表明だけの否定形にしない**（この波の実測。対の肯定形を必ず添える）。

## やらないこと（射程を広げない）

- 🔴 **「同一インタフェースの重複登録」を全サービスで機械検査する検査器は追加しない。**
  本リポジトリの規約は「**検査器・規約の追加は同型の事故が 2 回起きたら**」であり、本件は 1 回目である
  （軸 2・3 の全走査で他に 1 件も無いことを確認済み）。**1 回目は記録に留める。**
  本 PR が置くのは検査器ではなく、**この欠陥に対する回帰テスト**である。
- 寿命（`AddScoped` / `AddSingleton`）の統一。
- `ReportService` 側への波及（重複は無い）。

## 受け入れ基準

- [ ] `Program.cs` の `AddScoped<ILlmGovernanceReporter>` が 1 箇所だけになる
- [ ] `GetServices<ILlmGovernanceReporter>()` がちょうど 1 件を返すことをテストが固定する
- [ ] `GetRequiredService<ILlmGovernanceReporter>()` が `PublishingLlmGovernanceReporter` を返すことを
      テストが固定する（既存の配線が壊れていない）
- [ ] 重複を戻す変異でテストが赤くなることを実走で確認する
- [ ] `TradeDecisionService.Tests` の件数が減らない（既存テストの削除・skip をしない）
- [ ] build 0 Warning / 0 Error・format 差分なし・検査器全種 exit 0・カバレッジが床（0.83）を割らない
