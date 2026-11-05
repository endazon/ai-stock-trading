---
title: AST を単一ノードのクラウド（Hetzner）へ移すための values-cloud・イメージの digest 固定・DB のパスワードの Secret 化・検索を使わない判断の明示・OpenD と移行の手順書
type: spec
status: accepted
related_ids: [ADR-0006, ADR-0024, ADR-0073, FR-04, FR-08, NFR, IADR-0608, IADR-0053, IADR-0341, IADR-0455, IADR-0598]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0073_capital-baseline-missing-previous-day-observation-rejects-entry.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-08 RAG 検索の利用)
---

# 仕様書: 単一ノードのクラウドへの移行（#1525）

## 起点

- #1525（AST#24〔ADR-0006〕の子）。PoC を自宅の PC から Hetzner の単一ノード（k3s）へ移す。
- オーナーの決定（2026-11-05・planning#868）: 欧州の 8 GB で始める・ニュースの RAG は持ち込まない・Web の画面は持ち込まない・インフラ費の上限は月 5,000 円のまま・モデルと effort は変えない。
- 移行の条件（planning#868 のコメント・オーナーの決定）: 堅牢化の 5 段を満たすまで、本物の秘密をサーバーへ入れない。手順書の最初の節に書く。
- MSP 側の最小構成は MSP#2401（並行）。秘密の方式（ESO の有無）は向こうの決定に合わせる。本 PR は values で切り替えられる形にしておく。

## 決定（IADR-0608）

1. **`values-cloud.yaml` は `values.yaml` に直接重ねる単独の上書き**にする（`values-local.yaml` には重ねない）。経路 B の調整がクラウドへ黙って流れ込まないようにするため。
   - 値は `values-local.yaml` から、クラウドで要るものだけを写す。
   - 写さないもの: KB の宛先・資格の参照、Stage 0 の記録の構成、Discord の環境固有 ID。
2. **イメージは digest で固定する。** digest はリポジトリに書かない（ノードでビルドして取り込んだ後でしか決まらない）。
   - `global.image.requireDigest: true` のとき、自前のイメージ（11 サービス・OpenD・認証サイドカー）に digest が無ければ描画で止める。
   - digest は手順書の手順で別ファイル（ノードの外へ出さない・コミットしない）に書き出し、`-f` で重ねる。
   - `pullPolicy` は `Never`（レジストリを持たず、ノードへ取り込んだイメージだけを使う）。
   - 既定（`requireDigest` なし・digest なし）の描画は変えない。
3. **DB のパスワードを Secret から読めるようにする**（`global.db.passwordSecret`）。設定したときは、接続文字列のパスワードを env の参照（`$(DB_PASSWORD)`）で組み、値をチャートに書かない。
   - `values-cloud.yaml` はこれを使い、リポジトリの既定のパスワードを描画に出さない。
   - 未設定（既定・経路 B）の描画は変えない。
4. **検索（RAG）を使わない環境では、判断のプロンプトにその旨を 1 行書く**（本判断・一次とも。ニュースの状態の行の直後）。
   - ニュースの「取得済み」の行は「個々の記事は下の参考情報にあるものだけを渡します」と書く。検索が無い環境では参考情報は決して出ないので、そのままでは読み違えの余地がある（実測は §RAG を外したときの実測）。
   - 足すのは、検索の取得口が未構成（no-op）のときだけである。経路 B（検索を構成）のプロンプトは 1 文字も変えない。
   - 判断の中身（model・effort・max_tokens・用途）は変えない。
5. **秘密の供給方式は `externalSecrets.enabled` で切り替える。** values-cloud の既定は `false`（手で作る Secret）とする。MSP#2401 の決定が ESO なら、`true` へ 1 行で切り替える。
6. **OpenD**:
   - 配布物の tar の sha256 を記録してから、ノードでイメージを作る。
   - 初回の SMS 認証は `kubectl attach` で利用者が行う。
   - 取引パスワードはクラスタに置かない。SIMULATE では解錠は要らない。実弾は別の判断である。
