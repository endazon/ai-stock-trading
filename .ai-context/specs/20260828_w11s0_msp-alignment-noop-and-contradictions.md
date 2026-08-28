---
title: W11 段0 — MSP 整合監査で見つかった no-op と矛盾の是正
type: spec
status: approved
related_ids: [NFR, IADR-0189, IADR-0203, IADR-0262]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
---

# 仕様書: W11 段0 — MSP との整合監査で見つかった no-op と矛盾の是正

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 起点: 利用者指示「msp 側の実装と作りが合っているかどうか確認し是正する」による事前監査（親セッション）。
- 起点 ID: **NFR**（無採番。規約整備・検査器の整備＝メタ作業。`.claude/rules/traceability.md`「起点 ID の種別」許容ケース2）
- 裁定範囲: **no-op と矛盾の是正のみ**。MSP の検査器一式の移植は行わない（利用者裁定）。
- 参照した基盤リポ: `/home/user/microservices-platform`（読み取り専用・実測 2026-08-28）
- 参照した計画リポ: `/home/user/project-planning`（読み取り専用）
- 作業ブランチ: `claude/ast-implementation-issues-rzkoxb-w11s0`（`origin/develop` = `82b95ec` 起点）

## 対象4件（親セッションが実測済み）と実装方針

### (1) `scripts/check-plan-id-qualification.js` の `PROJECT_PREFIXES` 既定が空 → 何も検査していない

**追加実測（着手前に自分で確認した）**: 単純な「MSP と対称に `['MSP']` を埋める」では不十分と判断した。理由:

- `.ai-context/adr/IADR-0189_plan-id-qualification-and-traceability-kit-sync.md` 決定2・決定6が
  **既定を空のまま保ち、`PLAN_ID_PREFIXES` は CI 側の環境変数（`MSP,AST`）と `scripts.repo.test.js` の
  明示的な回帰テストで供給する**、という設計を **Accepted** で確定している。**バイト一致をキットと保つ
  ため**という理由づけである。
- しかし **この理由づけは資料再編（計画 ADR-0029・2026-08-21）より前**（IADR-0189 は 2026-08-14）に
  書かれている。ADR-0029 決定6 は「**キットとの乖離は受容する**」と明示しており、**バイト一致を保つ
  動機そのものが、その後の方針転換で失われている**。MSP 側は既に同じ理由で `PROJECT_PREFIXES` を
  自身の値（`['AST']`）で**ファイルへ直接埋めている**（`microservices-platform/scripts/check-plan-id-qualification.js:55`）。
- **CI（`ci.yml`）は既に `PLAN_ID_PREFIXES: "MSP,AST"` を明示して渡しており、CI 自体は no-op ではない。**
  no-op なのは**素の実行**（`node scripts/check-plan-id-qualification.js`。本仕様書の検証コマンド・
  pre-commit 相当の手元実行・`/verify` 相当が該当）である。IADR-0189 決定6 の残余リスク節が
  「`PLAN_ID_PREFIXES` の fail-open は残る」と自認しているとおり、**既知だが未解消のまま残っていた
  設計上の穴**である。
- **値は `['MSP']` ではなく `['MSP', 'AST']` を採用した。** 本リポは `AST/FR-17` のような**自己修飾**を
  実際に使っており（IADR-0189 決定3・24件以上）、CI が渡す実運用値も `MSP,AST` である。`['MSP']` だけに
  すると、素実行時に自己修飾の空白区切り誤りを検出できないという**別の縮退**を残すことになる。

**是正内容**:
- `PROJECT_PREFIXES` の既定値を `['MSP', 'AST']` へ変更（環境変数 `PLAN_ID_PREFIXES` は引き続き上書き可）。
- `.claude/rules/traceability.repo.md` に `check-cross-repo-refs.js` と同じ形式で
  `check-plan-id-qualification.js` の置換点表を新設し、値と根拠（IADR-0262 を参照）を記録する。
