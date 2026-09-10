---
title: SC-04 OpenD 認証操作画面（BFF 4 端点＋画面）を新設する
type: spec
status: done
related_ids: [FR-09, FR-11, SC-04, UC-06, NFR-03, NFR-05, NFR-06, ADR-0002, ADR-0024, IADR-0321]
author: claude (Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
  - planning:projects/ai-stock-trading/05_screens/mockups/hi-fi/sc-04.html
  - planning:projects/ai-stock-trading/05_screens/mockups/wireframe/sc-04.html
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md
---

# 仕様書: SC-04 OpenD 認証操作画面（BFF 4 端点＋画面）を新設する

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-04「OpenD 認証操作画面」（2026-09-09 新設）
- 機能要求（FR）: FR-09（Discord 通知）・FR-11（監査ログ）
- 非機能要件（NFR）: NFR-03（稼働率）・NFR-05（資格情報の秘匿。**適用範囲を一時的な検証コードへ広げた扱い**）・NFR-06（発注機能の非公開＝存在秘匿）
- ユースケース（UC）: UC-06 の**代替フロー**「ゲートウェイの有人認証」（**UC は新設しない**）
- 関連 ADR: ADR-0002（前提条件 1: 初回のデバイス信頼の確立は有人）・ADR-0024（決定 1 の 2 条件・決定 2）
- 計画書: https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/05_screens/01_screens.md の SC-04 節（隣接クローンの既定パスは project-planning／読み取り専用）
- 起点となる裁定: planning#594（利用者裁定 2026-09-09。**再議しない**）
- 既存の実装: #722（OpenD 標準入力の FIFO 化。コミット `b504b809`）

## 目的・背景

moomoo OpenD はログイン時に検証コード（SMS / 画像 CAPTCHA）を**対話コンソール**から要求する。
「初回のみ有人」は ADR-0002 / ADR-0024 で決着していたが、**その有人操作をどこで行うか**が計画に無く、
現行の唯一の手段は `kubectl attach`（運用者の手元に kubeconfig と CLI を要する）であった。
planning#594 の裁定により、**画面の 1 つとして提供する**。

**本画面は常用するものではない。** ADR-0024 決定 1 の 2 条件（デバイス信頼の永続化・egress IP の安定）が
そろう環境では再起動をまたいで無人で再ログインできる。要るのは (1) 初回のデバイス信頼の確立、
(2) 2 条件が崩れた場合の再認証、の 2 つだけである。

## 母集合の引き直し（着手前・必須。キット規則 1〜8 ＋ 本リポ規則 9・10）

**issue 本文・依頼文の「反映先」リストは母集合ではない。** 着手時に自分で引いた結果を以下に置く。

### 軸 1 —— 計画 ID レンジの宣言（誤りの側＝`SC-01..03` から引く）

```
grep -rn "SC-01\.\.0\?3" .   （node_modules / .git / bin / obj を除外。拡張子で絞らない）
```

| ヒット | 追随するか | 理由 |
| --- | --- | --- |
| `.claude/rules/traceability.repo.md:9` | **する** | **機械の単一情報源**（`readPlanIds()` / `check-commit-messages.js` / `check-trace-blocks.js` が読む）。ここを直さないと `feat(SC-04)` の件名が「実在しない ID」で落ち、**force push 禁止のため事後修正できない** |
| `scripts/scripts.repo.test.js:1804` 付近 | **する** | 件数 `31` と「`SC-04` は実在集合に混ざっていない」を固定している回帰テスト。レンジを進めると**必ず赤くなる**（fail-loud。意図どおり） |
| `.ai-context/adr/IADR-0207_*.md` / `.ai-context/adr/README.md` | **しない** | **凍結記録**。当時の実測（31 件・`SC-04` を含まない）を述べた記述であり、後から書き換えると当時の記述と食い違う |
| `.ai-context/specs/20260818_532_*.md` / `20260909_591_*.md` | **しない** | 同上（point-in-time の作業仕様書。`traceability.repo.md` の除外表が明示） |
| `scripts/check-trace-blocks.js:570` | **しない** | 自己試験が書き出す**合成フィクスチャ**（「ADR レンジ宣言が無い規約は例外」の負例）。レンジの宣言そのものではない |

レンジの転記元は **ADR 本文ではなく計画リポの実測**である（IADR-0320 決定）。実測 2026-09-09:

```
$ node tools/doc-checks/gen-plan-ranges.js --check      # project-planning
## ai-stock-trading
| SC | [1, 4] | **[1, 4]** | 4 | なし | ✅ |
```

### 軸 2 —— 「画面は 3 つ」を前提にした列挙（誤りの側＝`3 画面` / `SC-01 / SC-02 / SC-03` から引く）

| ヒット | 追随するか | 理由 |
| --- | --- | --- |
| `frontend/src/components/PaperModeBanner.tsx:5`（`全画面（SC-01 / SC-02 / SC-03）の上部に`） | **する** | 計画が「SC-04 も同様である」と明記（2026-09-09 追加）。コメントが 3 画面のままだと、次の実装者が SC-04 を対象外と読む |
| `frontend/src/lib/paperMode.ts:8`（`3 画面すべてが引く語彙`） | **する** | 同上（4 画面になる） |
| `.github/workflows/ci.yml:818`（`フロント3画面（SC-01/02/03）の Playwright E2E`） | **しない** | 記述は正しい。**E2E は本 PR の対象外**（下記「対象外」）であり、spec を足していないのに「4 画面」と書くと**実態より広く見える** |
| `frontend/playwright.config.ts:3` | **しない** | 同上 |
| `.ai-context/adr/IADR-0162` / `IADR-0288` / `IADR-0290` / `.ai-context/adr/README.md` | **しない** | 凍結記録 |
| `CHANGELOG.md:349` | **しない** | 生成物。過去のコミット件名であり、是正するなら `changelog-overrides.json` 側（本件は誤記ではないので不要） |

### 軸 3 —— 画面 feature の登録点（`sc03-controls` をキーに全走査。記憶で挙げない＝規則 9）

| ヒット | 追随するか | 理由 |
| --- | --- | --- |
| `frontend/src/features/index.ts` | **する** | 合成面。**ルートとナビは別経路**であり、片方だけだと「開けるのに左ナビに出ない」（逆も）になる |
| `frontend/src/features/index.test.tsx` | **自動的に掛かる** | `aiStockTradingNavItems` を `it.each` で回すため、ナビを足すと**新画面にも「h1 が描かれる」検査が自動で掛かる**（＝端点が全滅していても h1 を描く必要がある） |
| `frontend/eslint.config.js` | **不要** | feature ゾーンは `readdirSync` の**実ディレクトリ走査**で作られる（列挙を手で持たない設計）。新 feature は自動で規則の対象になる |
| `frontend/vitest.config.ts` | **不要** | `src/**/*.{test,spec}.{ts,tsx}` の glob |
| `docs/screens/20260718_SC-0{1,2,3}_*.md` | **する（新設）** | SC-04 の画面仕様書を同じ形（trace ブロック込み）で足す |
| `.ai-context/specs/*` | **しない** | 凍結記録 |

### 軸 4 —— BFF ルートの登録点（`MapRiskControlsBffEndpoints` をキーに引く）

| ヒット | 追随するか | 理由 |
| --- | --- | --- |
| `backend/Bff/AiStockTrading.Bff.Endpoints/` | **する（新設）** | `OpendAuthBffEndpoints.cs` |
| `backend/Bff/AiStockTrading.Bff.Endpoints.Tests/BffPassThroughTests.cs` の `AllRoutes` | **する** | 🔴 **「登録されているルートは `AllRoutes` と完全一致する」テストがある。** ルートを足して `AllRoutes` を更新しないと**必ず赤くなる**（意図どおりの fail-loud） |
| platform 側 `Platform.Bff/Composition` の登録簿 | **できない（対象外）** | 別リポジトリ（microservices-platform）の所有物。本リポの `.slnx` にも無い。**合成点への登録は MSP 側の作業**であり、ここでは行えない |

### 軸 5 —— 規則 10（この変更で新たに誤りになる自分の記述）

レンジを `SC-01..04` へ進めると、`scripts/scripts.repo.test.js` の**導出値** `31` が誤りになる。
**走査ではなく計算し直す**: `FR 21 + UC 7 + SC 4 = 32`。

## 対象範囲

### 対象

- **BFF**（`backend/Bff/AiStockTrading.Bff.Endpoints/`）
  - `OpendAuthBffEndpoints.cs`（新設。`/bff/opend-auth/` の 4 端点）
  - `OpendAuthUpstream.cs`（新設。**上流サイドカーとの写像を 1 ファイルへ閉じる**）
  - `BffPassThroughTests.cs` の `AllRoutes` 更新／`OpendAuthBffTests.cs`（新設）
- **フロントエンド**（`frontend/src/features/sc04-opend-auth/`。新設。`sc03-controls` と同じ形）
  - `types/` `api/` `components/` `routes/` `index.ts`
  - `frontend/src/features/index.ts`（合成面へ 1 行ずつ）
  - `frontend/src/components/PaperModeBanner.tsx` / `frontend/src/lib/paperMode.ts` のコメント（3 画面 → 4 画面）
- **規約・記録**
  - `.claude/rules/traceability.repo.md`（SC レンジ `01..03` → `01..04`）
  - `scripts/scripts.repo.test.js`（実在集合の件数 31 → 32・`SC-04` の位置を移す）
  - `docs/screens/20260909_SC-04_opend-auth.md`（新設。trace ブロック込み）
  - `.ai-context/adr/IADR-0321`（新設）

### 対象外（理由つき。黙って落とさない）

| 対象外 | 理由 |
| --- | --- |
| **サイドカー本体**（`backend/Services/OpendAuthGateway/`） | **別エージェントが #722 で並行実装中**。本 PR は BFF から上流として呼ぶだけで、実体は作らない（ファイル領域が非重複） |
| **platform 合成点への BFF 登録** | 別リポジトリ（microservices-platform）の所有物。本リポから触れない（軸 4） |
| **送信履歴の照会 API** | 計画の供給元の表が「🟡 **本画面が読む照会 API は未実装である**」と明記。**4 端点の外に新しい口を作らない**（口が増えるほど攻撃面が増える）。画面は**供給が無い**として描く |
| **Discord 通知（FR-09）の発火** | 通知はサイドカー／後段の関心事であり、BFF は pass-through である。**BFF から通知を投げると二重通知になる** |
| **Playwright E2E** | 既存 3 画面の E2E と同じ枠組みで別途足す。本 PR は単体テスト（Vitest / xUnit）に留める（軸 2 で `ci.yml` を更新しない理由と対） |
| **再送レート制限の実測値** | 計画が「**暫定 60 秒・算定根拠なし・実測待ち**」と明記。実装は暫定値を**サイドカー側**が持ち、BFF は透過するだけ |

## 設計

### 1. 認可 —— BFF 自身が `trading-owner` を強制する（既存 3 モジュールとの意図的な非対称）

既存の BFF（Assumptions / RiskControls / Monitor）は `.RequireAuthorization()` だけを掛け、
**owner 判定を後段（`OwnerOnly` ポリシーを持つドメインサービス）へ委ねる**。

🔴 **本モジュールはこれを踏襲できない。** 上流の OpenD 認証サイドカーは
**設計上まったく認証を持たない**（Ingress を持たず Pod 網にしか bind せず、入力面の安全は
allowlist が担う）。委譲先が無いため、**BFF が唯一の認可点である**。

- グループへ**エンドポイントフィルタ**を 1 つ掛け、4 端点すべてに機械的に効かせる
  （端点ごとに書くと、次に端点を足した人が落とす）。
- `trading-owner` を持たない場合は **`404 NotFound`**（403 ではない）。**存在秘匿**（NFR-06）。
- 🔴 **ロールが無いときは上流を呼ばない。** 呼ぶと、応答時間の差だけで「その端点は在る」と分かる。
  フィルタは上流呼び出しの**前**に位置する。

### 2. 上流契約と写像（`OpendAuthUpstream.cs` の 1 ファイルへ閉じる）

`OpendAuth:BaseUrl` を基点に、以下を呼ぶ。

| BFF | 上流 | 応答 |
| --- | --- | --- |
| `GET /bff/opend-auth/state` | `GET {base}/opend-auth/state` | `{ status, prompt, captchaAvailable, lastLoginAt, detail }` |
| `GET /bff/opend-auth/captcha` | `GET {base}/opend-auth/captcha` | `image/png` / 404 |
| `POST /bff/opend-auth/code` | `POST {base}/opend-auth/verify` | 204 / 400 / 409 / 503 |
| `POST /bff/opend-auth/resend` | `POST {base}/opend-auth/resend` | 204 / 409 / 503 |

**写像を 1 ファイルへ閉じる理由**: サイドカーは並行実装中であり、着地時に欄名が動く。
**動いたときの変更が 1 ファイルで済む形**にしておく（呼び出し側は写像後の型しか知らない）。

> 🔴 **［着手時の実測・2026-09-09］並行実装中のサイドカーの現物は、本仕様が前提とする契約と 4 点で異なる。**
> ①`state` に `status` / `lastLoginAt` が無く、代わりに `consoleAvailable` / `consoleTail` を持つ。
> ②`verify` の本文が `{ kind, code }` であり `kind` を**受け取る**。③`/opend-auth/resend` が無く、
> 再送は `verify` の `kind: "resend"` である。④エラー本文が `{ error }`。
> **本 PR は依頼で与えられた契約（上表）に対して実装する**（差異の調停は依頼者が行うと明示されている）。
> 上表の 4 点は `OpendAuthUpstream.cs` の中だけに現れるため、調停は同ファイルの編集で閉じる。
> **なお ② は BFF の対外契約には影響しない** —— BFF は待機中プロンプトから `kind` を**自分で決める**
> のであって、利用者から受け取るのではない（下記 4）。

### 3. 3 状態の描き分け（**値がある / 対象なし / 供給が無い**）

計画の共通規約「供給が無い値の表示規約」をそのまま適用する。**新しい形を発明しない** ——
既存の `MetricAvailability`（`Available`=0 / `NotSupplied`=1 / `NotApplicable`=2）と、
フロントの `isNotSupplied` / `METRIC_NOT_SUPPLIED_TEXT` を再利用する。

| 実態 | `promptAvailability` | 画面 |
| --- | --- | --- |
| OpenD が SMS / 画像の入力を待っている | `Available`(0) ＋ `prompt` | プロンプト種別を表示し、入力欄を**有効**にする |
| ログイン済みで入力待ちではない | `NotApplicable`(2) | 「いま OpenD は入力を待っていません」。入力欄は**無効** |
| 上流へ到達できない／`OpendAuth:BaseUrl` が未設定 | `NotSupplied`(1) | 「取得できていません（供給元がありません）」。入力欄は**無効** |

🔴 **取り違えると「壊れたゲートウェイ」が「正常にログインできている」ように見える。**
これが本画面で最も高くつく誤りである。

**供給可否はサーバが宣言する**（画面が推測しない）。`OpendAuth:BaseUrl` が空文字のときは
BFF が `NotSupplied` を宣言する（`KnowledgeBase__Search__BaseUrl` の空＝no-op と同じ fail-safe）。

### 4. 利用者はコマンドを選べない

- `POST /bff/opend-auth/code` の**本文はコードだけ**（`{ "code": "..." }`）。
  **`command` も `kind` も受け取る欄が無い。**
- 送るコマンドは**サーバが現在の待機プロンプトから決める**:
  `phone` → `input_phone_verify_code` / `pic` → `input_pic_verify_code`。
- **待機していなければ `409`**（画面の無効化だけに頼らない）。
- 再送は `POST /bff/opend-auth/resend`（**本文なし**）→ `req_phone_verify_code`。
- 上記 3 つ以外は**存在しない**。`relogin -login_pwd=` / `exit` / `close_api_conn` /
  `set_log_level` / `show_delay_report -detail_report_path=` / `show_sub_info -sub_info_path=` は
  **BFF に経路が無い**（許可リスト方式であって拒否リスト方式ではない）。
  後 2 者は **root 権限で任意パスへ書ける**ため、とくに危険である。

### 5. 検証コードの値を記録しない（NFR-05 の適用範囲の拡張）

**残すのは「送信した事実・日時・アクター・コマンド種別・結果」だけである。**

- BFF は要求本文を**ログへ出さない**（例外メッセージも出さない —— `JsonException` のメッセージには
  本文の断片が入る）。
- BFF の応答本文に**コードを載せない**（要求と同じ値でも返さない）。
- 上流応答（204）に本文は無く、そのまま透過する。
- 🔴 **`ProxyAsync` の素の流用ができない箇所である。** 既存 3 モジュールは本文を丸ごと
  「透過」するが、本モジュールは**コードを含む本文を再構成して上流へ渡す**（画面から来た
  本文をそのまま上流へ流すと、欄が増えたときに素通りする）。

### 6. 画面の構成（ハイファイモックアップ準拠）

`sc03-controls` のレイアウトをそのまま踏襲する（`components/` `routes/` `index.ts` ＋ `api/` `types/`）。

1. **内蔵 `paper` 稼働時の警告バナー** —— 計画が「SC-04 も同様である」と明記。
   🔴 **モックアップ本体には描かれていない**（共通要素を代表 1 箇所で示す慣行）。**見た目だけを頼りにすると落とす。**
2. **ゲートウェイの状態**（参照）: 接続状態・待機中のプロンプト種別・最終ログイン成功日時・
   デバイス信頼の永続化・egress IP の安定性・現在の発注先（参照のみ。変更は SC-02）。
3. **検証コードの入力**（本画面の主操作）: 入力欄 1 つと送信、SMS 再送。**コマンド名を持たない。**
4. **この画面から送れる操作**（許可リスト。利用者への**情報**として提示）。
5. **送信履歴**: 照会 API が無いため**供給が無い**として描く（「履歴なし」と描かない）。

**色だけで意味を持たせない**（色 ＋ 文言）。既存の共通プリミティブ（`PaperModeBanner`・
`availability*Text`）を使い、新しいプリミティブを作らない。

## 受け入れ基準

- [ ] `trading-owner` だけが 4 端点へ到達し、**それ以外は 404**（403 でも 401 でもない）
- [ ] ロールが無いとき、**上流を 1 度も呼ばない**
- [ ] 検証コードが**応答本文にもログ出力にも 1 度も現れない**
- [ ] 待機中でないときの `POST /code` は **409**
- [ ] **クライアントは送信コマンドを左右できない**（`command` / `kind` を本文へ入れても無視され、
      上流へは待機プロンプト由来の種別だけが渡る）
- [ ] `OpendAuth:BaseUrl` が空のとき、`/state` は **`NotSupplied` を宣言**する（200 で「正常」に見せない）
- [ ] 画面が 3 状態（値あり / 対象なし / 供給が無い）を**取り違えず**描き分ける
- [ ] `trading-owner` を持たない利用者は画面の存在を知れない（`NotFound` ＋ **API を呼ばない**）
- [ ] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る
- [ ] `pnpm`（npm）の `lint` / `typecheck` / `test` が通る
- [ ] 文書検査（`check-trace-blocks` / `check-cross-repo-refs` / `check-plan-id-qualification` /
      `gen-knowledge-graph --check` / `check-commit-messages`）が通る

## ［2026-09-10 追記 / AST#723］着地と配備

PR AST#723 で着地し、基盤側の BFF 合成点へも登録した（MSP#1366）。基盤の BFF とフロントエンドを
焼き直して配備済みで、**画面は 404 ではなくなっている**。

上流サイドカーとの契約のずれ 4 点は調停済みである。サイドカー側を本仕様書の契約へ寄せた
（`status` と `lastLoginAt` を足し、`verify` はコードだけを受け、`resend` を別の口に出した）。

残るのは**画面からの実投入**で、これは利用者の手が要る（SMS は利用者の端末へ届く）。

## 計画書との差異

| 差異 | 理由 |
| --- | --- |
| ルートを `/trading/opend-auth` ではなく **`/opend-auth`**（ユニット相対）とした | **既存 3 画面が同じ形**である（計画 `/trading/settings` → 実装 `/settings`、`/trading/control-status` → `/controls`）。`/trading` は基盤 SPA がユニットを載せる位置であり、ユニット側の宣言に含めると二重になる。**SC-04 だけ別の形にすると 4 画面の合成が壊れる** |
| 送信履歴を表示せず「供給が無い」と描く | 計画の供給元の表が「照会 API は未実装」と明記。4 端点の外に口を増やさない（対象外の表） |
