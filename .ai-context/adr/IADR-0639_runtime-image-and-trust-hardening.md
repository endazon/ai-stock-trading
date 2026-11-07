---
title: IADR-0639 稼働の前に実行環境とイメージを硬くする（Worker の段 3 と読み取り専用のルート・LLM 費用の停止を読めないときの段・基盤の CA を基盤の宛先だけで信頼・OpenD の基底の digest と配布物の照合・稼働のイメージから curl を外し依存の監査で high 以上を止める）
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-05, NFR-13, NFR-18, ADR-0006, ADR-0024, ADR-0074, IADR-0031, IADR-0049, IADR-0053, IADR-0582, IADR-0609, IADR-0621, IADR-0629, IADR-0631]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-05・NFR-13・NFR-18)
  - planning:projects/ai-stock-trading/07_adr/ADR-0074_llm-cost-cap-halt-covers-price-movement-decisions.md (決定 1・2 月次 LLM 費用上限の 100% で LLM の前に見送る)
---

# IADR-0639: 稼働の前に実行環境とイメージを硬くする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定（稼働前の実行環境の硬化の 1 束）。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「全部直してから稼働」）・claude（方式）

## 起点・関連

- クラウドへの移行（#1560）の稼働前の確認で見つかった低い重大度の項目のうち、実行環境とイメージの 5 つ。所有者の裁定で、稼働の前にすべて直す。
- 作業仕様書: `.ai-context/specs/20261107_runtime-image-and-trust-hardening.md`（母集合と受け入れ基準）。
- 改めるもの: IADR-0609（クラウドの Worker は段 2 で始め後で段 3 へ上げる）・IADR-0621 決定 2 / IADR-0629（基盤の CA を `SSL_CERT_DIR` でシステムの信頼ストアへ足す）・IADR-0031 / IADR-0582（費用の停止を読めないときは常に停止していない）・IADR-0049（compose の healthcheck のために稼働のイメージへ curl を入れる）。

## 決定

### 1. クラウドの Worker は稼働の前から段 3（非 root）と読み取り専用のルートで動かす

- `values-cloud.yaml` の `podSecurity.stage: 3`・`podSecurity.readOnlyRootFilesystem: true`。PSA の enforce は baseline のまま（OpenD は段 1。restricted は OpenD の段 3 も要る）。
- 書く場所は `/tmp` の emptyDir（`podSecurity.tmpSizeLimit`）だけにする。Worker が実行時に書くファイルを母集合で引いた（作業仕様書）。書くのは .NET の一時ファイル・診断の IPC（`/tmp`）と、Stage 0 の記録（PVC。クラウドでは無効）だけである。`/app` は読むだけ。DataProtection・Cookie・セッションの鍵を書く配線は無い。
- 発注執行の鍵の mount は段 3 で fsGroup ＋ 0440（IADR-0609 の既存の形）。
- IADR-0609 が段 2 から始めた理由（Stage 0 の PVC の root の既存ファイル・鍵の 0400）は、クラウドでは当たらない（PVC は無効・鍵は段 3 の形がある）。
- 戻すときは段 2 と `readOnlyRootFilesystem: false` を一緒に戻す（手順書 §9）。

### 2. 月次 LLM 費用の停止を読めないときの段（Off / Report / Enforce）

- 取引判断の関所（`ILlmCostHaltGate`）の照会を、読めた値と「読めない」（null）の 3 値で返す口（`ILlmCostHaltReader`）に分けた。REST・gRPC の実装の両方が持つ。
- 段 `CostControl:HaltQuery:UnavailableMode`（既定 Off）と猶予 `CostControl:HaltQuery:GraceSeconds`（既定 900 秒・0〜86400）。基盤の LLM ゲートウェイの呼び出しの上限の段（判定は常に行い、止めるのは Enforce だけ。当日の費用の枠を読めない状態を段 2 で送らない）と同じ考え方にした。
  - Off: 従来どおり、読めなければ停止していない（IADR-0031 / IADR-0582）。既定・ローカル。
  - Report: 倒す向きは Off と同じ。猶予を過ぎても読めなければ、Enforce なら止めることをエラーのログに出す。
  - Enforce: 読めた値は覚える。猶予の内は最後に読めた値を使い、猶予を過ぎても読めない（起動から一度も読めていないときを含む）ときは停止として扱う（LLM を呼ぶ前に見送る）。**values-cloud の既定**。
- 最後に読めた値はプロセスに 1 つ（`LlmCostHaltConfirmation`。singleton）。関所は判断ごとの scope で作る。
- 読めない段の値・猶予は起動で止める。Enforce なのに照会の宛先（`CostControl:BaseUrl` / `CostControl:Grpc`）が無いときも起動で止める（NoOp へ黙って倒さない）。
- LLM を使わない保護（損切り・利用者の手仕舞い・閉場前の手仕舞い・維持率割れの自動縮小）はこの関所を通らないので止まらない。
- 猶予を 15 分にした理由: 月次の予算は緩変で、短い不達では超えにくい（IADR-0031 の論拠）。一方で、費用統制が落ちたまま判断が LLM を呼び続ける時間に上限を置く。

### 3. 基盤の CA は基盤の宛先だけが信頼する