7. **ArgoCD は最初の稼働では使わない**（`targetRevision: main` が空のブランチを指す）。手順書に書き、values-cloud を ArgoCD の valueFiles に入れないことを試験で固定する。

## RAG を外したときの実測（決定 4 の根拠）

検索を外し、ニュースは集める（`finnhub-news` ほか）構成で、`TradeDecisionPromptBuilder.Build` を呼んだ。

- 参考情報の節は出ない（`retrieved` が空。`AppendRetrievalSection` は何も書かない）。
- ニュースの状態の行は「取得済み（…個々の記事は下の参考情報にあるものだけを渡します。参考情報に無いことは『材料となるニュースが無い』ことを意味しません）」になる。
- 誤読の否定（「無いことは、無いことを意味しない」）はあるが、「下の参考情報」が恒久的に存在しないことは読めない。
  - 経路 B では、検索の取りこぼし（その回に当たりが無い）と区別できる必要がある。
  - クラウドでは常に無い。
  - → 「この環境では検索を使っていない」を明示する（決定 4）。
- 計画との関係: FR-08（参考情報を判断に渡す）を PoC のクラウドの期間は満たさない。オーナーの決定（planning#868 の 2）として既に環流済みである。本 PR は、実装の扱い（決定 4）を planning#868 へ追記する。

## 母集合（規則 9）

- **KB の宛先の env**: `git grep -n "KnowledgeBase__" -- deploy/helm` → values-local の information-collection・report・trade-decision（Documents / Search / Auth / SearchAuth）。
  - values-cloud では、情報収集・報告書・取引判断のいずれにも書かない（既定の空＝no-op）。
  - テンプレートの導出（`KnowledgeBase__Auth__Authority`）は宛先があるときだけ描くので、空なら現れない。
- **イメージを描く行**: `git grep -n "image:" -- deploy/helm/ai-stock-trading/templates` → `deployment.yaml`（11 サービス）・`opend.yaml`（OpenD・認証サイドカー）・`cronjob.yaml`（取引サイクルの curl。values-cloud では無効）。
- **DB の接続文字列**: `deployment.yaml` の `ConnectionStrings__DefaultConnection` の 1 か所（`$svc.db` を持つサービス）。
- **検索の取得口**: `IRetrievalContextProvider` の実装は `NoOpRetrievalContextProvider` と `KnowledgeBaseRetrievalContextProvider` の 2 つ。プロンプトを組む呼び出しは本番（`TradeDecisionAppService`）と Stage 0（`Stage0DecisionRecorder`）。
  - Stage 0 は当時の参考情報を as-of 入力から渡すので、本 PR の行は足さない（検索の構成と無関係）。
- **除外**: `values-grpc-measurement.yaml`（計測用の重ね・本件と無関係）、ArgoCD の `appproject.yaml`。

## 受け入れ基準

- [x] values-cloud の描画に、knowledge・retrieval・document-service への宛先が無い（helm.yml）。
- [x] 同じ描画で、すべてのイメージが digest で固定され、`pullPolicy` は `Never`。digest が無ければ描画が止まる（helm.yml）。
- [x] values-cloud の描画に、リポジトリの既定の DB パスワードが現れない（helm.yml）。
- [x] KB の宛先を空にした構成で、取引判断・情報収集・報告書が起動し、判断の 1 回（一次と本判断）と日報の 1 回が失敗しない（xUnit）。
- [x] 検索が未構成のときだけ、プロンプトに「検索を使っていない」の行が出る。構成したときは出ない。model・purpose は変わらない（xUnit）。
- [x] 手順書に、移行の条件（5 段）・二重ログインの禁止・切り替えの順番・週末の期限・戻し方・初日と初週の確認・ArgoCD を使わないこと・OpenD の作り方と初回認証・取引パスワードを置かないことが書かれている（scripts.repo.test.js）。
- [x] values-local の描画が変わらない（本 PR の前後で `helm template -f values-local.yaml` のバイト一致を確かめた。PR 本文に記録）。
- [x] `/verify` が通る。
