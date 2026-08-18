# AI オーケストレーション — 役割スロットとエンジン差し替え

**本書は役割スロット（orchestrator / worker / reviewer）の正本である。** どの生成 AI（エンジン）を
どの役割に挿すか、差し替え時に何を守るか、失敗時にどう切り戻すかを定める。

> **本ファイルはキットの配布物である（分類 A・配布先固有の差分を持たない）。直接編集しないこと。**
> 毎セッション必読ではない（総量予算の母集合に入れない）。**スロットの構成を変更するとき・
> エンジンを追加するとき・フォールバックを設計するときに読む。**

## 1. 考え方 — プロファイルとロースターの分離

| 宣言 | ファイル | 意味 |
| --- | --- | --- |
| **プロファイル**（能力） | `AI_SETUP.md` §1 → `.ai-profile` | **何が使えるか**（ライセンス・シークレットの有無） |
| **ロースター**（配役） | `ai-roster.json` | **どの役割にどのエンジンを挿すか**と、失敗時のフォールバック連鎖 |

両者は併存する。`apply-profile.sh` はプロファイルを適用するだけで、ロースターを書き換えない。
使えないエンジン（プロファイルに無いもの）をロースターへ書いても動かない。

🔴 **ロースターは宣言であって実装ではない。** 各割当の `implemented_by` が**現在の実現手段**であり、
実体（アダプタ・ワークフロー）の無い割当は**無効**である。宣言だけ書いて「差し替えた」としないこと。

## 2. 役割スロットの定義

### orchestrator（司令塔）

- **責務**: フェーズ計画・issue 棚卸し・worker への委譲（worktree の割当）・FIFO マージ操作・
  監査の起動・**フォールバックの判断**。計画リポ `docs/ai-implementation-workflow-guide.md` §2〜3 の骨格の執行者。
- **既定**: Claude Code の対話セッション（`.claude/commands/` 群がインターフェース）。
- **差し替え時に守る契約**:
  1. **人間の 3 点チェックポイント（同ガイド §7）を代行しない**（フェーズ計画の承認・監査のサンプリング確認・裁定）。
  2. **マージ可否を判定しない**。可否は required checks（決定的ゲート）が決め、orchestrator は操作だけを行う。
  3. **並列の骨格を変えない**（ファイル領域の非重複判定・worktree 隔離・FIFO マージ・同時 4〜5 並列）。
  4. 委譲・フォールバック・マージの各操作を台帳（issue ラベル運用）に記録する。

### worker（作業）

- **責務**: issue を受けて、計画書を読む → 作業仕様書 → 実装 → テスト → PR 作成。1 issue の実装 diff は 400 行以内。
- **既定**: Claude。
  - ローカル並列: サブエージェント `spec-implementer`（`.claude/agents/`）
  - CLI 起動: `scripts/ai-adapters/run-worker-claude.sh`（`claude -p`）
  - GitHub 起動: `.github/workflows/claude-coding.yml`（`@claude` メンション）
- **差し替え候補**: Codex CLI（アダプタ同梱）、Cursor / Aider 等の `AGENTS.md` 対応ツール。
  GitHub Copilot coding agent は既存プロファイル `copilot` のまま独立であり、スロット体系に取り込まない。
- **差し替え時に守る契約**（エンジンによらず不変）:
  1. **作業仕様書（`docs/specs/`）なしで実装へ着手しない。**
  2. **トレーサビリティ規約に従う**（起点 ID をブランチ・コミット・コード・PR に残す。`.claude/rules/traceability.md`。
     非 Claude エンジンは同規約を自動で読まないため、**アダプタがプロンプトで要点を渡す**）。
  3. **帰属を機械可読に残す**: コミットの `Co-Authored-By:` トレーラにエンジン・モデルを記録し、
     PR に `agent:<engine>` / `model:<model>` ラベルを付ける（同ガイド §4）。
  4. 中間成果物（質問票・作業指示書）をコミットしない。force push しない。割当 worktree の外に書かない。
  5. 🔴 **`.claude/hooks/`（guard-bash / guard-secrets / check-impl）と `.claude/settings.json` の
     権限制御は Claude Code 専用であり、非 Claude worker には効かない。** 同等の安全策
     （破壊的操作の抑止・秘密情報の遮断）は**アダプタ側の責務**である（各エンジンのサンドボックス
     モード指定等。`scripts/ai-adapters/README.md`）。

