---
title: 作業仕様書 — FluentAssertions を AwesomeAssertions へ全置換する
type: work
status: review
related_ids: [NFR, IADR-0001]
author: endazon (with Claude Code)
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0010_dotnet-10-follow.md
related_specs:
  - ../adr/IADR-0001_repo-structure-and-stack.md
  - ./20260802_344_reimplementation-preparation.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: FluentAssertions → AwesomeAssertions 全置換（#351）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR。テスト基盤のライブラリ差し替え）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: 計画 ADR-0001（platform 再利用）・ADR-0010（.NET 10 追随）／ platform ADR-0030（アプリ層標準）
- 実装 ADR: [IADR-0001](../adr/IADR-0001_repo-structure-and-stack.md)（基盤リポに規約を揃える）
- 起点 issue: [#351](https://github.com/endazon/ai-stock-trading/issues/351)（親 [#345](https://github.com/endazon/ai-stock-trading/issues/345) / [#344](https://github.com/endazon/ai-stock-trading/issues/344)）

## 目的・背景

**FluentAssertions は v8 で商用ライセンスへ移行した**ため、platform ADR-0030 は不採用とし
AwesomeAssertions（FluentAssertions v7 系のフォーク）を標準と定めた。現行は v7.2.0（商用化前）に
留まっているが、依存更新のたびに踏む地雷であり、早く外すほど安全である。

本作業は [#345](https://github.com/endazon/ai-stock-trading/issues/345) の分割 1/4。#345 は 4 つの独立した大規模移行を含み、実測で単一 PR が
500 ファイル超になるため、利用者判断により 4 つの子 issue（#351〜#354）へ分割した。

**本件を最初に行う理由**: 4 件のうち唯一「振る舞いを変えない機械的置換」であり、既存の全テストが
「移行でテストの意味が変わらないこと」の保証になる。他 3 件（xUnit v3・プロジェクト構成・Wolverine）は
いずれも振る舞いか構造を変えるため、先に本件で足場を固める。

## 対象範囲

- 対象:
  - `Directory.Packages.props` の `FluentAssertions 7.2.0` → `AwesomeAssertions 9.5.0`
  - 全テストプロジェクト（**39 `.csproj`**。うち 1 件は develop 取り込みで加わった `PlanConformance.Tests`）の `PackageReference`
  - 全テストファイル（**287 `.cs`**。同上）の `using FluentAssertions;`
  - 不採用ライブラリの再混入を止める CI 検査（`scripts/check-banned-libraries.js` ＋ `banned-libraries` ジョブ）
  - `CLAUDE.md` の技術スタック別ルールの記載
- 対象外:
  - xUnit v2 → v3（**#352**）
  - プロジェクト構成 3 → 7 標準（**#353**）
  - MassTransit → Wolverine（**#354**）
  - `AGENTS.md` / `.github/copilot-instructions.md`（実測で FluentAssertions への言及が無く、変更不要）

## 設計

### バージョンの選択

| 候補 | 内容 | 判断 |
| --- | --- | --- |
| 7.2.1 | 現行 FluentAssertions 7.2.0 の直系フォーク。確実に drop-in | 保守的だが旧メジャーに留まる |
| **9.5.0（採用）** | 最新安定版 | **実測でビルド 0 error・テスト結果が完全一致したため採用** |

「互換性が不安だから古い版」ではなく、**実測して問題が無いことを確認したうえで最新版**を採る。
非互換があれば手当ての一覧をここに列挙する予定だったが、**手当てを要した箇所は 1 件も無かった**
（下記「検証結果」）。名前空間名以外の API 差分に遭遇しなかったため、置換は `using` と
パッケージ参照の 2 種のみである。

### 不採用ライブラリの再混入防止

剥がした事実をコードレビューの記憶に頼ると、サンプルコードの貼り付け・AI の既定選択・依存の推移で
容易に戻る。`scripts/check-banned-libraries.js` が機械的に止める。

- `BANNED`: **今すぐ検査してよいもの**を載せる（FluentAssertions ＋ MediatR / AutoMapper / Mapster の 4 件）
- `PENDING`: **現に使われていて剥がすまで BANNED にできないもの**（MassTransit のみ。実測 140 ファイル → #354）

**移行前に BANNED へ登録して検査を skip する運用は採らない**。無効化された検査は無いのと同じであり、
再有効化は必ず忘れられる。`PENDING` に一覧を残すのは「忘れられた移行」を可視化するためである。

**ただし「移行未完了」と「そもそも導入されていない」は別物である**。MediatR / AutoMapper / Mapster は
実測で**参照 0 件**であり、今 `BANNED` に置いても既存コードを一切壊さない。むしろ #353 / #354 の
着手前に置くことに意味がある — 本検査器が動機として挙げる再混入経路（AI の既定選択・サンプルコードの
貼り付け）が最も働くのはまさにその作業中であり、無コストで防げるものを見送る理由が無い
（PR #355 の AI レビュー指摘）。

検出は `Include="<名前>"` と `using <名前>` の 2 パターンに限定し、**散文中の言及は誤検出しない**
（「FluentAssertions から移行した」と経緯をコメントへ書けること）。前方一致による巻き込み
（`FluentAssertionsExtras` 等）も起こさない。

## 受け入れ基準

- [x] `FluentAssertions` への参照が `.cs` / `.csproj` / `Directory.Packages.props` のいずれにも残っていない
- [x] `dotnet build` が 0 Warning / 0 Error
- [x] `dotnet test`（`Category!=Integration`）が**置換前と同一の合格数**で green
- [x] `dotnet format --verify-no-changes` が通る
- [x] 不採用ライブラリの再混入を CI が検知する（故意に混入させて失敗を確認した）
- [x] `CLAUDE.md` の記載が AwesomeAssertions に追随している

## テスト方針

本作業は振る舞いを変えない置換であるため、**既存テストの合格数の一致**が受け入れの中心である。
あわせて、新設した検査器自体のテストを `scripts/scripts.repo.test.js` に追加した（6 本）。

| 確認 | 方法 | 結果 |
| --- | --- | --- |
| 置換で意味が変わらない | 置換前後の合格数を比較 | **2250 → 2250**（Failed=0） |
| 混入を検知する | 一時ファイルに `using FluentAssertions;` を作る | exit 1 で検出・除去後に復帰 |
| 散文を誤検出しない | コメント中の言及を検査 | 検出 0 |
| 前方一致で巻き込まない | `FluentAssertionsExtras` を検査 | 検出 0 |

## 計画書との差異

- 差異: なし。platform ADR-0030 の棚卸しどおり AwesomeAssertions を採用した。
- **バージョンの整合は未確認**: ADR-0030 は「バージョンは Central Package Management で基盤リポと揃える」と
  定めるが、`microservices-platform` リポジトリは本セッションの参照範囲外のため、9.5.0 が基盤リポと
  一致するかを確認できていない。差異があれば追随する（未決事項 1）。

## 未決事項

1. **基盤リポとのバージョン整合** — `microservices-platform` の Central Package Management が指定する
   AwesomeAssertions の版と一致するかを確認する。相違があれば揃える（platform#455 の完了後）。
2. ~~**#343（PR #350）とのマージ順**~~ → **対応済み（2026-08-03）**。#343（PR #350）が先に develop へ
   マージされたため、本ブランチへ `git merge origin/develop` で取り込み（force push 禁止のため rebase は
   採らない）、新設された `AiStockTrading.PlanConformance.Tests` も AwesomeAssertions へ置換した
   （`.cs` 1・`.csproj` 1）。競合した `ci.yml`（`test-traceability` / `banned-libraries`）と
   `scripts/scripts.repo.test.js` は両側の追加を残して解消した。

## 変更履歴

| 日付 | 内容 |
| --- | --- |
| 2026-08-03 | 初版作成（#351） |
| 2026-08-03 | develop（#350 マージ済み）を取り込み、`AiStockTrading.PlanConformance.Tests` も置換（未決事項 2 を解消） |
