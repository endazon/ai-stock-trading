# トレーサビリティ規約（本リポジトリ固有）

`traceability.md`（キット配布物）を補う、**ai-stock-trading 固有**の取り決めを置く。
配布物は直接編集しない（同期のたびに手動マージが要るため）。同ディレクトリの `*.md` は自動適用される。

## クロスリポジトリ参照の表記（確定・2026-08-15）

キット規約は「**プロジェクト内で短縮形とフルパス形式のどちらに寄せるかを最初に決め、混在させない**」と
定める。本リポジトリは **短縮形** を正とする（[#487](https://github.com/endazon/ai-stock-trading/issues/487)
利用者裁定・[IADR-0200](../../docs/adr/IADR-0200_cross-repo-ref-notation.md)）。

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

### 検査の置換点（`scripts/scripts.repo.test.js` が与える）

```
CROSS_REPO_NAMES=project-planning:planning,microservices-platform:MSP
CROSS_REPO_SELF_NAMES=AST,ai-stock-trading
CROSS_REPO_EXCLUDES=:!planning,:!docs/specs,:!feedback,:!.claude/rules/traceability.md
```

### 除外とその理由

| 除外 | 理由 |
| --- | --- |
| `planning`（submodule） | 別リポジトリの実体であり、本リポジトリの成果物ではない |
| `docs/specs/`（作業仕様書） | **point-in-time の記録**。後から表記だけ直すと**当時の記述と食い違う**（裁定 2026-08-15。姉妹検査器 `check-plan-id-qualification.js` と同じ既定） |
| `feedback/`（環流記録） | 同上。**送付・環流した時点の記録**である |
| `.claude/rules/traceability.md` | 🔴 **キット配布物（分類 A）であり手元で直さない。** 違反 1 件は計画側へ環流した（planning#349） |

**`CHANGELOG.md` は除外しない。** 生成物であるため、コミット件名は書き換えず
`scripts/changelog-overrides.json` の `remap` で**生成物の側を是正する**。