### reviewer（検証実走レビュー）

- **責務**: **検証実走付き** AI レビュー（レビュー自身がテスト・検査スクリプトを実走する形式）の実行主体。
  静的読解のみのレビューはこのスロットの対象外である。
- **既定**: `.github/workflows/claude-code-review.yml`。
- 🔴 **同時に有効な実装は常に 1 つである。** `ai-roster.json` の reviewer は単一値＋フォールバック連鎖のみを
  持ち、複数エンジンの並記はできない。**AI レビューの多重化は品質を上げない（実証あり。品質の主軸は
  決定的ゲート）** — 計画リポ同ガイド §4 の確定裁定であり、スキーマでこれを構造的に守る。
- **差し替え時に守る契約**:
  1. **検証実走付きであること**（実行コマンドと出力の証跡付きで報告する。宣言だけのレビューは不合格）。
  2. **approve をマージ可否の根拠にしない**（required checks が主軸）。
  3. **健全性を監視する**（実走が静的読解へ退行していないか）。Claude 用の安全弁
     （`check-review-verdict.js` / `check-permission-denials.js`）は claude-code-action 専用であり、
     差し替え先での同等の仕組みは**差し替える側の責務**である。

### スロットにしない役割

**批判（Adversarial Review）・分析・壁打ち専任のスロットは設けない。** 並行して別視点のレビューを
重ねる形は上記 §4 裁定（レビュー多重化は品質を上げない）に抵触し、決定的ゲート主軸の構造上
マージ判定へ接続する枠も無い。設計段階での別エンジンによる批判・セカンドオピニオンは、
上流（計画リポ）での**人間主導の利用**であり本キットの統制対象外である。
将来「批判役」を実装側で使う場合の受け皿は、新スロットではなく**フェーズ末監査（同ガイド §5）の
実行主体の差し替え**である。その拡張は事故・効果の実測が出てから検討する（1 回目は記録に留める）。

## 3. `ai-roster.json` のスキーマ

```json
{
  "slots": {
    "orchestrator": { "engine": "claude-code", "implemented_by": "対話セッション + .claude/commands/", "fallback": [] },
    "worker": {
      "engine": "claude",
      "implemented_by": { "cli": "scripts/ai-adapters/run-worker-claude.sh", "github": ".github/workflows/claude-coding.yml" },
      "fallback": []
    },
    "reviewer": { "engine": "claude", "implemented_by": ".github/workflows/claude-code-review.yml", "fallback": [] }
  }
}
```

- `engine`: エンジン名（単一値）。アダプタのファイル名 `run-worker-<engine>.sh` と一致させる。
- `implemented_by`: **必須**。その割当の現在の実現手段（実在するファイル・仕組み）。実体が無ければその割当は無効。
- `fallback`: 失敗時に切り替える先のエンジン名の配列（先頭から順に試す）。**空なら切り替えない**。
  既定エンジン以外を挿すときは、**末尾に `"claude"` を置いて既定へ戻れるようにする**ことを推奨する。
- ロースターの検証器は無い（検査器の新設は「同型の事故が 2 回」条件。誤設定は記録に留める）。
- 配布先がエンジンを差し替えた状態は AI_SETUP.md §1 のチェックボックスと同じ
  「キットが選択を委ねている欄」＝固有デルタ第 5 種であり、キットへの追随漏れではない
  （`scripts/check-kit-sync.js` の分類表に記録する）。

## 4. フォールバック（失敗時の切り戻し）

### CLI 面（実現手段: `scripts/ai-adapters/run-worker.sh`）

orchestrator は worker を共通エントリ `run-worker.sh` 経由で起動する。同スクリプトが失敗を判定し、
**既定でロースターの `fallback` 連鎖の次エンジンへ自動で切り替える**（`--no-fallback` で無効化）。

