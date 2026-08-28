# トレーサビリティ規約（本リポジトリ固有）

`traceability.md`（キット配布物）を補う、**ai-stock-trading 固有**の取り決めを置く。
配布物は直接編集しない（同期のたびに手動マージが要るため）。同ディレクトリの `*.md` は自動適用される。

## 起点 ID の種別（固有）

裸の ID は**本リポジトリ（ai-stock-trading）の計画書**を指す。レンジは
`FR-01..21` / `UC-01..07` / `SC-01..03`（#532）、計画 ADR は `ADR-0001..0029`（欠番なし。
project-planning `projects/ai-stock-trading/07_adr/` の実ファイルと一致。trace ブロック規約
〔ADR-0029 決定4〕の値域検査が読む）。

- **この節は機械の単一情報源である。** `scripts/check-test-traceability.js` の `readPlanIds()` が
  本節の FR/UC/SC レンジ表記（バッククォート囲みの `FR-01..21` の形）を読み、`check-commit-messages.js` が
  コミット件名・PR タイトルの起点 ID の**実在性**を検査する。`scripts/check-trace-blocks.js` は
  `scripts/lib/plan-ranges.js`（`readPlanIds()` と同じ節を再利用する拡張点）経由で計画 ADR の
  レンジ（`` `ADR-0001..0029` `` の形）も読む。**節を消す・改名する・書式を崩すと
  検査器は例外で落ちる**（黙って 0 件検査へ落ちない fail-loud）。**資料再編（ADR-0029）で
  planning submodule への依存を撤去したため、レンジの更新は計画リポジトリ（GitHub URL または
  隣接クローンの読み取り専用）を直接確認して行う。以前あった「pin も直す」手順（走査基準の
  コミット SHA を記録する運用）は、pin 自体が撤去されたため適用しない。**
- **`SC-13` / `SC-16` は本リポの画面ではない。** 計画 `05_screens/01_screens.md` に現れるが、
  いずれも**基盤（microservices-platform）の画面を明示的に参照**する地の文である
  （例: 「基盤の SC-16（アカウント設定）へ遷移する」）。実在集合へ入れない。
- **`IADR` の実在性は `.ai-context/adr/` のファイルの有無で検査する**（本節の対象外）。
  **計画 `ADR` は上記レンジで検査する**（旧記述「該当ファイルの有無で検査する」は、trace ブロックの
  値域検査を新設したため計画 ADR には当てはまらなくなった。IADR は引き続きファイルの有無）。
- **`NFR` はレンジを持たない**（無採番を許す 2 場合は配布物 `traceability.md` が定める）。

## クロスリポジトリ参照の表記（確定・2026-08-15）

