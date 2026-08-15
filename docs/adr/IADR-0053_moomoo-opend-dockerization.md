---
title: IADR-0053 moomoo OpenD はダウンロード方式の Docker Image で常駐させ、k8s に opend サービスとしてオプトイン配備する（Proposed）
type: impl-adr
status: Proposed
related_ids:
  - ADR-0002 # 証券会社連携=moomoo OpenAPI（OpenD 常駐が必要）
  - ADR-0024 # 無人再起動は「安定 egress IP」を条件に成立（本 IADR の初回結論を反証）
  - IADR-0016 # 実弾防止ゲート（既定 paper）
  - IADR-0052 # AST k8s Helm chart
  - IADR-0167 # 本 IADR の初回結論の撤回と、再起動最小化の維持
author: claude
created: 2026-07-14
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md"
  - "../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md"
---

# IADR-0053: moomoo OpenD の Docker Image 化（Proposed）

- 状態: **Proposed**（検討・試作段階。Accepted は無人運用/Hetzner 接続の一次確認後）
- 日付: 2026-07-14 ／ **改訂: 2026-08-07**（下記「改訂（2026-08-07・ADR-0024）」）
- 決定者: claude（実装・起案）

> ## ⚠️ 改訂（2026-08-07・[ADR-0024](../../planning/projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md)）
>
> **本 IADR の「PoC 結果」が『完全無人（自動再起動）は不可（確定）』と書いている命題は、反証されている。**
> 計画 ADR-0024 決定2 が**名指しで「誤りである」と定めた**（利用者裁定・質問票 第 13 回 Q2）。
>
> 正しくは **条件付き成立**である（ADR-0024 決定1）。次の 2 条件がそろえば、**Pod 再作成をまたいで無人で再ログインできる**。
>
> | 条件 | 内容 |
> | --- | --- |
> | **(1) デバイス信頼の永続化** | 先行する対話デバイス検証と API 規制アンケートの完了が **PVC（`/root/.com.moomoo.OpenD`）に永続している**こと |
> | **(2) egress IP の安定** | **NAT 後の egress IP が変化しない**こと（例: 単一ノード・固定 NAT）。**Pod IP（クラスタ内部）は無関係である** |
>
> **初回 PoC の誤りの所在**: 「新 Pod ＝新 IP だから再検証」という推論が、**Pod IP（クラスタ内部）と egress IP（NAT 後）を
> 混同していた**（ADR-0024 §コンテキスト）。**moomoo が検証の対象にしているのは egress IP であり、Pod IP は無関係である。**
> この誤りは同日（2026-07-15）の #13 結合作業で既に反証されており（Deployment を 3 リビジョン `Recreate` して
> 3 回とも対話検証なしに `Login successful`。#130）、**その事実が本 IADR へ反映されないまま 3 週間残った**。
>
> **ただし「再起動してよい」ことにはならない**（ADR-0024 決定3）。**再起動の最小化は維持する。**
> 本改訂が認めるのは「**再起動しても復旧できる**」ことであって、再起動を推奨するものではない。
> **SPOF であること自体も変わらない**（決定4。単一インスタンスであり、復旧までの発注不可時間は生じる）。
>
> **未検証として残るもの**（ADR-0024 決定5・`docs/blocked-tasks.md` A-9 に登録）: ① **egress IP 変更時の再検証の要否**、
> ② **Hetzner（海外 IP）からの接続可否と ToS 適合**、③ 取引パスワードのアンロック自動化の可否。
>
> **状態は `Proposed` のまま据え置く。** 昇格条件のうち「無人運用の一次確認」は満たされたが、
> **「Hetzner 接続の一次確認」は決定5-2 で依然未検証**であるため、片方だけでは昇格させない（[IADR-0167](IADR-0167_opend-unattended-restart-followup.md) 決定1）。
>
> **以下の初回 PoC の記述は、誤りも含めて消さずに残す。** 誤りの所在（Pod IP と egress IP の混同）が
> 最も再発しやすい形であり、消すと同じ誤りを繰り返すためである（IADR-0167 決定4）。

## 起点・関連