- 基盤の CA を `SSL_CERT_DIR` でシステムの信頼ストアへ足すのをやめた（chart は既定で描かない）。外部 API（Discord・Finnhub ほか）の TLS は公開の CA だけで検証する。
- 基盤の宛先は、アプリが CA の場所（`PlatformInfraCa:CertificatePath`）から宛先ごとに検証する:
  - 認可サーバー: JwtBearer のメタデータ・鍵の取得（Backchannel）、トークンの取得の HttpClient（サービスのトークン・基盤のレルムのトークン・Discord の所有者のトークン・KB のトークン）。
  - ブローカの amqps: RabbitMQ.Client の検証のコールバック（名前の不一致は従来どおり拒む）。
  - LLM ゲートウェイ: 従来どおり（IADR-0621 決定 5）。
  - DB: 従来どおり接続文字列の Root Certificate（この CA だけを信頼する）。
- 判定の部品（`PlatformInfraCaTrust`）は全サービスが通る共通の配線（PlatformShim）へ移した（共有の Infrastructure は認可・ブローカの配線から参照できないため）。名前空間は `AiStockTrading.TestSupport.PlatformShim.Foundation.Tls`。
- トークンの取得の HttpClient は主ハンドラを差し替えず、既定の主ハンドラ（HttpClientHandler / SocketsHttpHandler）の検証だけを足す。CA の場所が無ければ何も変えない（ローカル・既定の挙動はそのまま）。試験の偽の主ハンドラ・既に検証を持つハンドラは触らない。
- 段階の入れ方: 切り戻しの値 `global.infraCa.addToSystemTrust`（既定 false）を置いた。true にすると従来どおり `SSL_CERT_DIR` を描く（外部 API でも基盤の CA を信頼する）。

### 4. OpenD のイメージの基底を digest で固定し、配布物を記録の sha256 で照合する

- `deploy/opend/Dockerfile` の基底 `runtime-deps:8.0-jammy` を manifest list の digest で固定し、Dependabot の docker に `/deploy/opend` を足した。
- 配布物（tar.gz）の sha256 をリポジトリの記録 `deploy/opend/opend-tarball.sha256`（版ごとに 1 行。レビューを経て入る）と照合する。`scripts/opend-build.sh` は記録に無い版・値の違う配布物ではビルドしない。Dockerfile も展開の前に同じ値（`OPEND_TARBALL_SHA256`）で照合し、無い・合わないときは止まる。

### 5. 稼働のイメージから curl を外し、依存の監査で high 以上を止める

- `backend/Dockerfile` の稼働段で curl を入れない。k8s の probe は kubelet の httpGet（イメージの道具を使わない）。compose の healthcheck は基底イメージの bash の `/dev/tcp` で HTTP/1.0 の GET を送り、状態行の 200 を見る。
- ルートの `Directory.Build.props` に NuGet Audit を置いた（条件なし＝単独・submodule の両方に効く）: `NuGetAuditMode=all`（推移的な依存も）・`NuGetAuditLevel=low`・`WarningsAsErrors` に `NU1903`（high）・`NU1904`（critical）。build・test・publish・イメージのビルドの復元が止まる。moderate 以下は警告のまま。

## 却下した案

- 費用の停止を読めない瞬間に停止として扱う（猶予なし）: 費用統制の短い再起動・3 秒のタイムアウト 1 回で判断が止まる。猶予 0 は値で選べる。
- 基盤の CA に Name Constraints を付ける: 基盤（MSP）の CA の作り直しが要り、本リポジトリの範囲を超える。宛先ごとの信頼で同じ効果（外部 API で信頼しない）を本リポジトリだけで得られる。
- 稼働のイメージとは別に curl を持つ dev 用の段を作る: compose と稼働でイメージが分かれ、同じイメージを試すという compose の意味が薄れる。
- `packages.lock.json` と `RestoreLockedMode` を同じ変更で入れる: 全プロジェクトのロックファイルの生成と Dependabot の更新の形の確かめが要り、変更が大きい。監査は推移的な依存も見るので、既知の脆弱性の取り込みは止まる。残る危険へ回す。

## 結果

- 稼働の前から、Worker は非 root・読み取り専用のルート、費用統制が落ちれば猶予の後に LLM の判断を見送り、外部 API の TLS は公開の CA だけで検証する。
- compose のサービスは curl を使わない healthcheck で動く。OpenD のイメージは固定した基底と照合した配布物からだけ作られる。

## 残る危険

- **OpenD の配布物の公式の値**: 作業の環境から、公式の配布元が sha256 を示しているかを確かめられなかった。示していなければ、初めて取得した版の値は取得したときの配布物を正とするしかない（初回の信頼）。記録をリポジトリに置くことで、取り直し・別の担当のビルドが同じ物であることは確かめられる。公式の値があれば記録の前に突き合わせる（手順書 §3）。
- **OpenD の基底の .NET 8 の期限**: `runtime-deps:8.0-jammy` は .NET 8 の支援の終わり（2026-11）の後、基底の更新が止まる。OpenD は .NET を使わず Ubuntu 22.04 の共有ライブラリだけを要るので、基底を別の Ubuntu 22.04 の最小のイメージへ替える検討が要る（実 OpenD での確かめが要るので本決定では替えない）。
- **推移的な依存の固定**: ロックファイルが無いので、イメージのビルドの時点の推移的な依存の版は固定されない（監査は通る版に限る）。
- **報告書の LLM**: 報告書のサービスは月次の費用の停止を読まない（ADR-0074 の対象は判断）。後ろ盾は基盤のゲートウェイの当日の費用の上限と提供者の月の上限（オーナーの設定）である。
- **段 3 の実機の確かめ**: Worker の段 3 と読み取り専用のルートは描画と試験で固定したが、クラウドの実機の起動は初日に確かめる（手順書 §9）。