キット規約は「**プロジェクト内で短縮形とフルパス形式のどちらに寄せるかを最初に決め、混在させない**」と
定める。本リポジトリは **短縮形** を正とする（[#487](https://github.com/endazon/ai-stock-trading/issues/487)
利用者裁定・[IADR-0200](../../.ai-context/adr/IADR-0200_cross-repo-ref-notation.md)）。

| リポジトリ | 書式 | 例 |
| --- | --- | --- |
| `project-planning` | `planning#NNN` | `planning#329` |
| `microservices-platform` | `MSP#NNN` | `MSP#286` |
| **本リポジトリ** | **裸の `#NNN`** | `#487` |

- **詰めて書く。** `planning #329` / `planning PR #329` / `planning issue #329` はいずれも違反である
  （修飾語と番号が空白で離れると機械的突合に掛からない）。
- **列挙形でも各番号を修飾する。** `planning#319 / #323` は違反、`planning#319 / planning#323` が正しい。
- **フルパス形式（`endazon/project-planning#329`）は例外として許す。**
  `.md` で**自動リンクになるのはこの形だけ**であるため、リンクさせたい箇所では使ってよい。
- **意図的に誤例を書くときはインラインコードかコードフェンスに入れる**（`literal な引用は表記規約の対象外`）。

> 🔴 **「長い表記（`project-planning#NNN`）へ寄せる」は選べない。**
> 検査器 `check-cross-repo-refs.js` は設計上「短縮形へ寄せ、フルパス形式だけを例外として許す」であり、
> **短縮名にリポジトリ名そのものを与えても、自分自身への置換を提案して違反にし続ける**（実測）。
> 長い表記を採るには**キット配布物の改修を計画側へ環流する**必要がある。

### 検査の置換点（`check-cross-repo-refs.js` のファイル内で埋める。#530 / [IADR-0206](../../.ai-context/adr/IADR-0206_kit-pin-179a69a-substitution-points-in-file.md)）

```
CROSS_REPOS          = project-planning:planning, microservices-platform:MSP
SELF_NAMES           = AST, ai-stock-trading
EXCLUDE_PATHSPECS    = :!.ai-context/specs
KNOWN_OWNERS         = endazon
```

旧方式（env 注入でバイト一致を温存。IADR-0200 決定5）は、キット版 `scripts.test.js` が実データ本走を
env なしの素実行で行うようになり成立しなくなった。同名の環境変数（`CROSS_REPO_*`）による上書きは
引き続き有効で、`scripts.repo.test.js` のテストが同値を与えて検査する。

### 検査の置換点（`check-plan-id-qualification.js` のファイル内で埋める。NFR / [IADR-0262](../../.ai-context/adr/IADR-0262_plan-id-qualification-default-and-doc-contradictions.md)）

```
PROJECT_PREFIXES     = MSP, AST
```

**姉妹検査器（`check-cross-repo-refs.js`）と同じくファイル内へ直書きする。** [IADR-0189](../../.ai-context/adr/IADR-0189_plan-id-qualification-and-traceability-kit-sync.md)
決定2・決定6は「既定を空のまま保ち `PLAN_ID_PREFIXES` は CI の環境変数と回帰テストだけで供給する」
としていたが、これは**バイト一致をキットと保つため**という、資料再編（ADR-0029 決定6。kit との乖離を
受容する）より前の理由づけであった。**素の実行（env なし）だけが skip して緑になる非対称**が
IADR-0189 自身の残余リスクとして残っており、本 IADR-0262 で決定2・決定6 を部分的に supersede して
埋めた。`AST` を含むのは、本リポが `AST/FR-17` のような**自己修飾**を実際に使っているため
（IADR-0189 決定3の踏襲）。環境変数 `PLAN_ID_PREFIXES` による上書きは引き続き有効。

### 除外とその理由

| 除外 | 理由 |
| --- | --- |
| `.ai-context/specs/`（作業仕様書） | **point-in-time の記録**。後から表記だけ直すと**当時の記述と食い違う**（裁定 2026-08-15。姉妹検査器 `check-plan-id-qualification.js` と同じ既定） |

`planning`（submodule）・`feedback/`（環流記録）の除外は、資料再編（計画 ADR-0029・2026-08-21）で
両ディレクトリ自体が撤去されたため削除した。除外を維持する対象が存在しない。

**`CHANGELOG.md` は除外しない。** 生成物であるため、コミット件名は書き換えず
`scripts/changelog-overrides.json` の `remap` で**生成物の側を是正する**。

### 外した除外（黙って消さない）

| 除外 | いつ | なぜ外せたか |
| --- | --- | --- |
| `.claude/rules/traceability.md` | 2026-08-15（[#517](https://github.com/endazon/ai-stock-trading/issues/517) / [IADR-0202](../../.ai-context/adr/IADR-0202_traceability-md-classification.md)） | キット配布物の違反 1 件（`planning issue #202`）を計画側へ環流し（planning#349）、**キット側が是正された**。本リポも追随したため対象へ戻した。同ファイルは**分類 A（バイト一致）へ移した**ので、今後キットが違反を持ち込めば検査が赤くなる |

> 🔴 **暫定の除外は、外す条件と一緒に書く。** この除外は「planning#349 が是正されたら外す」と
> [IADR-0200](../../.ai-context/adr/IADR-0200_cross-repo-ref-notation.md) の残余リスクに書いてあったから外せた。
> **条件を書かない除外は、恒久化する。**
