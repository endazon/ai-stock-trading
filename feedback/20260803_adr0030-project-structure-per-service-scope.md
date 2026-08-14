---
title: 7 標準プロジェクト構成は「常設」か「実体があるものだけ」か／サービス公開クライアント（`*.Client`）の置き場が無い — ADR-0030・12_backend-application-stack への確認
type: plan-feedback
status: accepted
category: その他
related_ids:
  - NFR
  - ADR-0030 # platform（microservices-platform）側の計画 ADR
  - ADR-0019 # 同上（ユニット第一構成）
  - IADR-0128 # 本リポの実装 ADR（本フィードバックの発生源）
source_repo: ai-stock-trading
source_ref: "refactor/NFR-standard-project-layout / #353 / docs/specs/20260803_353_standard-project-layout.md §12 未決事項 3・6 / docs/adr/IADR-0128_standard-project-layout.md"
author: endazon (with Claude Code)
created: 2026-08-03
dispatched: true
planning_issue: 180
---

# フィードバック: 7 標準プロジェクト構成の適用範囲と、サービス公開クライアントの置き場

> **送付済み（2026-08-04）。** 計画リポジトリへ `plan-feedback` ラベル付き Issue として起票した:
> [endazon/project-planning#180](https://github.com/endazon/project-planning/issues/180)。
> 宛先は計画リポジトリ `project-planning` であり、裁定の対象は同リポ内の
> `projects/microservices-platform/`（基盤側）の文書である。
> 以降のトリアージ・裁定は当該 Issue で行う。本書は実装リポジトリ側の控えである。

## 種別

その他（**計画書の記述の解釈が実装側で一意に定まらない**＝規範性と粒度の明文化要求）。
「要求の誤り」ではない。ADR-0030 の決定内容そのものに反対しているわけではなく、
**「7 標準をサービス単位でどう適用するか」が文面から一意に読めない**ことを報告する。

## 起点となる計画書

- 機能要求（FR）: — （非機能。プロジェクト構成の標準追随）
- ユースケース（UC）: —
- 画面（SC）: —
- 関連 ADR: **platform ADR-0030**（バックエンドアプリケーション層のライブラリ標準・Accepted）、
  platform ADR-0019（ユニット第一構成）、platform ADR-0029（gRPC/REST 使い分け基準）
- 計画書リンク:
  - `projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md`
  - `projects/microservices-platform/06_technical/12_backend-application-stack.md`（fixed・§プロジェクト構成）
  - `projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md`

## 現状（計画書の記述 / As-Is）

`12_backend-application-stack.md` §プロジェクト構成（サービス単位）は次を定める。

```text
src/
 ├── Api             # エンドポイント定義・DI 構成・ProblemDetails 変換
 ├── Application     # ユースケース（Wolverine ハンドラ）・検証・マッピング
 ├── Domain          # エンティティ・値オブジェクト（外部依存なし）
 ├── Infrastructure  # EF Core・Redis・オブジェクトストレージ等の実装
 ├── Contracts       # 公開契約（proto・イベント・DTO）
 ├── SharedKernel    # Result / Error・共通基底（過度な共通化は避ける）
 └── Tests           # Unit / Integration
```

ADR-0030 §決定 の選定基準 3 は「Domain は外部ライブラリ依存ゼロ、**SharedKernel は自前実装（Result 型）を置き**、
フレームワーク依存（Wolverine）は Application 層のハンドラまでに留める」と述べる。

一方 ADR-0019 決定 4 は「ユニット固有のイベント契約は**ユニット側の契約プロジェクト**に置く」と定める。

上記のいずれにも、次の 3 点は書かれていない。

1. 7 つを**サービスごとに常に作る**のか、**実体があるものだけ作る**のか（規範性）。
2. `Contracts` の粒度が**サービス単位**なのか**ユニット単位**なのか（ADR-0019 決定 4 との関係）。
3. 「あるサービスが**他サービスへ公開する**クライアントライブラリ」（HTTP クライアント＋キャッシュ＋
   fail-safe＋DI 拡張＋イベント consumer の一式）を 7 標準のどこへ置くのか。

## 問題点 / あるべき姿（To-Be）

**問題点**: 3 点とも実装側の裁量に落ちるため、実装リポジトリごとに違う構成が「標準に揃った」と主張できてしまう。
とくに 1 は「空の `SharedKernel` / `Contracts` を全サービスに並べる」形でも文面上は満たせるが、それは
ADR-0030 自身の但し書き（「過度な共通化は避ける」）・選定基準 2（標準機能優先＝構成要素を増やさない）と
逆方向であり、**構成は標準に見えるのに規律は無い**状態を作る。

**あるべき姿**: `12_backend-application-stack.md` §プロジェクト構成に、(a) 7 標準の規範性、
(b) `Contracts` の粒度、(c) サービス公開クライアントの位置づけ、の 3 点を一文ずつ明記する。

## 実装で判明した経緯

- ai-stock-trading（AST）は基盤の可変機能ユニットであり、AST [IADR-0001](../docs/adr/IADR-0001_repo-structure-and-stack.md) が
  「リポ構成・規約は基盤実装リポに揃える」と定めている。
- [#353](https://github.com/endazon/ai-stock-trading/issues/353)（全 11 サービスを標準構成へ再配置）で、旧構成
  `Domain / Application / Worker` を 7 標準へ写す作業を行った（[IADR-0128](../docs/adr/IADR-0128_standard-project-layout.md)・
  作業仕様書 `docs/specs/20260803_353_standard-project-layout.md`）。
- その際、上記 3 点が文面から一意に決まらなかったため、AST 側で次の実装判断を行った（IADR-0128 決定 2・5）。
  **これは計画の解釈であって、計画への反対ではない。**

| 論点 | AST の暫定判断 | 根拠 |
| --- | --- | --- |
| 7 標準の規範性 | **実体があるものだけ作る**。結果 1 サービス = `Api` / `Application` / `Domain`（実体のある 9 サービスのみ）/ `Infrastructure` の最大 4 | ADR-0030 の但し書き「過度な共通化は避ける」・選定基準 2・「Result = SharedKernel の自前実装」という定義（＝SharedKernel は Result の置き場） |
| `SharedKernel` | **作らない**（Result / Error 型が未導入のため実体が無い。導入は独立 issue） | 同上 |
| `Contracts` | **ユニット単位で 1 つ**（`backend/Shared/AiStockTrading.Shared.Contracts`）。サービス個別には作らない | ADR-0019 決定 4。サービス間で共有されるイベント契約（`OrderApproved` は発注執行とリスク統制の双方が使う）は per-service `Contracts` では置き場を失う |
| `*.Client` | **標準外の第 8 のプロジェクトとして残す**（`ConfigurationService.Client`） | `Contracts`（型の置き場）にも `Infrastructure`（サービス内部の技術詳細）にも収まらない。畳むと消費側が同じキャッシュ・無効化・fail-safe を各々書き写す |

- 実装結果: 全 11 サービスが `Api/Application/Domain/Infrastructure` へ再配置され、プロジェクト数は 76 → 99。
  `Domain` の外部依存ゼロは csproj の静的解析（`backend/Tests/AiStockTrading.Architecture.Tests`）で機械的に強制した。
- **未確認事項（本フィードバックが必要な理由）**: 基盤実装リポ（`microservices-platform` 本体）の実サービスが
  per-service の `Contracts` / `SharedKernel` を持っているかを、AST 側セッションでは確認できていない。
  IADR-0001 が「揃える先は基盤実装リポ」と定めている以上、基盤の実構成が上表と食い違うなら AST 側の判断を見直す。

## 提案（計画への反映案）

- 反映先候補: **`12_backend-application-stack.md` §プロジェクト構成の追記**（主）／必要なら ADR-0030 の
  §決定 選定基準 3 への 1 文追記（従）。新 ADR までは要さないと考える（決定の変更ではなく明文化のため）。
- 提案内容:

**(1) 規範性の明記**（いずれかを選ぶ。AST は案 A を暫定採用済み）

| 案 | 文案 | 含意 |
| --- | --- | --- |
| **A（AST 暫定採用）** | 「上記 7 つは**上限（許容される構成要素の一覧）**であり、**実体のあるものだけを作る**。空のプロジェクトは作らない」 | 空プロジェクトの乱造を防ぐ。「標準に揃っている」の判定に実装側の説明が要る |
| B | 「7 つは**すべてのサービスに常設**する」 | 見た目の一様性は最大。空の `SharedKernel` / `Contracts` が全サービス分増える |

**(2) `Contracts` の粒度と ADR-0019 決定 4 との関係の明記**

> 「`Contracts` は**サービスが単独で公開する契約**（自サービスの proto・DTO）を置く。**複数サービスが共有する
> ユニット固有のイベント契約は、ADR-0019 決定 4 に従いユニット単位の契約プロジェクトへ置く**（両者は排他ではない）」

—— のように、per-service と per-unit の使い分けを一文で示していただきたい。現状は
「§プロジェクト構成に `Contracts` がある」ことと「ADR-0019 決定 4 がユニット側に置くと言う」ことが
**同じ対象について別の場所を指しているように読める**。

**(3) サービス公開クライアント（`*.Client`）の位置づけの明記**（3 案を提示。AST は案 ii を暫定採用）

| 案 | 内容 | 懸念 |
| --- | --- | --- |
| i | `Contracts` に含める | `Contracts`（型の置き場）に HTTP クライアント・キャッシュ・DI 拡張・consumer が入り、定義が壊れる |
| **ii（AST 暫定採用）** | **`Client` を第 8 の標準プロジェクトとして追加**（「他サービスへ公開する同期呼び出しクライアント。キャッシュ・fail-safe・DI 拡張を含む」） | 標準の構成要素が 1 つ増える |
| iii | `*.Client` を作らず、ADR-0029 の基準に従って **gRPC 生成クライアント**へ寄せる | 現行の REST 同期呼び出し（AST `ConfigurationService.Client`）の書き換えが要る。ADR-0027（Wolverine）移行と併せた検討になる |

## 影響範囲

- **microservices-platform 側**: `12_backend-application-stack.md`（fixed）の §プロジェクト構成への追記。
  案 B を採る場合は、基盤・knowledge・AST の全ユニットで空プロジェクトの新設作業が発生する。
  案 ii を採る場合は 7 標準が 8 になる（`06_technical` の図と ADR-0030 §決定 の記述の追随）。
- **ai-stock-trading 側**: 案 A・案 ii のままなら追加作業なし（[IADR-0128](../docs/adr/IADR-0128_standard-project-layout.md) が
  そのまま有効）。案 B を採る場合は 11 サービス分の空 `SharedKernel` / `Contracts` の新設と `backend.slnx` 追随が要る。
  案 iii を採る場合は `ConfigurationService.Client` の消費側（費用統制・リスク統制・取引判断ほか）を巻き込む改修になる。
- **関連する後続 issue**: `Result` / `Error` 型（SharedKernel）の導入は独立 issue の予定であり、本件 (1) の裁定が
  その要否・置き場に直結する。`*.Client` の扱いは [#354](https://github.com/endazon/ai-stock-trading/issues/354)
  （MassTransit → Wolverine 移行）でのサービス間同期呼び出しの形と併せて確定するのが自然である。
- **判定基準への影響**: 本件が未裁定のあいだ、「標準構成に揃っている」の判定は AST [IADR-0128](../docs/adr/IADR-0128_standard-project-layout.md)
  決定 2 を併せて読む必要がある（7 のうち 2 つが存在しない状態が正である、という但し書き付きの適合）。