- 関連計画 ID: ADR-0002（moomoo OpenAPI。**OpenD ゲートウェイの常駐が必要**。2026-08-01 に `Accepted`。
  「OpenD 無人運用の成立性」は **ADR-0024 で決着（条件付き成立）**。「海外 IP(Hetzner) 接続・ToS」は**未決のまま**）／
  **ADR-0024**（本 IADR の初回結論を反証した計画 ADR）／IADR-0016（実弾防止ゲート・既定 paper）
- Issue: #124（OpenD Docker 化の検討・試作）／ #13（moomoo アダプタ実装）
- 前提環境: MSP#266 / AST #122（連結ローカル k8s dev）

## コンテキストと課題

moomoo 発注（#13）は OpenD（FutuOpenD）ゲートウェイの常駐が前提（既定 :11111）。現状 `BrokerFactory` は
`Broker:Provider=moomoo` を選ぶと起動停止する（実弾防止・IADR-0016）。連結 k8s dev で OpenD をコンテナ常駐
できれば、moomoo アダプタは `opend:11111` へ接続する構成に落とせる。課題は **無人ログイン（デバイス認証/2FA）**、
**バイナリ再配布(EULA)**、**資格情報の秘匿**、**海外 IP 接続の ToS**。

## 決定（方向性・Proposed）

1. **バイナリ非同梱の Docker Image** とする。OpenD(Linux) の**バイナリはイメージに焼かず・コミットせず**
   （再配布/EULA 回避・~440MB）、**公式取得の tar.gz をビルド時にコンテキストへ取り込む**（PoC で当初の
   「ダウンロード URL 方式」から変更。配布が口座ログイン前提のため。`.gitignore`＋ビルドスクリプトで一時配置）。
   バージョンは pin する。
2. **k8s には `opend` Deployment/Service（ClusterIP :11111）としてオプトイン配備**する（AST chart に
   `opend.enabled`、**既定 false**＝fail-safe。OpenD 不在時は moomoo を選べず paper のまま）。
3. **資格情報は k8s Secret / 環境変数**で注入し、`FutuOpenD.xml` をマウントする（コミットしない。暫定 Secret、
   恒久は Vault 等）。
4. **dev は SIMULATE（ペーパー）**（`TrdEnv.SIMULATE`）に限定する。実弾は本 IADR の対象外（money-safety）。
5. **デバイス認証の永続化で無人再起動** — 起案時に採用 → 初回 PoC で「不成立」として撤回 →
   **その撤回を 2026-08-07 に取り消す**（ADR-0024 決定1/決定2）。**デバイス信頼の永続化＋安定 egress IP で無人再起動は成立する。**
   採用するのは引き続き**常駐モデル**である（初回のみ対話検証・**再起動は最小化**する。ADR-0024 決定3）が、
   その理由は「再起動すると必ず有人検証が要るから」ではなく、「**SPOF であり復旧までの発注不可時間が生じるから**」である
   （決定4）。

## PoC 結果（2026-07-15・初回検証。#124）

> 🔴 **この節の 4 項目め（完全無人は不可）は 2026-08-07 に反証・撤回された。** 冒頭の「改訂」節を参照すること。
> 記述は誤りの所在を残すために削除していない。

実バイナリ `moomoo_OpenD_10.8.6818`（コマンドライン版・実行ファイル `OpenD`・設定 `OpenD.xml`）で検証:

- ✅ **ビルド成功**。ベースは `mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy`（nerdctl の docker.io 認証ヘルパ
  失敗を避けるため mcr を採用。当初の「ダウンロード URL 方式」は口座ログインが要る配布のため、**参照 tar.gz を
  ビルドコンテキストへ一時配置する取り込み方式**へ変更した）。
- ✅ **共有ライブラリ充足**（ダミー資格情報でも `error while loading shared libraries` は出ず OpenD 起動。
  追加 apt は `libgomp1`/`libglib2.0-0` のみ）。
- ✅ **実口座でログイン成功**（画像 CAPTCHA `input_pic_verify_code` ＋ SMS `input_phone_verify_code`。権限取得）。
  規制アンケート（`api.moomoo.com/v2`・口座で一度きり）完了後は OpenD が**常駐継続**する。
