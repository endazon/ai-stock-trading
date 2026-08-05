---
title: 作業仕様書 — カバレッジ床の分母から EF Core 自動生成コードを除外し、床を引き直す
type: spec
status: review
related_ids:
  - NFR
  - IADR-0143
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
related_specs:
  - "./20260803_343_regression-test-foundation.md"
  - "../adr/IADR-0143_coverage-denominator-generated-code-exclusion.md"
  - "../DEFINITION_OF_DONE.md"
---

# 作業仕様書: カバレッジ床の分母から EF Core 自動生成コードを除外する

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **NFR**（テスト・カバレッジ運用）。本作業は計画書由来の機能実装ではなく、CLAUDE.md
  「自動化・検証・安全」の CI ゲート（カバレッジ床）自体の欠陥是正である。
- 起点 issue: [#390](https://github.com/endazon/ai-stock-trading/pull/390)（#334 の PR。マイグレーション
  追加だけで床を割った実害）
- 既存の枠組み: [作業仕様書 20260803（#343）](./20260803_343_regression-test-foundation.md) §3
  「カバレッジ floor と ratchet」が `scripts/check-coverage.js` と `coverage-floor.json` を導入した。
- 実装 ADR: [IADR-0143](../adr/IADR-0143_coverage-denominator-generated-code-exclusion.md)

## 背景と問題

`scripts/check-coverage.js` は Cobertura レポートの全ファイルを分母に入れる。ここに
**`dotnet ef migrations add` が生成するファイル**が含まれている。

develop 先端（`5df9049`）を Release・清掃済みで実測した内訳は次のとおり。

| 区分 | 被覆/行 | 率 | ファイル数 |
| --- | --- | --- | --- |
| `**/Migrations/*.Designer.cs` | 0/3,399 | 0.00% | 24 |
| `**/Migrations/*ModelSnapshot.cs` | 0/767 | 0.00% | 7 |
| （参考）`Migrations/` 配下の手書きマイグレーション本体 | 0/611 | 0.00% | 24 |
| その他（プロダクションコード＋テスト） | 10,794/12,625 | 85.50% | — |
| **合計** | **10,794/17,402** | **62.03%** | — |

自動生成 2 種だけで **分母の 23.9%（4,166 行）が恒久的に 0%** である。`.Designer.cs` は
`[DbContext]` 属性とモデルスナップショットの再掲、`*ModelSnapshot.cs` はモデル全体のスナップショットで、
**いずれも人が書くものでもテストするものでもない**（実行されるのは EF のマイグレーション適用経路であり、
これらの型の本体行ではない）。

その結果、**マイグレーションを 1 つ追加するだけで数百行の 0% コードが分母に積まれ、
機械的にカバレッジが下がる**。PR #390（#334）は実際にこれで床（62.00%）を割り、`build-and-test` が
失敗した（実測 61.10%）。develop 自体も 62.03% と紙一重であり、**次に誰がマイグレーションを足しても
同じことが起きる**。テストを 1 行も減らしていない PR が落ちる指標は、指標として壊れている。

`check-coverage.js` に除外機構は現状ない（`grep -c "exclude\|除外"` が 0）。

## 対象範囲

- 対象:
  1. `scripts/check-coverage.js` への**宣言的な除外機構**（パターン・理由・出力・歯止め）
  2. `coverage-floor.json` への除外宣言と、除外後実測に基づく `lineRateFloor` の引き直し
  3. `scripts/scripts.repo.test.js` への除外ロジックの単体テスト（否定形を含む）
  4. 測定手順（Release・清掃）の明文化
- 対象外:
  - テストの追加によるカバレッジ改善（各ドメイン issue の範囲）
  - 手書きマイグレーション本体（`Up`/`Down`）の除外（**しない**。理由は IADR-0143 決定2）
  - 分岐カバレッジ・行以外の指標の導入

## 測定手順（罠つき・必ずこの手順で測ること）

本作業の測定で監査側が実際に嵌まった罠が 2 つある。**この節を読まずに測ると CI と違う値が出る**。

1. **CI は Release ビルドである**（`ci.yml` の `build-and-test` は `--configuration Release`）。
   Debug で測ると最適化・デバッグ用コードの差で**行数そのものが変わり**、CI と一致しない
   （実測: Debug 63.11% vs Release 61.10%）。
2. **`bin` / `obj` / `TestResults` の残骸が混入する**（#353 で既知）。`findReports` は
   `backend/` 配下の `coverage.cobertura.xml` を**すべて**拾うため、過去の実行で残った
   `TestResults/<guid>/` が分母・分子とも水増しする（実測: 未清掃 64.99%・レポート 255 件 /
   清掃後 51 件）。**レポート件数（本スクリプトが毎回出力する）が 51 件から乖離していたら残骸を疑う。**

正しい手順:

```sh
# 1) bin / obj / TestResults を退避（削除でも可。ただし rm -rf は本リポの hook が禁止）
find backend -type d \( -name bin -o -name obj -o -name TestResults \) -prune -print \
  | while read -r d; do mv "$d" "<退避先>/$(echo "$d" | tr '/' '_')"; done
# 2) Release でビルド
dotnet restore backend/backend.slnx
dotnet build   backend/backend.slnx --no-restore --configuration Release
# 3) CI と同じフィルタ・同じ収集で実行
dotnet test    backend/backend.slnx --no-build --configuration Release \
  --filter "Category!=Integration" --collect:"XPlat Code Coverage"
# 4) 集計（--no-exclude で除外前の値も出せる）
node scripts/check-coverage.js
node scripts/check-coverage.js --no-exclude
```

## 設計

### 1. 除外パターンは `coverage-floor.json` に宣言的に持つ

```jsonc
"exclude": [
  { "pattern": "**/Migrations/*.Designer.cs",     "reason": "dotnet ef migrations add が生成する ..." },
  { "pattern": "**/Migrations/*ModelSnapshot.cs", "reason": "dotnet ef が生成する ..." }
]
```

- **置き場所**: 床の値と同じファイル。床は「何を分母に入れるか」を決めてから初めて意味を持つ数値であり、
  2 ファイルに分けると**床を見直す PR のレビューに除外集合が現れない**（床だけが動き、何を外して測った
  床なのか追えなくなる）。1 ファイルなら差分 1 つで両方が見える。根拠は IADR-0143 決定1。
- **`reason` は必須**。理由の無いパターンは検査で失敗させる（「とりあえず外して恒久化」を防ぐ）。
- **書式は glob**（`**` = 任意のディレクトリ階層・`*` = `/` を跨がない）。issue や CLAUDE.md で人が書く
  表記そのままであり、レビュアがパターンの効果を読み取れる。正規表現をコードへ埋め込む案は、
  「何をなぜ外したか」が設定に現れないため採らない。

### 2. 除外は必ず出力する（黙って分母を縮めない）

`check-coverage.js` は既に「被覆/行・レポート件数」の内訳を出している。同じ形で除外の内訳を出す。

```
[check-coverage] 除外 31 ファイル・4166 行（うち被覆 0 行）。分母 17402 → 13236 行
[check-coverage]   - **/Migrations/*.Designer.cs: 24 ファイル・3399 行  … 理由
[check-coverage]   - **/Migrations/*ModelSnapshot.cs: 7 ファイル・767 行 … 理由
[check-coverage] 行カバレッジ 81.55%（10794/13236 行・レポート 51 件）/ floor 79.00%
```

### 3. 除外が効きすぎないための歯止め（2 方向）

| # | 歯止め | 挙動 |
| --- | --- | --- |
| G1 | **除外率の上限** `maxExcludedLineShare`（既定 0.35） | 除外行が全体行のこの割合を超えたら**失敗**。パターンが実コードを飲み込む退行を止める |
| G2 | **空振りパターンの通知** | 1 ファイルも一致しないパターンは CI notice で報告する（誤記・対象消滅の検知）。失敗はさせない |
| G3 | `reason` 必須 | 理由の無いエントリは失敗 |
| G4 | 手書きマイグレーション本体は**除外しない** | `Up`/`Down` は人が編集し得る（データ移行・生 SQL）ため分母に残す。IADR-0143 決定2 |

G1 の 0.35 は現状の除外率 23.9% に対する余裕であり、「マイグレーションが今の 1.5 倍に増えても通るが、
`**/*.cs` のような事故は止まる」水準に置く。

### 4. 床の引き直し

除外後実測 **81.55%** に対し、`check-coverage.js` が自ら持つヒステリシス `RATCHET_MARGIN = 0.02`
（2 ポイント）を引いた **0.79** を新しい床とする。これは `--suggest` が出す候補値と**同一の計算**であり、
本作業のために別の余裕幅を発明しない。余裕 2.55 ポイントの内訳は「不安定テストの揺れ」＋
「マイグレーション本体（1 件あたり約 25 行 ≒ 0.15 ポイント）の追加」である。

## 受け入れ基準

- [ ] `coverage-floor.json` の `exclude` に、パターンと理由が対で宣言されている
- [ ] `node scripts/check-coverage.js` が除外の内訳（パターン別ファイル数・行数・分母の変化）を出力する
- [ ] `--no-exclude` で除外前の値も測れる
- [ ] 除外率が `maxExcludedLineShare` を超えると失敗する
- [ ] 理由の無い除外エントリがあると失敗する
- [ ] 空振りパターンが通知される
- [ ] 手書きマイグレーション本体（`20260804090000_AddStage1Progress.cs` 等）が除外されない
- [ ] `Migrations` / `Designer` の語を含むだけの通常ファイルが除外されない（否定形テスト）
- [ ] 新しい床で `node scripts/check-coverage.js` が通る
- [ ] `dotnet build backend/backend.slnx --configuration Release` が 0 Warning / 0 Error
- [ ] `dotnet test`（`Category!=Integration`）が全件成功（ベースライン 2,638 件）
- [ ] `node scripts/scripts.test.js` が通る

## テスト方針

`scripts/scripts.repo.test.js`（キット配布の `scripts.test.js` は**バイト一致に保つ**規約のため、
本リポ固有のテストは companion 側に置く。既存の `check-coverage` テスト 5 本も同ファイルにある）へ追加する。

| 確認 | 種別 | 内容 |
| --- | --- | --- |
| 自動生成が除外される | 正 | `.Designer.cs` / `*ModelSnapshot.cs` が `Migrations/` 配下で一致する |
| **通常のプロダクションコードが除外されない** | **否定形** | `MigrationsRunner.cs`・`Migrations.cs`・`DesignerLayout.cs`・`Services/.../Migrations/` の外の `*.Designer.cs` が一致しない |
| **手書きマイグレーション本体が除外されない** | **否定形** | `Migrations/20260805044003_AddStage1ExcludedInternalPaperDays.cs` が一致しない |
| 除外の内訳が出る | 正 | 除外結果がパターン別のファイル数・行数を持つ |
| 除外率の上限で失敗する | 否定形 | 除外が過大なら `violations` を返す |
| 理由の無いエントリで失敗する | 否定形 | `reason` 欠落で `violations` を返す |
| 空振りパターンを通知する | 正 | 一致 0 件のパターンが報告される |
| 実ツリーの設定が健全 | 契約 | `coverage-floor.json` の `exclude` が全件 `reason` を持ち、実ツリーの除外率が上限内 |

## 計画書との差異

差異なし。本作業は計画書由来の値・仕様を一切変更せず、CI ゲートの測り方のみを是正する。

## 未決事項

1. **手書きマイグレーション本体（0/611 行）の扱い**。本作業では分母に残す（IADR-0143 決定2）。
   マイグレーション適用の結合テスト（`Category=Integration`・#82 / IADR-0049）が既定 CI から
   除外されている限り恒久的に 0% であり、将来「除外する」か「nightly の integration 分を
   合算する」かの判断が要る。
2. **床の再引き上げ**。81.55% に対し床 79% は 2.55 ポイントの余裕がある。各ドメイン issue の
   テスト追加後に人手の PR で `--suggest` の候補へ引き上げる（自動 ratchet はしない）。

## 変更履歴

| 日付 | 内容 |
| --- | --- |
| 2026-08-05 | 初版作成（着手前） |