- **IADR-0189 の決定2・決定6 を IADR-0262 で部分的に supersede する**。IADR-0189 本体は書き換えず、
  日付つき追記ブロック（`traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」に
  倣う）を追加する。`.ai-context/adr/` は「凍結記録」だが同節が「live な権威文書」として追記ブロックを
  許容する対象であることを確認した（specs / superpowers のみが追記ブロックも含めて禁止）。

#### 走査件数の下限検査（0 件走査で fail-loud）の設計

**既に部分的に実装されていた。** `main()` に「`trackedFiles()` が `[]` を返したら fail-closed（exit 1）」
という分岐が既存（`files.length === 0` の分岐、`checker` が非 null の場合のみ到達）。これは
「対象なし（skip・exit 0）」と「拾えなかった（fail・exit 1）」を区別する設計として妥当であり、
**新規に作る必要はなかった**。

ただし、**この分岐は自己試験（`--self-test`）でカバーされていなかった**（`main()` のインライン
ロジックであり、CLI 経由でしか通らないため）。**設計として妥当かどうかを固定する**ため、判定ロジックを
純関数 `isEmptyScanFailure(checker, files)` へ切り出し、`main()` から呼ぶ形にリファクタリングし、
`--self-test` へ次の4ケースを追加した。

| ケース | 期待 |
| --- | --- |
| checker あり・`files = []` | fail-closed と判定する（true） |
| checker あり・`files = ['a.md']` | 判定しない（false） |
| checker なし（skip 分岐が先に処理する前提） | 判定しない（false） |
| `files = null`（git 失敗・fail-open 分岐が別にある） | 判定しない（false） |

**「非空なのに 0 件」という単純な下限とは別に設計した理由**: `PROJECT_PREFIXES` を意図的に空へ戻す
（他プロジェクトを参照しないフォークにする等）ケースが**設計上の正常系**として存在するため、
「非空の checker が存在する場合に限り」0 件走査を fail とする、という条件を厳密に保った。

### (2) `.claude/rules/traceability.md` の companion 段落が自リポ CLAUDE.md と矛盾

MSP 側の是正済み文面（`microservices-platform/.claude/rules/traceability.md:14-16`）へ揃えた。
ADR 番号は AST の対応する計画 ADR（`ADR-0029`。資料再編）へ読み替え、日付は AST の `CLAUDE.md` /
`traceability.repo.md` に既出の資料再編日付（**2026-08-21**）に合わせた（両ファイルとも
「資料再編（計画 ADR-0029・2026-08-21）」の形で既に書いている）。

差分は 22 バイトの減（15,977 → 15,955）。必読規約の総量予算（51,200 バイト）への影響は**減る方向**。

**「配布物・直接編集しない」との整合**: `traceability.md` 冒頭は「本ファイルはキットの配布物である。
直接編集しないこと」と自称するが、以下の理由で編集が正当である旨を IADR-0262 に明記した。

1. **バイト一致検査（`check-kit-sync.js` 相当）は ADR-0029 決定6 で既に退役している。** 「直接編集すると
   バイト一致が崩れ、同期のたびに手動マージが要る」という当の懸念が、依拠する検査ごと存在しない。
2. **MSP 自身が既に同じファイルへ自リポの ADR 番号（`ADR-0048`）を書き込んで編集している。** 配布物を
   一切編集しないという原則は、少なくとも companion 段落の日付つき変更注記に関しては既に破られている
   （kit の一次配布元である project-planning 側がどう扱っているかは未確認だが、少なくとも MSP は kit の
   コピーを独自に編集済みである）。
3. **ADR-0029 決定6 は「kit との乖離を受容する」と明示し、追随義務も同期義務も無いとしている。** 直接
   編集を避ける動機（将来の同期を楽にする）自体が、この方針の下では成立しない。

### (3) `AGENTS.md` の2点

- **`AGENTS.md:42`**: 「新 ADR 等」→「新 IADR 等」（MSP `AGENTS.md:42` に合わせた）。
- **`AGENTS.md:9`**: 「起票前に同件の既存 issue を必ず検索する」を追加し、ラベルへ `feedback` を足した
  （`decision-needed` のみ → `feedback` / `decision-needed`）。MSP `AGENTS.md:9` に合わせた。
- **`CLAUDE.md:105`**（禁止事項）: 「新 ADR」→「新 IADR」（MSP `CLAUDE.md:95` に合わせた）。

**母集合の再確認（規則1: 誤りの文字列 `新 ADR` で全文走査）**: 12 件ヒット。上記2ファイル3箇所以外の
10件は、次の理由で**すべて正しい用法**であり是正不要と判断した（全件を個別に確認した）。

| ファイル | 用法 | 判断 |
| --- | --- | --- |
| `backend/Tests/.../McpExposureNotDeclaredTests.cs:76` | 「ADR-0012 を Supersede する新 ADR」 | ADR-0012 は**計画 ADR**（MCP 公開可否は製品アーキテクチャ決定）。正しい |
| `backend/Shared/.../TradeExpenseCategoryTests.cs:46` | 「ADR-0016 決定15/FR-11...新 ADR か planning への環流」 | 経費区分数は**計画側の業務ルール**。正しい |
| `backend/Shared/.../EventBackwardCompatibilityTests.cs:52` | 「破壊的変更が必要なら新 ADR」 | platform `10_composability-design` という**計画側**の設計文書を根拠にした後方互換規約。正しい |
| `docs/security/security.md:68,71` | 「将来公開する場合...新 ADR が必須」 | 同じく ADR-0012（計画ADR）の Supersede 手続き。正しい |
| `.claude/agents/plan-feedbacker.md:23,31` | 環流先候補「新 ADR」 | この agent は**計画リポジトリへの環流**が仕事。正しい（MSP 版も同一文言） |
| `.claude/agents/adr-guardian.md:28` | 「新 ADR の起票が必要」 | 計画 ADR 制約の逸脱時の提案。文脈上、計画 ADR を指す。正しい（MSP 版と同一） |
| `.claude/rules/traceability.md:77` | 「逸脱が必要なら新 ADR の起票を提案する」 | **kit 配布物・MSP と完全にバイト一致**（両リポとも同文言）。キット側の一般文言であり本リポ固有の誤りではない。是正対象外 |
| `.claude/commands/adr-check.md:15` | 「新 ADR の起票が必要」 | 計画 ADR 制約チェックコマンドの文脈。正しい（MSP 版と同一） |
| `.claude/commands/plan-feedback.md:34` | 反映先候補「新 ADR」 | 計画リポジトリへの環流コマンド。正しい（MSP 版と同一） |

→ **新規の是正は 0 件**（列挙の3箇所のみ）。

### (4) `CLAUDE.md` の共有プロジェクト列挙漏れ

**着手前に現況を再確認したところ、`CLAUDE.md:121` は既に是正済みだった。** 現在の記述は
「共有物は `backend/Shared/AiStockTrading.Shared.*`」と**ワイルドカード表記**になっており、
`Contracts` / `Infrastructure` / `KnowledgeBase`（および将来追加される可能性のある `Kernel` 等）を
**列挙せず包含する**。親が指摘した文字列（`{Contracts,Infrastructure}`）は現在の `CLAUDE.md` には
存在しない。**この点は是正不要（既に解消済み）と判断し、CLAUDE.md は変更していない。**

**ただし同じ矛盾パターンを別ファイルに発見した（母集合の再確認・規則1）**: `backend/TestSupport/README.md:4`
が旧来の**非ワイルドカード列挙**（`AiStockTrading.Shared.{Contracts,Infrastructure}`）を残しており、
KnowledgeBase が漏れているだけでなく、**パスも `src/Shared` という現存しないディレクトリ**を指していた
（実際は `backend/Shared`。本リポに `src/` ディレクトリは存在しない）。CLAUDE.md が既に採用した
ワイルドカード表記に揃えて是正した。`AiStockTrading.Shared.Kernel` を名指しで足すことはしていない
（ワイルドカードなので不要であり、まだ develop に存在しないため名指しも避けた）。

## 母集合の再確認（実施した軸と結果）

`.claude/rules/traceability.md`「是正・追随の母集合の取り方」規則1〜8・`traceability.repo.md` 規則9・10
に従い、以下の軸で全リポジトリを走査した（`--include` で拡張子を絞らず、母集合は `.claude/` `docs/`
`.ai-context/` `scripts/` `.github/` `backend/` すべてを対象。ただし `.ai-context/specs/` と
`.ai-context/superpowers/` は point-in-time の凍結記録のため**除外**し、以下「除外したもの」に理由を書く）。

| # | 軸（検索語） | 目的 | ヒット | 新規に是正したもの |
| --- | --- | --- | --- | --- |
| 1 | `新 ADR` / `新ADR` | (3) の追加確認 | 12（frozen `.ai-context/adr` 除く実質12） | 0（既知の3箇所のみ。他9は正当な計画ADR参照） |
| 2 | `PLAN_ID_PREFIXES` / `PROJECT_PREFIXES` | (1) の波及確認（既定値変更で古くなる記述） | 5（非 frozen） | `scripts/scripts.repo.test.js` のコメント2行・`.github/workflows/ci.yml` のコメント1箇所 |
| 3 | `Shared.{Contracts,Infrastructure}` / `AiStockTrading.Shared.` 列挙 | (4) の追加確認 | 2（`CLAUDE.md` は既に解消済み・`backend/TestSupport/README.md`） | `backend/TestSupport/README.md` |
| 4 | `バイト一致が崩れ` / `バイト一致を保` | (2) の波及確認（他にも同じ矛盾が無いか） | 2（`scripts/README.md`・`traceability.md`） | 0（`scripts/README.md` は既に ADR-0029 決定6 後の文面へ更新済みで矛盾なし。`traceability.md` は (2) で対応済み） |
| 5 | `src/Services` / `src/Shared` | 軸3で見つけたパス誤りの横展開 | 1（`backend/TestSupport/README.md`。軸3と同一） | 同上（重複） |

**合計で親の4件以外に新規是正したもの: 2件**（`backend/TestSupport/README.md` のパス・列挙誤り、
`scripts/scripts.repo.test.js` と `ci.yml` の「PLAN_ID_PREFIXES を落とすと skip して緑になる」という
記述——既定値変更により**もはや事実と異なる**ため）。

### 除外したものとその理由

| 除外 | 理由 |
| --- | --- |
| `.ai-context/specs/*.md`（`20260814_477_...` 等、PROJECT_PREFIXES・バイト一致に言及する複数ファイル） | **point-in-time の記録**。当時の実測・判断をそのまま残す。表記を今の状態に合わせて書き換えると当時の記述と食い違う（`traceability.repo.md`「除外とその理由」と同じ規律） |
| `.ai-context/adr/IADR-0189_*.md`・`IADR-0203_*.md` の本文プロズ | 凍結記録。決定を覆す場合は本文を書き換えず新 IADR（本件は IADR-0262）を作り、`traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」に従って**日付つき追記ブロックのみ**を IADR-0189 へ足す。IADR-0203 は他リポジトリの分類監査（#521）の一時点のスナップショットであり、IADR-0262 側から参照するに留め、IADR-0203 自体は変更しない |
| `.ai-context/adr/README.md` の IADR-0189 索引行 | `check-adr-index-sync.js` の要求どおり、IADR-0189 本文へ追記ブロックを足した差分に合わせて索引行も最小限更新した（是正ではなく同期） |
| `docs/traceability-appendix.md:79`（「空のままなら skip する」） | **本リポ固有の状態を主張していない**（`PROJECT_PREFIXES` を配布時に書き換える、という kit 一般の仕組みの説明。本リポが空かどうかを断定していない）。矛盾ではないため是正不要 |
| `.claude/rules/traceability.md:77`「新 ADR の起票を提案する」 | kit 配布物・MSP と完全にバイト一致（軸1で確認済み）。本リポ固有の誤りではなく、キット側への環流の要否は別issueの射程 |

## 実装した変更一覧

1. `scripts/check-plan-id-qualification.js`: `PROJECT_PREFIXES` 既定を `['MSP', 'AST']` へ、
   `isEmptyScanFailure()` を新設し `main()` から呼ぶ形へリファクタリング、`--self-test` に4ケース追加。
2. `.claude/rules/traceability.repo.md`: `check-plan-id-qualification.js` の置換点表を新設。
3. `.claude/rules/traceability.md`: companion 段落を MSP の是正済み文面へ揃える（ADR-0029・2026-08-21）。
4. `AGENTS.md`: L9 に既存issue検索の一文＋`feedback`ラベルを追加、L42 の「新 ADR」→「新 IADR」。
5. `CLAUDE.md`: L105 の「新 ADR」→「新 IADR」（L121 は既に是正済みのため変更なし）。
6. `backend/TestSupport/README.md`: パスとプロジェクト列挙をワイルドカード表記へ是正。
7. `scripts/scripts.repo.test.js`: PROJECT_PREFIXES 既定変更に伴い古くなったコメントを是正し、
   「既定を空へ戻す退行」を検出する回帰テストを1件追加。
8. `.github/workflows/ci.yml`: 同上のコメント是正。
9. `.ai-context/adr/IADR-0262_plan-id-qualification-default-and-doc-contradictions.md`: 新規 IADR。
10. `.ai-context/adr/IADR-0189_*.md`: 日付つき追記ブロックを追加（決定2・決定6の一部を IADR-0262 が
    supersede する旨）。
11. `.ai-context/adr/README.md`: IADR-0262 の索引行を追加、IADR-0189 索引行を追記に合わせて最小更新。

## 受け入れ基準

- [ ] `node scripts/check-plan-id-qualification.js` が skip ではなく実件数を報告し、違反 0 件で終了する
- [ ] `node scripts/check-plan-id-qualification.js --self-test` が通る（既存 + 新規4ケース）
- [ ] `node scripts/check-cross-repo-refs.js` が通る
- [ ] `node scripts/check-commit-messages.js` が通る
- [ ] `node scripts/check-trace-blocks.js` が通る
- [ ] `node scripts/check-adr-index-sync.js` が通る（IADR-0262 新設・IADR-0189 追記の両方に索引行が追随）
- [ ] `node scripts/check-doc-links.js` が通る
- [ ] `node scripts/check-reading-budget.js` が通る（予算内。`traceability.md` は −22B、他は僅かな増）
- [ ] `node scripts/gen-knowledge-graph.js --check` が通る
- [ ] `node --test scripts/scripts.test.js scripts/scripts.repo.test.js` が通る
- [ ] `dotnet build backend/backend.slnx` が通る（バックエンド変更は無いはずだが確認する）
- [ ] `dotnet format backend/backend.slnx --verify-no-changes` が通る

## テスト方針

- `check-plan-id-qualification.js` の既定値変更・下限検査の設計は `--self-test` の新規4ケースで固定する。
- 「既定を空へ戻す退行」（IADR-0189 決定6 が警告した fail-open の再発）を検出する回帰テストを
  `scripts.repo.test.js` に1件追加する（環境変数を明示的に落として素実行し、skip にならず実件数を
  報告することを確認する）。
