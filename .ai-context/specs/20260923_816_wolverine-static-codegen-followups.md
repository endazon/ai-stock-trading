---
title: Wolverine 静的コード生成の周辺の小さな不整合を直す（Dockerfile の検出・環境変数に依存する 3 テスト・IADR 追記の言い過ぎ。#816）
type: spec
status: accepted
related_ids: [NFR-01, ADR-0006, ADR-0013, IADR-0129, IADR-0268]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0013_messaging-follow-wolverine-kafka.md
---

# 仕様書: Wolverine 静的コード生成の周辺の小さな不整合（#816）

## 起点

- **[#816](https://github.com/endazon/ai-stock-trading/issues/816)**（tech-debt）。PR #815（[#811](https://github.com/endazon/ai-stock-trading/issues/811) の残項目）の
  監査で見つかった非ブロッキングの指摘 3 点。
- 起点 ID: **NFR-01**（保守性）。固定しているのは [IADR-0129](../adr/IADR-0129_wolverine-messaging-topology.md)
  2026-09-17 追記（決定 6-2〜6-5。`codegen write` ＋ `TypeLoadMode.Static`）。
- 計画 ADR: ADR-0013（Wolverine 移行）／基盤 ADR-0006（.NET / コンテナ）。

## 対象範囲

- 対象:
  1. `backend/Dockerfile` の codegen 分岐の検出規則とそのコメント。
  2. `.ai-context/adr/IADR-0129_wolverine-messaging-topology.md` の 2026-09-17 追記 (iii) の言い過ぎ（＋索引行）。
  3. `AiStockTrading.TestSupport.PlatformShim.Tests` の 3 テストの、プロセス環境変数への依存。
  4. 1 の回帰を止める `scripts/scripts.repo.test.js` の 1 ブロック。
- 対象外: **製品コードは 1 行も変更しない**（`WolverineExtensions` / 各サービスの `Program.cs` は無改修）。
  `ci.yml` の乾式 publish が codegen を通さないこと（IADR-0129 の残余として据え置き）。chart / values。

## 🔴 実測（起票内容の再現）

稼働イメージと同じ `WOLVERINE_TYPE_LOAD_MODE=Static` を環境に置いたまま当該テストプロジェクトを走らせる。

```
$ WOLVERINE_TYPE_LOAD_MODE=Static dotnet test backend/TestSupport/AiStockTrading.TestSupport.PlatformShim.Tests/…csproj --no-build
失敗!   -失敗:     3、合格:   141、スキップ:     0、合計:   144
  失敗 … WolverineTopologyTests.自分が購読している型の発行もブローカの共有_exchange_へ向かう
  失敗 … WolverineHandlerCodegenTests.内部実装に依存するハンドラも生成コードから実行できる
  失敗 … FoundationRegistrationTests.共通再試行を適用したメッセージ基盤は解決できる
   JasperFx.CodeGeneration.MissingTypeException : Missing expected pre-generated type(s) …
```

機序は PR #815 が直した 1 件と同型である。3 件はいずれも `UseAiStockTradingRabbitMq` を通した
**ホストを実際に起動する**テストであり、共通配線は `TypeLoadModeHasChanged` が立っていないとき
プロセスの環境変数を読む（IADR-0129 決定 6-2）。リポジトリに生成物はコミットしないので、
`Static` と読めば起動時の表明（`WolverinePreGeneratedCodeAssertion`。決定 6-3）が必ず落ちる。
**テストが固定している性質（再試行方針・生成コードの組み立て・経路の向き先）とは無関係な失敗**である。

## 直し方

### 1. Dockerfile の検出を検査器と同じ規則へ寄せる

現行は `grep -q 'UseWolverine('` で、(a) コメント中の `UseWolverine(` に反応し、(b) `UseWolverine (` のような
空白入りには反応しない。`scripts/check-consumer-endpoint-names.js` の `wiringOf()` は
**行頭が `//` / `*` / `/*` の行を除いてから** `/UseWolverine\s*\(/` を当てる。Dockerfile 側を同じ 2 段へ改める。

```sh
grep -Ev '^[[:space:]]*(//|\*|/\*)' "<Program.cs>" | grep -Eq 'UseWolverine[[:space:]]*\('
```

コメントは「同じ信号」という断定をやめ、**同じ 2 段の規則を写している**ことと、写しであるがゆえに
`scripts.repo.test.js` の回帰テストが両者を突き合わせることを書く。

- 🔴 **`grep -q` を `grep -Eq` にするだけでは足りない**（コメントへの反応が残る）。`-v` の段が要る。
- パイプの終了コードは最後の `grep -Eq` のものなので、前段が 0 行を出したときは 1（＝else 枝）になる。

### 2. IADR-0129 の 2026-09-17 追記 (iii) の言い過ぎを是正する

(iii) の「配線するサービスは実呼び出しで必ず一致する（取りこぼしは起きない）」は誤り
（`UseWolverine (` のように空白を挟めば grep に拾われない）。

- **本文は書き換えない**（`.ai-context/` は凍結記録）。**日付つき追記ブロック
  `## ［2026-09-23 追記 / #816］…`** を末尾（「## 関連」の直前）に足し、(iii) の 2 点を是正する:
  - 取りこぼしは起き得た（空白入りの呼び出し）。ただし**無音ではない** —— 生成コードを欠いたイメージは
    決定 6-3 の表明により**起動時に**落ちる（`MissingTypeException`。Pod の起動失敗）。
  - 本 PR で Dockerfile の検出を `wiringOf()` と同じ 2 段へ寄せたので、ずれ自体を消した。
- `updated:` を 2026-09-23 へ前進させる。
- `.ai-context/adr/README.md` の IADR-0129 索引行へ本追記の要約を**末尾に追加**する。
  🔴 **既存の 4 つの追記ブロックを 1 つも落とさない**（`node scripts/check-adr-index-addendum-loss.js`）。
- **新しい IADR は起こさない。** 決めているのは既存決定の表現の是正であり、
  同追記の親決定（6-2〜6-5）と同じ 1 箇所に置くべきものである（追記の先例に倣う）。

### 3. 3 テストを環境変数から独立させる

起票の「直し方」は PR #815 と同じ**退避・復元＋並列化無効のコレクション**を挙げるが、本 PR は
**呼び出し側の明示設定（`opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic`）で固定する**形を採る。
理由:

- 解決順の①は「呼び出し側の明示設定」であり（IADR-0129 決定 6-2）、`WolverineTypeLoadModeTests.呼び出し側の明示設定は共通配線に上書きされない`
  が**既に固定している経路**である。プロセスの環境変数を読む枝そのものへ入らない。
- 退避・復元は**プロセス全体の状態を触る**ため、使うクラスを `ProcessEnvironmentCollection`
  （`DisableParallelization = true`）へ入れる必要がある。3 クラス（計 24 テスト）を直列化する代償を、
  1 行の明示設定で避けられる。
- 退避・復元が要るのは「**環境変数を読む枝そのもの**を固定するテスト」だけである —— それは
  `WolverineTypeLoadModeTests.共通配線の既定は_Dynamic_である`（PR #815 が直した 1 件）であり、
  **そちらは現行のまま据え置く**。

🔴 **前提（実測で確かめた）**: `TypeLoadMode` の setter は、**既定値と同じ `Dynamic` を代入したときにも**
`TypeLoadModeHasChanged` を立てる。立たなければ明示設定は効かず起票の勧める形へ倒す必要があったが、
`WOLVERINE_TYPE_LOAD_MODE=Static` を置いた実走が **144 passed / 0 failed**（前は 3 failed）で成立を示した。

固定は 1 行の拡張メソッド `WolverineTestOptions.PinDynamicTypeLoadMode()`（**テストプロジェクト内**に置く。
共有 shim `AiStockTrading.TestSupport.PlatformShim` は 11 サービスの `Program.cs` が参照する製品コードなので、
テスト専用の口を生やさない）。**共通配線より前に**呼ぶ。

対象:

| ファイル | テスト |
| --- | --- |
| `FoundationRegistrationTests.cs` | `共通再試行を適用したメッセージ基盤は解決できる` |
| `WolverineHandlerCodegenTests.cs` | `内部実装に依存するハンドラも生成コードから実行できる` |
| `WolverineTopologyTests.cs` | `自分が購読している型の発行もブローカの共有_exchange_へ向かう` |

### 4. 1 の回帰テスト

`scripts/scripts.repo.test.js` へ 1 ブロックを足す。`backend/Dockerfile` から検出コマンドを**実ファイルから
抜き出して** `sh -c` で実走し、同じ入力に対する `check-consumer-endpoint-names.js` の `wiringOf()` の
判定と一致することを確かめる（4 件の入力: 素の呼び出し／空白入り／`//` コメント／呼び出しなし）。
**手で写した規則は、突き合わせる相手が無ければ黙ってずれる**（#816 そのものがその実例）。

- テスト ID は振らない（本リポの C# テストは FR 系列〔`T-10-*` 等〕に限って ID を持ち、
  `scripts/*.test.js` と `PlatformShim.Tests` は ID を持たない既存慣行に従う）。

## 受け入れ基準

- [x] AC1: `WOLVERINE_TYPE_LOAD_MODE=Static` を置いた `dotnet test AiStockTrading.TestSupport.PlatformShim.Tests`
      が 144 件すべて合格（前は 3 失敗）。**実測: 成功! -失敗: 0、合格: 144**。
- [x] AC2: 環境変数**なし**の同テストも従来どおり全件合格（陰性対照）。**実測: 0 失敗 / 144 合格**。
- [x] AC3: `node scripts/scripts.test.js` の新ブロックが緑
      （`ok backend/Dockerfile: codegen 分岐の検出規則が wiringOf() と同じ判定になる（#816）`）。
      🔴 **同スイート全体は Windows ローカルでは完走しない**（`setup.sh` の試験が
      `/usr/bin` の symlink で PATH を絞る設計で、Windows の node からは bash を解決できず ENOENT。
      **本 PR と無関係の既存の制約**で、CI（ubuntu）では完走する）。
- [x] AC4: `check-adr-index-addendum-loss.js`（追記 90 件すべて健在）/ `check-adr-index-sync.js` /
      `check-doc-links.js` / `check-trace-blocks.js` / `gen-knowledge-graph.js --check` /
      `check-cross-repo-refs.js` / `check-plan-id-qualification.js` / `check-reading-budget.js` /
      `check-consumer-endpoint-names.js` / `check-commit-messages.js` が緑。
- [x] AC5: `dotnet format --verify-no-changes` が変更したプロジェクトで無差分。
- [x] AC6: Dockerfile の検出が、コメント中の `UseWolverine(` に反応せず `UseWolverine (` に反応する
      （4 の回帰テストが `sh` 実走で示す）。**変異試験 2 件で番人であることを確認**:
      ①素の `grep -q` へ戻す → 文字列レベルの表明が落ちる ②コメント除外を `//` だけに痩せさせる
      （文字列の表明は 2 つとも通る） → **実走の突合が `" * builder.Host.UseWolverine( …"` で落ちる**。
- [x] AC7: 現行ツリーの全 12 `Program.cs` に対する判定が新旧で一致する
      （11 サービス codegen / opend-auth-gateway skip。＝イメージビルドの挙動は不変）。

## 未決事項

- `ci.yml` の乾式 publish は codegen を通さない（IADR-0129 の残余のまま。本 PR では触らない）。
- 索引行の追記ブロック消失検査は `.ai-context/adr/README.md` の索引行だけを見る（本体の追記は対象外。
  IADR-0363 決定の射程）。本 PR で広げない。