- **失敗の判定**: ①非 0 終了 ②タイムアウト（`WORKER_TIMEOUT` 秒。既定 2700 = GitHub 面の
  `claude-coding.yml` と同じ 45 分）③stderr のレート制限・過負荷パターン（`429` / `rate limit` / `overloaded`）。
- **切り替えの手順**: worktree を割当時点の状態へ戻してから（未コミットの変更を破棄する。
  **worktree はアダプタ専用の隔離場所であり、この破棄は手作業を巻き込まない**）、次エンジンで同一入力を再実行する。
- **記録**: 切り替えの発生は stdout に機械可読 1 行（`FALLBACK <from> -> <to> reason=<種別>`）で出力される。
  orchestrator はこれを台帳へ記録し、成果 PR に `fallback-from:<engine>` ラベルを付ける。
  帰属（`Co-Authored-By` / `agent:` ラベル）は**実際に成果を出したエンジン**で記録する。

### GitHub 面（機械化しない・運用規範）

GitHub 起動のフォールバックは機械化せず、次の規範で運用する。

- **既定エンジンの経路（`claude-coding.yml`）は、他エンジンを主とする場合も `--prune` で削除せず
  有効のまま残す。** 他エンジンの起動が失敗・停滞したら、同じ issue へ `@claude` メンションで
  切り戻す（人間または orchestrator が行い、台帳に記録する）。
- 機械化しない理由: エンジンごとに起動・失敗の形が異なり、共通の失敗判定を CI に置くと
  誤検知が検査そのものを外させる。事故が 2 回実測されたら再検討する。

## 5. エンジンの追加手順

新しいエンジン `<engine>` を worker / reviewer に挿す場合:

1. **CLI**: `scripts/ai-adapters/run-worker-<engine>.sh` を `README.md` の共通契約
   （入出力・終了コード・帰属・安全策）に従って作成する。`run-worker-codex.sh` を雛形にする。
2. **GitHub**: ワークフローは `<engine>-coding.example.yml` / `<engine>-code-review.example.yml` の
   命名で `.github/workflows/` に置く（`apply-profile.sh <engine>` が `.example` を外す）。
   実装はエンジン公式の GitHub 連携手段に従う。
3. `ai-roster.json` の該当スロットの `engine` を差し替え、`implemented_by` を実在のパスへ更新し、
   `fallback` の末尾に `"claude"`（または残す既定経路）を置く。
4. **検査器の適用範囲を確認する**（次節）。非対象のエンジンは「検査されていない」状態で動く。

## 6. 既存検査器・ガードの適用範囲

**下表の「対象外」は「検査済み」を意味しない。** 非 Claude エンジンはこれらの外で動く。

| 仕組み | 適用範囲 | 非 Claude エンジンでの扱い |
| --- | --- | --- |
| `.claude/hooks/`（guard-bash / guard-secrets / check-impl） | Claude Code のみ | **効かない**。同等の安全策はアダプタ側の責務 |
| `.claude/settings.json` の permissions | Claude Code のみ | 同上 |
| `scripts/check-ai-workflow-config.js` | `claude-coding` / `claude-code-review`（claude-code-action）のみ | 他エンジンのワークフローは検査されない |
| `scripts/check-permission-denials.js` | claude-code-action の実行ログ形式のみ | 同上 |
| `scripts/check-review-verdict.js` | claude-code-action のレビュー投稿のみ | 同上 |
| CI の決定的ゲート（`ci` / `security` / `codeql` / `pr-title` / `pr-size`） | **全エンジン共通** | そのまま効く（エンジン非依存） |
| コミット・PR の規約検査（`check-commit-messages.js` 等） | **全エンジン共通** | そのまま効く |

**エンジンを差し替えても品質の主軸が決定的ゲート（下 2 行）にあることは変わらない。**
Claude 専用の仕組み（上 5 行）は「Claude を使うときの追加の安全弁」であり、マージ可否の根拠ではない。