- 🔴 ~~**完全無人（自動再起動）は不可（確定）**。デバイス状態を **home（`/root/.com.moomoo.OpenD`）＋install
  （`/opt/opend/AppData.dat` 等）の両方**を PVC 永続化しても、**新 Pod（＝新 IP）は再び画像/SMS 検証を要求**した。
  検証は **IP/セッション依存**で、永続化では回避できない（experiment-appdata.yaml で確認）。~~
  **【❌ 反証・撤回 2026-08-07・ADR-0024 決定2】** 「新 Pod ＝新 IP」の推論が **Pod IP と egress IP を混同していた**。
  検証の対象は **egress IP** であり、Pod IP は無関係である。**「確定」と書ける観測ではなかった**
  ——単一の環境での 1 回の観測から一般的な結論を引き出しており、同日の追検証（#130）で 3 回連続して反証された。
- ➡️ ~~**決定を更新: 常駐モデル**。当初の「初回認証→永続化→無人」案は成立しないため撤回。~~
  **【⚠️ 一部訂正 2026-08-07】常駐モデルの採用は維持する**（ADR-0024 決定3 が「再起動の最小化」を維持しているため）。
  訂正するのは**理由**である。OpenD を**長時間常駐**させ、**初回のみ 1 回だけ対話で検証**
  （`kubectl attach -it deploy/opend` → `input_*_verify_code`）、**再起動を極力避ける**（安定ノード・rolling 不使用・
  単一レプリカ）。~~起動/再起動のたびに~~ 検証が要るのは**初回のみ**であり、以降は条件 (1)(2) の下で無人である。
  #13 は稼働中 `opend:11111` へ SIMULATE 接続。

## 未確定（Accepted の条件・残）

- 海外 IP（Hetzner）からの OpenD 接続可否と利用規約上の扱い（**ADR-0024 決定5-2 で依然未検証**。
  **Hetzner はクラウドであり egress IP の安定性が単一ノード前提と異なり得るため、下の「egress IP 変更時の再検証の要否」が
  直接効く**）。→ **本 IADR を `Proposed` に据え置く唯一の理由である**（IADR-0167 決定1）。
- **egress IP 変更時の再検証の要否**（ADR-0024 決定5-1・未検証）。決定1 の条件 (2) が崩れたときの挙動そのものである。
- 長期常駐の安定性・強制アップデート頻度・取引パスワードのアンロック（ADR-0024 決定5-3・未検証）。
- ~~**ADR-0002「無人運用の成立性」への回答: 限定的成立（起動時有人・以降常駐）。→ /plan-feedback で環流する。**~~
  **【✅ 解消済み 2026-08-07】環流は完了した**（`feedback/20260715_adr0002-opend-unattended-limited.md` →
  planning#212 → 質問票 第 13 回 Q2）。回答は**「限定的成立」ではなく「条件付き成立」**である（ADR-0024 決定1）。
- 口座条件・市況データ権限（SIMULATE で不要な範囲の切り分け）。

## トレードオフ・代替案

- **同梱方式**（バイナリをイメージに焼く）: 起動が単純だが EULA/再配布リスク。→ 不採用（ダウンロード方式）。
- **ホスト常駐（コンテナ化しない）**: 現行 desktop 運用のまま。k8s 一貫配備・再現性で劣る。→ dev では非採用。
- **既定有効**: 資格情報必須で fail-safe を壊す。→ 不採用（既定無効・オプトイン）。

## 影響

- 追加（試作・#124）: `opend` の Dockerfile（ダウンロード方式）・k8s manifest（chart オプトイン）・Secret 雛形。
- コード影響なし（本 IADR 時点）。moomoo アダプタ実装は #13。
- ~~Accepted 時: ADR-0002 の未決（無人運用/Hetzner）を /plan-feedback で計画へ環流する。~~
  **【一部解消 2026-08-07】無人運用は環流済み・ADR-0024 で決着。Hetzner は未検証のまま残る**（#24 / #132）。
