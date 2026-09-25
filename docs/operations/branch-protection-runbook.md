---
title: 運用 Runbook — develop のルールセット（必須チェック・コードオーナー・バイパス）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
---
<!-- trace:
ids: [NFR]
adrs: []
iadrs: [IADR-0185, IADR-0190]
specs: [20260926_644_branch-protection-codeowners]
issues: [#644, #501, #473, #483]
-->
<!-- 起点 ID・関連 ADR/IADR・仕様書名・修飾付き issue 参照は本文へ書かず、上の trace ブロックへ入れる（scripts/check-trace-blocks.js が検査する） -->

# 運用 Runbook: develop のルールセット（必須チェック・コードオーナー・バイパス）

> 運用仕様書（`docs/operations/`）の下位にあたる手順書である。
> **本書のコマンドはリポジトリの統制設定を変える。実行は利用者に留保される**（AI は用意だけを行い、実行しない）。
> 必須にする check 名の一覧の正本は [`docs/ai-workflow.md`](../ai-workflow.md) §必須チェックの有効化 であり、本書はそれを実行可能な形へ落としたものである。

## 🔴 まず知っておくこと —— 「ブランチ保護は未設定」は半分だけ正しかった

これまでの再検証は **旧来のブランチ保護 API**（`branches/develop/protection`）だけを見て「未設定（404）」と記録してきた。
**それ自体は正しいが、develop には別の仕組みである「ルールセット」が 2026-07-08 から有効である。**

```bash
$ gh api repos/endazon/ai-stock-trading/branches/develop/protection
{"message":"Branch not protected", ... "status":"404"}          # 旧来の保護は無い

$ gh api repos/endazon/ai-stock-trading/rulesets --jq '.[] | "\(.id) \(.name) \(.enforcement)"'
18662050 develop-rule active                                    # ← develop（既定ブランチ）に効いている
18662047 main-rule active
```

実測（2026-09-26）の `develop-rule` の中身:

| ルール | 設定 | 意味 |
| --- | --- | --- |
| `pull_request` | 承認 1 件・**コードオーナーの承認を要する**・最後の push 後の承認・会話の解決・push で古い承認を捨てる・マージ方式は squash / rebase | レビューの関門。**`CODEOWNERS` が無かったのでコードオーナーの部分は空振りしていた** |
| `required_status_checks` | **`pr-title` の 1 件だけ**（strict なし） | `build-and-test` も `claude-review` も必須ではない |
| `update` / `creation` / `deletion` / `non_fast_forward` | 有効 | バイパスを持つ者だけが develop を更新できる（PR のマージを含む） |
| `required_linear_history` / `required_signatures` | 有効 | 直線履歴・署名つきコミット |
| `code_scanning`（CodeQL） / `code_quality` / `copilot_code_review` | 有効 | CodeQL の high 以上・エラーで止める／Copilot の自動レビュー |

| バイパス（`bypass_actors`） | モード | 帰結 |
| --- | --- | --- |
| リポジトリロール `5`（**Admin**） | **`exempt`** | **ルールが評価されない。** 利用者本人（admin）と、**利用者の `gh` トークンで動く AI セッション**はすべてを素通りする |
| Integration `29110`（Dependabot） | `always` | 直接 push を含め常に素通り |
| Integration `1236702`（Claude の GitHub App） | `always` | 同上 |
| Integration `1143301`（Copilot coding agent） | `always` | 同上 |
| Integration `946600`（**未特定**。公開 API のスラッグ照会では引けなかった） | `always` | 同上。**利用者が画面で何かを確かめる**（Settings → Rules → Rulesets → develop-rule → Bypass list） |

**したがって「AI が実装し AI が承認してマージする」ループが止まらなかった原因は、保護が無いことではなく、
マージしている主体（利用者のトークン）が `exempt` であることにある。** 赤い `claude-review` のままマージできた事実もこれで説明が付く。
必須チェックを足すだけでは、この主体には効かない（手順 2 が要る）。

## この手順を実行する条件（いつ走らせるか）

- 必須チェックの一覧（[`docs/ai-workflow.md`](../ai-workflow.md) の表）を変えたとき、ジョブを改名・統合したとき。
- AI のマージ関門を実効化すると決めたとき（手順 2）。
- 棚卸しの再検証で、下の「確認」の結果が本書と食い違ったとき。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | リポジトリの **admin**（ルールセットの変更）。`gh auth status` で `repo` スコープ |
| 必要なツール | `gh`（2.x） |
| 所要時間の目安 | 手順 1 は 5 分。手順 2 は判断を含めて 15 分 |
| 実行者 | **利用者本人**（AI は実行しない。統制の配備は利用者に留保される） |

## 手順

### 手順 0: 現状を保存する（戻すときの正本になる）

```bash
gh api repos/endazon/ai-stock-trading/rulesets/18662050 \
  --jq '{name, target, enforcement, conditions, rules, bypass_actors}' > ruleset-develop.before.json
```

- **更新 API が受け取る 6 項目だけに絞って保存する**（`id`・`_links`・`created_at`・`current_user_can_bypass` 等の読み取り専用の項目を落とす）。
  このファイルはそのまま「戻し方」の本文になる。
- 保存したファイルはリポジトリ配下に置かない（誤ってコミットしない）。

**🔴 PUT の前に、本書の本文が現況と食い違っていないことを確かめる。** 手順 1・2 の本文は 2026-09-26 に取得したルールの配列を
書き写したものであり、更新 API はルールの配列を**丸ごと置き換える**。その後に画面等でルールが変わっていれば、PUT で黙って消える。

```bash
# 現況のルールの種類（required_status_checks を除く）。手順 1 の本文と同じ 10 種・同じ順であること:
# deletion non_fast_forward update creation required_linear_history required_signatures
# pull_request code_scanning code_quality copilot_code_review
gh api repos/endazon/ai-stock-trading/rulesets/18662050 --jq '[.rules[] | select(.type != "required_status_checks") | .type] | join(" ")'

# 値を持つルールの中身。手順 1 の本文の同じ要素と 1 項目ずつ一致すること
gh api repos/endazon/ai-stock-trading/rulesets/18662050 \
  --jq '.rules[] | select(.type == "pull_request" or .type == "code_scanning" or .type == "code_quality" or .type == "copilot_code_review")'
```

**1 つでも違えば、本書の本文をそのまま送らない。** 現況の値で本文を組み直してから送る（または本書を直す PR を先に出す）。

### 手順 1: 必須チェックを実際のジョブ名で埋める（低リスク・推奨）

`required_status_checks` だけを差し替える。**ルールセットの更新はルールの配列を丸ごと置き換える**ため、
既存の他のルールも同じ値で並べてある（2026-09-26 に取得した値そのまま。変えたのは最後の 1 要素だけ）。

```bash
gh api -X PUT repos/endazon/ai-stock-trading/rulesets/18662050 --input - <<'JSON'
{
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    { "type": "update" },
    { "type": "creation" },
    { "type": "required_linear_history" },
    { "type": "required_signatures" },
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 1,
        "dismiss_stale_reviews_on_push": true,
        "required_reviewers": [],
        "require_code_owner_review": true,
        "require_last_push_approval": true,
        "required_review_thread_resolution": true,
        "require_extra_approval_for_unattributed_changes": true,
        "allowed_merge_methods": ["squash", "rebase"]
      }
    },
    {
      "type": "code_scanning",
      "parameters": {
        "code_scanning_tools": [
          { "tool": "CodeQL", "security_alerts_threshold": "high_or_higher", "alerts_threshold": "errors" }
        ]
      }
    },
    { "type": "code_quality", "parameters": { "severity": "errors" } },
    { "type": "copilot_code_review", "parameters": { "review_on_push": true, "review_draft_pull_requests": false } },
    {
      "type": "required_status_checks",
      "parameters": {
        "strict_required_status_checks_policy": true,
        "do_not_enforce_on_create": false,
        "required_status_checks": [
          { "context": "build-and-test",         "integration_id": 15368 },
          { "context": "lint",                   "integration_id": 15368 },
          { "context": "commit-messages",        "integration_id": 15368 },
          { "context": "pr-title",               "integration_id": 15368 },
          { "context": "Secret scan (gitleaks)", "integration_id": 15368 },
          { "context": "Dependency review",      "integration_id": 15368 },
          { "context": "claude-review",          "integration_id": 15368 }
        ]
      }
    }
  ]
}
JSON
```

- **7 件の名前は実物から引いた。** 2026-09-26 にマージ済み PR の head の check-run を列挙し、7 件すべてが
  report されていることを確かめた（`gh api repos/endazon/ai-stock-trading/commits/<head SHA>/check-runs --jq '.check_runs[].name'`）。
  いずれも `paths:` を持たず全 PR で起動する（`ci.yml` / `security.yml` / `pr-title.yml` / `claude-code-review.yml` の `on:`）。
  bot の PR で `skipped` になるものは合格として扱われる。ただし **CHANGELOG・OpenAPI の自動更新 PR には現状そもそも check が 1 つも付かない**（下の「失敗したときの分岐」）。
- `integration_id: 15368` は GitHub Actions の App ID である（`gh api apps/github-actions --jq .id`）。
  **同じ名前のコミットステータスを別の経路から偽装されても合格にしない**ための固定である。
- `strict_required_status_checks_policy: true` は「develop の最新に rebase 済みであること」を求める。
  運用の FIFO（develop へ rebase → CI 通過 → マージ）と同じ形である。rebase の手間が嫌なら `false` のままでよい。
- **必須にしないもの**: `backend-test (1..4)`（matrix の脚。集約は `build-and-test`）、`Analyze (csharp)`（`paths:` あり）、
  `helm`（`paths:` あり）、`pr-size`（警告専用）。理由は [`docs/ai-workflow.md`](../ai-workflow.md) に書いてある。
- **追加の候補（利用者の判断）**: `static-checks`（文書・trace ブロック等の Node 検査）と `scripts-tests` も全 PR で起動し
  report されている。必須の表に載っていないのは表が先に書かれたためであり、足すなら上の配列へ 2 行加え、
  [`docs/ai-workflow.md`](../ai-workflow.md) の表も同じ PR で直す。

### 手順 2: バイパスを決める（判断が要る。これをしないと AI のマージ関門にはならない）

**手順 1 だけでは利用者のトークンで動く AI セッションは止まらない**（Admin が `exempt` のため）。
判断の前に、単独アカウント運用に固有の 2 つの制約を押さえる。

- **PR の作成者は自分の PR を承認できない。** 本リポジトリの PR は、AI が作ったものも含めて**利用者のアカウントが作成者**である。
  したがって「承認 1 件・コードオーナーの承認」は、**利用者のアカウントしか無い限り、バイパスなしでは永久に満たされない。**
  残したまま Admin を `pull_request` に下げると、**すべてのマージが `--admin`（バイパス）になり**、バイパスが日常の操作になって
  「越えた」という記録が意味を失う。
- **`update` ルールは「バイパスを持つ者だけが develop を更新できる」**（PR のマージを含む）。直接 push は `pull_request` ルールだけで
  既に禁じられるため、`update` を残す利点は薄く、残すと利用者のマージが条件を満たしていてもバイパス扱いになり得る。

| 案 | 設定 | 止まること | 止まらないこと・副作用 |
| --- | --- | --- | --- |
| **A. 現状維持** | Admin `exempt` のまま | 利用者以外の直接 push とマージ | AI のマージは何も止まらない。暫定手段（下の「限界」）を運用で守る |
| **B. 当面の推奨: 「赤いマージ」だけを止める** | Admin を `pull_request` へ。AI 系 Integration の `always` を外す。`update` ルールを外す。承認数 0・コードオーナー承認なし（単独アカウントでは満たせないため） | **必須チェックが赤・未完了、または会話が未解決の PR を、普通の `gh pr merge` でマージできなくなる。** develop への直接 push も止まる | 利用者（と AI）が**明示的に** `gh pr merge --admin` を使えば越えられる（越えた記録は残る）。**コードオーナーの承認は効かない** —— 「AI の実装を AI が承認する」ループは、チェックが緑なら止まらない |
| **C. 本来の関門（コードオーナーを効かせる）** | AI に**別の GitHub アカウント（または App）**を持たせ、書き込み権限だけ与える（admin にしない・バイパスに入れない）。Admin は `pull_request`。`update` ルールを外す。承認 1 件・コードオーナー承認は**残す** | **AI の PR は利用者の承認なしにマージできない**（AI のアカウントはバイパスを持たない）。利用者は承認してから通常のマージをする | **資格情報の新設が要る**（アカウント・トークンの作成は利用者の操作。AI には作れない）。AI の作業環境の `gh` をそのトークンへ切り替える作業も要る |

**案 B のコマンド**（手順 1 の後に実行する。ルールの配列と `bypass_actors` を 1 回で送る）:

```bash
gh api -X PUT repos/endazon/ai-stock-trading/rulesets/18662050 --input - <<'JSON'
{
  "bypass_actors": [
    { "actor_id": 5,      "actor_type": "RepositoryRole", "bypass_mode": "pull_request" },
    { "actor_id": 29110,  "actor_type": "Integration",    "bypass_mode": "pull_request" },
    { "actor_id": 946600, "actor_type": "Integration",    "bypass_mode": "pull_request" }
  ],
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    { "type": "creation" },
    { "type": "required_linear_history" },
    { "type": "required_signatures" },
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": true,
        "required_reviewers": [],
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": true,
        "require_extra_approval_for_unattributed_changes": true,
        "allowed_merge_methods": ["squash", "rebase"]
      }
    },
    {
      "type": "code_scanning",
      "parameters": {
        "code_scanning_tools": [
          { "tool": "CodeQL", "security_alerts_threshold": "high_or_higher", "alerts_threshold": "errors" }
        ]
      }
    },
    { "type": "code_quality", "parameters": { "severity": "errors" } },
    { "type": "copilot_code_review", "parameters": { "review_on_push": true, "review_draft_pull_requests": false } },
    {
      "type": "required_status_checks",
      "parameters": {
        "strict_required_status_checks_policy": true,
        "do_not_enforce_on_create": false,
        "required_status_checks": [
          { "context": "build-and-test",         "integration_id": 15368 },
          { "context": "lint",                   "integration_id": 15368 },
          { "context": "commit-messages",        "integration_id": 15368 },
          { "context": "pr-title",               "integration_id": 15368 },
          { "context": "Secret scan (gitleaks)", "integration_id": 15368 },
          { "context": "Dependency review",      "integration_id": 15368 },
          { "context": "claude-review",          "integration_id": 15368 }
        ]
      }
    }
  ]
}
JSON
```

- 手順 1 からの差は 4 点だけである: `bypass_actors` の差し替え、`update` の除去、`pull_request` の承認数 0・コードオーナー承認と
  最後の push 後の承認を `false`。他のルールは 2026-09-26 に取得した値のまま。
- Claude の App（`1236702`）と Copilot coding agent（`1143301`）は外した。**AI 自身がルールを越えられる設定は、
  コードオーナーを置いた目的と正面から矛盾する。** Dependabot（`29110`）は `pull_request` に下げた（依存更新も PR と必須チェックを通す）。
- `946600` は正体を確かめてから残すか決める（上は「残す・ただし PR でのみ」）。
- `required_review_thread_resolution: true` は残した。Copilot の自動レビュー（`copilot_code_review`）が付けた会話も、
  解決しないと普通のマージができない。煩わしければ `false` にする。
- 案 B を採ったら、AI 向けの規約に「`--admin` を使わない（使うのは利用者だけ）」を足すかどうかも決める（本書の射程外）。

**案 C** は、AI 用のアカウントとトークンが用意できた時点で、上の本文の `pull_request` を手順 1 の値（承認 1・コードオーナー承認 `true`・
最後の push 後の承認 `true`）へ戻して送る。`bypass_actors` は案 B と同じでよい（AI のアカウントは入れない）。

### 手順 3: `CODEOWNERS` を配置する（済）

`.github/CODEOWNERS` は `* @endazon` で配置済みである。`pull_request` ルールの「コードオーナーの承認」は現行のルールセットで既に有効なので、
**配置した時点から、バイパスを持たない主体の PR には利用者の承認が要る**（現状、そのような主体は外部の貢献者だけである）。
利用者自身が作成者の PR には効かない（上の制約）。コードオーナーが AI に効くのは案 C のときだけである。

## 確認（この手順が成功したと言える条件）

```bash
# 1) 必須チェックが 7 件になっている
gh api repos/endazon/ai-stock-trading/rules/branches/develop \
  --jq '.[] | select(.type=="required_status_checks") | .parameters.required_status_checks[].context'

# 2) 手順 1 でバイパスが変わっていない（手順 2 を実行したなら、その値になっている）
gh api repos/endazon/ai-stock-trading/rulesets/18662050 --jq '.bypass_actors'

# 3) 手順 1 で他のルールが消えていない（before と比べて required_status_checks 以外が同じ）
gh api repos/endazon/ai-stock-trading/rulesets/18662050 --jq '[.rules[].type]'
```

- 次に開いた PR の Checks 欄で、7 件に「Required」が付いていること。
- 案 B を採ったなら: `claude-review` が走り終わる前の PR に `gh pr merge --squash` を打つと**拒否される**こと（`--admin` なしで）。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| すべての PR が「Expected — Waiting for status to be reported」のまま | 必須にした名前が check として存在しない（ワークフロー名を書いた・ジョブを改名した） | 実際の PR の check-run 名を引き直し（[`docs/ai-workflow.md`](../ai-workflow.md) §check 名は「読む」のではなく「引く」）、配列を直す |
| `claude-review` だけが永久に来ない | AI 基盤の停止・トークン失効・利用枠超過 | 利用者が `--admin` で越える（案 B）か、一時的に配列から外す。**全 PR が止まる副作用は必須化の代償である** |
| **CHANGELOG・OpenAPI の自動更新 PR にチェックが 1 つも付かない** | 両ワークフローとも実質 `GITHUB_TOKEN` で PR を作るため、他のワークフローが起動しない。`changelog.yml` は `secrets.AUTOMATION_PR_TOKEN` を先に試すが、**その Secret は登録されていない**（2026-09-26 実測: 登録済みは `CLAUDE_CODE_OAUTH_TOKEN` と `PLANNING_REPO_TOKEN` だけ）。実際に CHANGELOG の PR #798 の head の check-run は **0 件** | 必須チェックを入れた後は、**両方とも利用者が `--admin` で越える**（案 B・C でも Admin のバイパスは `pull_request` で残す理由の 1 つ）。恒久策は PAT（または App トークン）を `AUTOMATION_PR_TOKEN` として登録し、`openapi.yml` にも同じ `token:` を使うこと（**資格情報の作成は利用者の操作**） |
| 手順 1 の後にバイパスや他のルールが変わっていた | 更新 API が省いた項目を既定値へ戻す挙動だった | 下の「戻し方」で before へ戻し、`bypass_actors` も並べた完全な本文で送り直す |

### 戻し方

```bash
gh api -X PUT repos/endazon/ai-stock-trading/rulesets/18662050 --input ruleset-develop.before.json
```

- `ruleset-develop.before.json` は手順 0 で 6 項目（`name` / `target` / `enforcement` / `conditions` / `rules` / `bypass_actors`）に
  絞って保存したものである。絞らずに保存してしまったときは、送る前に同じ `--jq` の式で絞る
  （`gh api repos/endazon/ai-stock-trading/rulesets/18662050 --jq '{name, target, enforcement, conditions, rules, bypass_actors}'` の形）。
- **戻せなくなることは無い。** ルールセットの編集はリポジトリの管理者の権限であり、ルールセット自身のバイパス設定
  （案 B・C で Admin を `pull_request` に下げる・外す）とは関係なく、管理者はいつでもルールセットを編集・無効化できる。
  最悪の場合も Settings → Rules → Rulesets → develop-rule の画面で `Enforcement status` を `Disabled` にすれば、すべてのルールが止まる。

## 記録

- 実行したら、[`docs/blocked-tasks.md`](../blocked-tasks.md) の B-1・B-2 の「最後に測った時点」と現況を更新し、
  対応する issue に「確認」の 3 コマンドの出力を貼る。
- どの案（A / B / C）を採ったかと理由は、同じ issue に利用者の判断として残す。

## 限界（この手順で担保できないこと）

- **必須チェックは「走り終わった」ことしか担保しない。** `claude-review` は 🔴 の指摘があっても success を返す
  （採否は人間の判断）。🔴 のままのマージを止めるのはコードオーナーの承認であり、それは案 C でしか AI に効かない。
- **案 A・B のあいだの暫定手段**: マージの前に、AI レビューの判定（🔴 / 🟡 / 🟢）が投稿されていること・🔴 が 0 件であること・
  CI が全緑であることを確かめる。🔴 が 1 件でもあればマージしない。
- `main` 側のルールセット（`main-rule`）は本書の射程外である（必須チェックを持たない。release の流れを決めるときに併せて見る）。
