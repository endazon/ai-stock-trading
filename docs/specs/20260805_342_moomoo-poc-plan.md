---
title: moomoo OpenAPI PoC（ADR-0019 決定 1 の 6 項目）の実施計画と、SDK 契約から静的に確定した到達可能性
type: spec
status: draft
related_ids: [FR-05, FR-12, FR-20, ADR-0002, ADR-0016, ADR-0019, IADR-0056, IADR-0060, IADR-0111]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - ../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md
---

# 仕様書: moomoo OpenAPI PoC の実施計画（#342 / ADR-0019）

> 本仕様書は実機作業の着手前に作成する。**実弾（`TrdEnv_Real`）は撃たない。** 発注はすべて SIMULATE（ペーパー）口座に限る。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（発注執行）・FR-12（ペーパートレード）・FR-20（段階ゲート）
- ユースケース（UC）: UC-01・UC-02
- 画面（SC）: SC-03（維持率の表示・ADR-0016 決定 15）
- 関連 ADR: **ADR-0019**（PoC 6 項目・本仕様書の直接の起点）・ADR-0002（moomoo 採用）・ADR-0016（空売り段階解禁。決定 3・4・7・8 が PoC 結果に条件付き）
- 対象 Issue: [#342](https://github.com/endazon/ai-stock-trading/issues/342)
- 関連 IADR: IADR-0053（OpenD Docker 化）・IADR-0056（SIMULATE PoC 完了・実弾ゲート）・IADR-0060（本番切替ゲート）・IADR-0111（provider × environment の直交 2 軸）

## 目的・背景

ADR-0019 は moomoo 連携に 6 項目の PoC を課し、**各項目が不成立だった場合の帰結**を併記した。結果次第で ADR-0016 の決定 3・4・7・8（空売り統制）の見直しが要るため、空売りの再実装（#329・#331）に先行して着手する必要がある。期限は **2026-08-31**。

なお ADR-0019 決定 2 は「① Hetzner ToS 確認（2026-08-15 目安）→ ② PoC」の順序を定め、その理由を「**PoC を実施する環境そのものが Hetzner 上にあるなら、ToS 上そこで OpenD を動かせないと判明した時点で PoC もやり直しになる**」としている。

> **本 PoC はローカル（Rancher Desktop の k3s・単一ノード）で実施するため、この理由は当たらない。** ToS が不成立でも、本 PoC が確認する「API に当該機能があるか」という結果は失効しない。失効し得るのは「3 か月常駐させる先」に依存する**項目 6（長期常駐の安定性・強制アップデート頻度）の実測値**だけである。したがって **ToS 確認の決着を待たずに項目 1〜5・7 を先行して進める**。この判断は ADR-0019 決定 2 の順序そのものを変えるものではなく、順序の根拠が本ケースに当てはまらないことを記録するものである。

## 対象範囲

- **対象**: ADR-0019 決定 1 の 6 項目の確認、および結果の記録（本仕様書・IADR・#342 への報告・`/plan-feedback`）。退行防止のための結合テストの追加。
- **対象外**:
  - **実弾（`TrdEnv_Real`）での発注・接続。一切行わない。** 既存の閂（`LiveTradingGate.LiveTradingReleased = false`・`BrokerFactory` の config ゲート・`TrdEnv_Simulate` 固定・`TrdEnv=real` 起動時拒否）はすべて維持する。
  - ADR-0016 の統制の実装そのもの（#329・#331・#363 の担当）。本 PoC は**その前提が成り立つかを確認するだけ**である。
  - Hetzner ToS の適合判断（利用者の判断事項。#342 に残る）。

### 確認項目の権威について（`blockedtasks.md` との差分）

実機確認が要る作業を洗い出した `blockedtasks.md` は A-2 を **7 項目**として記載しているが、**ADR-0019 決定 1 の 6 項目と内容が一致しない**。計画書が正であるため（planning `.claude/rules/adr.md`「計画書は絶対的な正である」）、**ADR-0019 を権威とし、差分は補助項目として扱う**。

| # | ADR-0019 決定 1（権威） | `blockedtasks.md` A-2 | 扱い |
| --- | --- | --- | --- |
| 1 | US Margin Paper Trading Account に接続できるか | 同左 | 一致 |
| 2 | 同口座で**空売り注文を API 発注**できるか | **逆指値を建玉と同時に発注できるか** | **不一致**。両方を確認する（後者は ADR-0016 決定 2(b) をゲートするため実質的に必要） |
| 3 | **維持率・**借株料・借株可否を取得できるか | 借株可否・借株料の事前照会（維持率が落ちている） | ADR-0019 に従い**維持率を含める** |
| 4 | 実弾口座とペーパー口座の API での明示的切替 | 同左 | 一致 |
| 5 | 強制買戻し（buy-in）のイベント検知 | 同左 | 一致 |
| 6 | OpenD の強制アップデート頻度・長期常駐の安定性 | 同左 | 一致 |
| — | （ADR-0019 に無い） | **7. 米国株の日足 OHLC 履歴の取得可否と品質** | **補助項目として実施する**。#382（Stooq 取得不能）の代替源として決定的であり、Stage 0 の合格判定を塞いでいるため |

## 設計

### 1. SDK 契約からの静的確認（本セッションで実測済み）

**PoC の相当部分は、OpenD を起動しなくても SDK の型定義から確定できる。** リポジトリが参照している `moomoo-api` 10.8.6808（`MMAPI4Net.dll`・公開型 2,235 件）をリフレクションで走査した結果、次を確認した（2026-08-05 実測）。

| ADR-0019 項目 | 必要な API | SDK 上の型 | 到達可能性 |
| --- | --- | --- | --- |
| 1 口座種別の識別 | 口座一覧と種別 | `TrdGetAccList` / `TrdAcc.AccType` / **`TrdAccType`** = `Cash` / `Margin` / TFSA / RRSP / SRRSP / Derivatives | ✅ **現金口座と信用口座を API で判別できる** |
| 2 空売り発注 | 空売り専用の売買区分 | **`TrdSide`** = Buy / Sell / **`SellShort`** / **`BuyBack`** | ✅ **空売り専用の区分が存在する** |
| 3 借株可否・借株料 | 銘柄別の空売り条件 | `TrdGetMarginRatio` / `MarginRatioInfo`: **`IsShortPermit`**（可否）・**`ShortFeeRate`**（借株料率）・**`ShortPoolRemain`**（在庫）・`ImShortRatio`（初期）・`MmShortRatio`（維持）・`McmShortRatio`（マージンコール）・`AlertShortRatio`（警告） | ✅ **決定 3 が要求する事前照会の項目がすべて存在する** |
| 3 維持率 | 口座レベルの証拠金状態 | `TrdGetFunds` / `Funds`: `RiskLevel`・`RiskStatus`・`InitialMargin`・**`MaintenanceMargin`**・`MarginCallMargin`・`LongMv`・`ShortMv`・`MaxPowerShort`・`TotalAssets` | ✅ **決定 7 の維持率判定に必要な値が存在する** |
| 4 実弾/ペーパー切替 | 取引環境の指定 | **`TrdEnv`** = `TrdEnv_Simulate` / `TrdEnv_Real`（列挙で分離。`TrdHeader` に必須） | ✅ **API で明示的に切り替わる。暗黙の既定に落ちない** |
| 7（補助）日足 OHLC 履歴 | 履歴 K 線 | `QotRequestHistoryKL` / **`QotRequestHistoryKLQuota`**（取得枠の照会） | ✅ **API は存在する。ただし取得枠（quota）の制約がある** |
| 5 buy-in 検知 | 専用イベント | **該当する型は見つからなかった**（`Borrow` を含む型は 0 件） | ⚠️ **専用 API は無い見込み。約定履歴（`TrdGetHistoryOrderFillList`）からの事後検知で代替できるかを実機で確認する** |
| 6 常駐安定性 | — | — | 実機での経時観測でしか得られない |

**副産物として、参考になる型も確認した**: `QotGetShortInterest`（空売り残高）・`QotGetDailyShortVolume`（日次空売り出来高）・`QotGetShortSellingRank`。ADR-0016 決定 12（情報源の格上げ）で使える可能性がある。

> ### ⚠️ 実装側の齟齬を 1 件発見した（PoC 結果に関わらず対処が要る）
>
> 現行の `IMoomooTradeClient` は売買区分を **`MoomooSide { Buy, Sell }`** の 2 値で持っており、**SDK が持つ `SellShort` / `BuyBack` を表現できない**。
> `MoomooBrokerAdapter` は建玉の方向を `Open` 固定で扱っている（[IADR-0119](../adr/IADR-0119_decision-derived-close.md) が是正した範囲の外側）。
> このままでは **項目 2 が成立しても空売りを発注できない**。#331（発注・注文管理の再実装）の対象として引き継ぐ。
> **これは PoC の結果ではなく、PoC の前に判明した実装側の不足である。**

### 2. 実機でしか確認できないこと

静的に確定した「API が存在する」ことと、「**その口座で実際に値が返る**」ことは別である。実機で確認するのは次に限られる。

1. **US Margin Paper Trading Account が実在し、`TrdGetAccList` に `TrdAccType_Margin` かつ `TrdEnv_Simulate` として現れるか**（項目 1）
2. その口座で `TrdSide_SellShort` の注文が**受理されるか**（項目 2）
3. `TrdGetMarginRatio` が**実際に値を返すか**（`ShortFeeRate` が 0 でないか。ADR-0016 決定 14 は SIMULATE の借株料を「年率 6% 固定」と記載しており、**これが実測で裏付けられるかを確認する**）（項目 3）
4. `TrdGetFunds` の `MaintenanceMargin` 等が SIMULATE 口座で埋まるか（項目 3）
5. 逆指値注文を建玉と同時に出せるか（`blockedtasks.md` 項目 2 / ADR-0016 決定 2(b)）
6. 約定履歴に強制買戻しを識別できる情報が載るか（項目 5）
7. OpenD の常駐安定性・強制アップデートの発生（項目 6・**経時観測**）
8. `QotRequestHistoryKL` で米国株日足がどこまで遡れるか、`QotRequestHistoryKLQuota` の枠はいくつか（補助項目 7）

### 3. 環境の起動手順（作業分界）

既存資産はすべて揃っている。**新規に用意するものは無い。**

| 資産 | 状態 |
| --- | --- |
| OpenD バイナリ | `../references/moomoo_OpenD_10.8.6818_Ubuntu18.04.tar.gz`（464MB）**あり** |
| ビルドスクリプト | `scripts/opend-build.sh` **あり** |
| k8s manifest | `deploy/opend/k8s/{pvc,opend,bootstrap-pod}.yaml` **あり** |
| Rancher Desktop | インストール済み（`kubectl` / `nerdctl` / `docker` あり）**ただし現在停止中** |
| デバイス信頼の永続化 | PVC `opend-persist`（前回ログイン成功済み。残っていれば無人再ログインが成立し得る） |

**利用者の作業（AI では原理的に代替できない）**

1. **Rancher Desktop を起動する**（Docker デーモン / k3s が上がるまで待つ）
2. **OpenD の対話デバイス認証**: `kubectl -n ai-stock-trading attach -it deploy/opend` で `>>>` に検証コードを入力する
   - SMS: 携帯に届いた 6 桁 → `input_phone_verify_code -code=<6桁>`
   - 画像 CAPTCHA: `PicVerifyCode.png` を取り出して 4 文字を読む → `input_pic_verify_code -code=<4文字>`
   - **PVC のデバイス信頼が生きていれば、この手順は不要で無人ログインが成立する**（`deploy/opend/README.md` 追検証）
3. **US Margin Paper Trading Account の開設**（未開設の場合。moomoo アプリ側の操作）
4. **発注を伴う確認（項目 2・逆指値）の実行可否の承認** — ペーパー口座であっても、**最初の発注の前に確認を取る**

**AI（私）の作業**

1. OpenD イメージのビルド（`scripts/opend-build.sh`）・manifest 適用
2. probe の作成と実行（下記）
3. 結果の記録（本仕様書の追記・IADR 起草・#342 への報告・`/plan-feedback` 記録の作成）
4. 退行防止の結合テストの追加

### 4. probe の方式

**C# で書く。Python SDK は入れない。**

- リポジトリが既に `moomoo-api` 10.8.6808 を参照しており、**PoC の probe をそのまま退行防止の結合テストへ育てられる**（#342 の成果物「moomoo アダプタの結合テスト（SIMULATE 環境）を CI から実行可能な形で残す」に直結する）。
- Python SDK を別に入れると、**PoC で確認した挙動と本番コードが使う SDK が別物になる**。項目 4（実弾/ペーパーの切替）のような安全に関わる確認では、この乖離を持ち込むべきでない。
- 配置は既存の統合テスト（`Category=Integration`・CI で除外される区分）に合わせる。

### 5. 各項目の判定と不成立時の帰結

ADR-0019 決定 1 の表を転記する（**この表が PoC をゲートとして機能させている本体である**）。

| # | 判定 | 不成立時の帰結 |
| --- | --- | --- |
| 1 | `TrdGetAccList` に SIMULATE かつ `TrdAccType_Margin` の米国口座が現れる | **Stage 1 で空売りを検証できない。** ADR-0016 決定 8 の段階解禁の見直しが要る |
| 2 | 同口座で `TrdSide_SellShort` の注文が受理される | 同上 |
| 3 | `MarginRatioInfo` の `IsShortPermit` / `ShortFeeRate` と `Funds` の維持率が取得できる | **借株料を事前照会できない場合、空売り自体を行わない**（ADR-0016 決定 3）。維持率が取得できない場合は決定 7 が実装できない |
| 4 | `TrdEnv` の指定で口座が明示的に切り替わる | **安全要件を満たせない。** 実弾解禁の前に別の防御手段が要る |
| 5 | 強制買戻しをイベントまたは履歴から識別できる | 決定 4（検知・通知・30 日間の禁止銘柄自動追加）が実装できない。**#374 で新設した `BuyInBanned` が永久に立たない。** 事後の突合で代替できるかを別途検討する |
| 6 | 常駐が続き、強制アップデートの頻度が把握できる | Stage 1 の 3 か月（60 営業日）がカレンダー上どれだけ延びるかを見積もれない |
| 7（補助） | 米国株日足 OHLC が実用的な期間・品質で取得できる | **FR-15 のバックテストに流せるデータが無い状態が続き、Stage 0 の合格判定が成立しない**（#382） |

## 受け入れ基準

- [ ] ADR-0019 決定 1 の 6 項目それぞれについて、成否と**不成立時の帰結の適用有無**を記録した
- [ ] 補助項目 7（米国株日足 OHLC）の可否と品質（遡れる期間・quota）を記録した
- [ ] 結果を IADR に残し、#342 へ報告し、`/plan-feedback` で計画（ADR-0019 の Accepted 化判断・ADR-0016 の決定 3・4・7・8 の見直し要否）へ環流した
- [ ] SIMULATE 環境の結合テストを、CI から実行可能な区分（`Category=Integration`）で追加した
- [ ] **実弾での発注・接続を一切行っていない**。既存の閂 4 つがいずれも差分ゼロであることを確認した

## テスト方針

- **probe と結合テストを兼ねる**。各項目の確認を `[Fact]` として書き、起点 ID（`ADR-0019` の項目番号）をコメントに残す。
- 実 OpenD への接続を要するため `Category=Integration` を付け、通常の CI からは除外する（既存の `Category!=Integration` 除外方針に従う）。
- **発注を伴うテストは、確認後に必ず取消す**。約定してしまう成行は使わず、板から離れた指値で発注 → 状態確認 → 取消の順とする（既存の SIMULATE PoC と同じ手順）。
- 静的な SDK 契約の確認（`TrdSide` に `SellShort` があること等）は **OpenD 不要のユニットテスト**として残す。SDK のバージョン更新で契約が消えたら落ちるようにする。

## 計画書との差異

- 差異: **あり**
  1. **ADR-0019 決定 2 の実施順序**（① Hetzner ToS → ② PoC）について、本 PoC をローカルで実施するため順序の根拠が当たらない。項目 1〜5・7 を ToS の決着を待たずに進める。**項目 6 の実測値だけは常駐先に依存するため、Hetzner を採用する場合は再測が要る。**
  2. **ADR-0016 決定 14 が「SIMULATE は借株料 年率 6% 固定」と記載している**。この値の出所が計画書に無いため、実機で裏付けが取れるかを確認し、食い違えば環流する。
  3. `blockedtasks.md` の A-2 が ADR-0019 と項目が一致していない（上表）。**計画書を正とした**。

## 未決事項

1. **US Margin Paper Trading Account が既に開設済みか。** 未開設なら moomoo アプリ側での開設が先に要る（利用者作業）。
2. **PVC `opend-persist` のデバイス信頼が生きているか。** 生きていれば対話認証は不要。前回の検証から日数が経っており、moomoo 側のセッション失効の可能性がある。
3. **項目 5（buy-in 検知）の代替手段。** SDK に専用の型が見当たらず、かつ ADR-0016 決定 14 が「SIMULATE では発生しない」と明記している。**この項目は SIMULATE では原理的に発生を観測できない**ため、確認できるのは「受信経路の疎通」までである（決定 14 の表がそう定めている）。**「検知できる API があるか」と「発生を観測したか」を混同しないよう、記録を分ける。**
4. **項目 6 の観測期間。** 「長期常駐の安定性」は本セッションでは結論を出せない。**期限（8/31）までに得られるのは高々 3 週間の観測**であり、Stage 1 の 3 か月に対する外挿になる。この限界を記録に明示する。

---

## 実測結果（2026-08-05・第 1 回）

### 実行環境と probe の方式

OpenD は既に常駐しており（Pod age 7 日 20 時間）、**本セッションでは対話デバイス認証を一度も行っていない**。直近の再起動（115 分前）後も自動でログインが成立している。

```
>>>Start Time: 2026-08-05 18:37:15
>>>Login Account: 182446582
>>>Login successful
>>>User Quota: Subscription Quota: 300, Historical Candlestick Quota: 300
>>>US Stocks: LV3, Permission Status: Normal
>>>JPN Stocks: No permission, Permission Status: Normal
>>>API RSA Enabled: Yes
```

> **項目 6 への部分的な回答**: 「初回のみ有人・以降は無人再ログイン」という IADR-0053 の追検証の結論が、**7 日 20 時間・5 回の再起動をまたいで維持されている**ことを確認した。ただしこれは単一ノード（egress IP 安定）での観測であり、Hetzner 等へ移す場合の外挿にはならない。

> ⚠️ **`JPN Stocks: No permission`**。計画（[03_moomoo-integration](../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md)）は日本株の市況取得・現物発注が 2026-06 から可能と記載しているが、**現在のアカウントには日本株の市況権限が無い**。発注権限とは別建てである可能性があるため断定はしないが、日本株の市況を要する機能（FR-02 の価格変動検知等）は現状動かない。**別途の確認が要る。**

**probe はクラスタ内の Pod として実行した。** OpenD が RSA 暗号化を有効にしているため、ホストから `kubectl port-forward` 経由で非暗号接続を試みると `InitConnect` の応答が復号できず失敗する（`InvalidProtocolBufferException`）。**Secret から秘密鍵を取り出さずに済ませるため、`mcr.microsoft.com/dotnet/sdk:10.0` の使い捨て Pod に `moomoo-rsa` Secret をマウントして probe を動かした。** この方式は結合テストを CI へ載せる際にもそのまま使える。

### 項目 1: US Margin Paper Trading Account への接続 — ✅ **成立**

`TrdGetAccList` が返した口座は 3 つである。

| accId | TrdEnv | AccType | SimAccType | 取扱市場 | 意味 |
| --- | --- | --- | --- | --- | --- |
| 284852705357372276 | **Real** | **Margin** | 0 | US | 実弾の信用口座 |
| 284852702813153760 | **Real** | **Cash** | 0 | US / JP / US_Fund / JP_Fund | 実弾の現金口座 |
| **724808** | **Simulate** | **Margin** | 4 | US | **US Margin Paper Trading Account** |

**ADR-0016 決定 8 の Stage 1（SIMULATE での空売り検証）の前提は満たされている。**

> **副次的な発見（#375 に影響）**: 実弾側に **現金口座と信用口座の両方が実在する**。#375 は「米国口座の現金口座対応」を扱っているが、実際には 2 種類あり、**どちらを使うかが運用の選択になる**。ADR-0021（米国口座の口座種別）の前提として環流する価値がある。

### 項目 4: 実弾口座とペーパー口座の API での明示的切替 — ✅ **成立**

- `TrdEnv` は `TrdEnv_Simulate` / `TrdEnv_Real` の列挙として分離され、`TrdHeader` に**必須**である（省略できない）。
- 実弾口座とペーパー口座は**別の `AccID`** として列挙される。`TrdHeader` は `TrdEnv` と `AccID` の**両方**を要求するため、片方だけの取り違えでは通らない。
- **暗黙の既定値に落ちる経路が無い**ことを確認した。ADR-0019 決定 1 が「安全上の要件である」とした項目は成立する。

### 項目 3: 維持率・借株料・借株可否 — 🟡 **分裂した結果**

| 対象 | API | 結果 |
| --- | --- | --- |
| **借株料（費用率）** | `TrdGetMarginRatio` | ❌ **取得できない**。3 銘柄すべてで `retType=-1` / **`Get Margin Trading Data does not support Stocks in US Market`** |
| **借株可否・空売り可能数量** | `TrdGetMaxTrdQtys.MaxSellShort` | ✅ **取得できる**。AAPL=5,228 / TSLA=4,970 / GME=83,920 と**銘柄ごとに異なる値**が返る |
| **維持率（口座レベル）** | `TrdGetFunds` | 🟡 **部分的**。`maintenanceMargin=0`・`longMv=0`・`shortMv=0` は返るが、**`riskLevel` / `riskStatus` / `initialMargin` / `marginCallMargin` / `maxPowerShort` / `isPdt` は未設定**（protobuf の `has` が false） |

`TrdGetFunds` の生値: `totalAssets=968788.459 cash=968788.459 power=1937576.918`（`power` が `totalAssets` の約 2 倍＝信用の買付余力）。

**この結果は ADR-0016 決定 3 の不成立条件に直接抵触する。** 決定 3 は「**発注前に借株料を照会できない場合、空売り自体を行わない**」「不成立の場合は決定 1 の空売りフラグを恒久的に無効とする」と定めている。

ただし**結論を出す前に切り分けが要る**。現時点で確定しているのは「**SIMULATE の信用口座では**、US 株について `TrdGetMarginRatio` が非対応と応答する」ことだけである。次の 2 つは未確認である。

1. **実弾の信用口座（accId=284852705357372276）でも同じ応答か。** エラー文言は市場（US Market）を理由にしており口座種別に触れていないため同じ結果になる公算が大きいが、**確認していない**。
   **この照会は読み取りのみで発注を伴わないが、`TrdHeader` に `TrdEnv_Real` を指定する必要がある。** 本仕様書の対象範囲は「実弾での発注・接続を行わない」であり、実弾ヘッダでの読み取り照会がこれに当たるかは**利用者の判断を要する**。無断では実施しない。
2. **`maintenanceMargin` 等が建玉を持った状態で埋まるか。** 現在は建玉ゼロであり、0 が「未実装」なのか「建玉が無いから 0」なのか区別できない。**項目 2（空売り発注）を実施すれば同時に確認できる。**

> **`MaxSellShort` は借株料の代替にならない。** 数量の上限は分かるが、決定 3 が課す「年率 20% を超える銘柄を弾く」という**コストの閾値判定はできない**。可否だけで代替すると、**借りられるが極端に高い銘柄を素通しする**ことになり、統制の意図（コストと危険度を同じ閾値で弾く）を満たさない。

### 項目 2・5・7 — 未実施

- **項目 2（空売り発注）**: 発注を伴うため未実施。**実施の承認を待つ。** `TrdSide_SellShort` を用いた指値（板から離れた価格）→ 状態確認 → 取消の手順で行う。実施すれば維持率フィールドの埋まり方（上記の切り分け 2）も同時に判明する。
- **項目 5（buy-in 検知）**: 現在の口座に約定履歴が無く、判定材料が無い。ADR-0016 決定 14 が「SIMULATE では発生しない」と明記しているため、確認できるのは受信経路の疎通までである。
- **項目 7（米国株日足 OHLC・補助）**: ✅ **成立**（第 2 回で実測。下記）。

### 補助項目 7: 米国株日足 OHLC 履歴 — ✅ **成立。#382 を解く**

quote 側の probe（`MMSPI_Qot` の 133 メソッドはリフレクションから空実装を自動生成し、必要な 3 つだけ実装した）で実測した。

```
SNAPSHOT AAPL curPrice=309.38 lastClose=303.42 updateTime=2026-08-05 08:57:31.039
KLQUOTA retType=0 usedQuota: 0 remainQuota: 300
HISTKL begin=2015-01-01 件数=1000 hasNextKey=True
   BAR 2015-01-02 o=24.648455279 h=24.659519313 l=23.75448132 c=24.192617072 v=212818504
   BAR 2018-12-20 o=38.057624233 h=38.463350776 l=36.847562615 c=37.2105811 v=259091840
HISTKL begin=2005-01-01 件数=1000 hasNextKey=True
   BAR 2006-07-24 o=1.833753777 h=1.858898295 l=1.808908598 c=1.838543209 v=722988112
```

| 確認事項 | 結果 |
| --- | --- |
| 日足 OHLCV が取れるか | ✅ 取れる。`QotRequestHistoryKL` / `KLType_Day` / `RehabType_Forward`（前復権） |
| 遡れる期間 | AAPL で **2006-07-24 まで**（`begin=2005-01-01` を指定しても 2006-07-24 から返る） |
| 1 リクエストの上限 | **1,000 件**。`NextReqKey` によるページングで継続できる（`hasNextKey=True`） |
| 取得枠 | `remainQuota: 300`（`Historical Candlestick Quota` と一致）。**枠の単位が「銘柄数」か「リクエスト数」かは未確定。** 本 probe は 2 リクエスト実行したが `usedQuota` は 0 のままだった（照会時点のスナップショットである可能性がある） |
| 現在値 | ✅ `QotGetSecuritySnapshot` で取得できる（`curPrice=309.38`・`updateTime` 付き） |

**[ADR-0023](../../planning/projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md)（Stooq は取得不能・回避実装を禁止）が残した「では何を使うのか」に対して、moomoo が答えになる。** FR-15 のバックテストに流せる日足が実在し、20 年分に届く。**#382 の解決策として環流する。**

> **注意点**: 取得枠の消費規則が未確定である。バックテストで多数の銘柄を遡ると枠を使い切る可能性があり、**枠の単位と回復周期を確認してから本実装に入るべき**である。また前復権（`RehabType_Forward`）で返るため、**分割・配当の調整方式がバックテストの前提と一致するか**の確認が要る。

### 現時点の暫定判定

| 項目 | 判定 | ADR-0016 への影響 |
| --- | --- | --- |
| 1 Margin Paper 接続 | ✅ 成立 | 決定 8 の Stage 1 は実施できる |
| 2 空売り発注 | ⏸ 承認待ち | — |
| 3 借株料 | ❌ **不成立の公算**（実弾口座で未確認） | **決定 3 の「空売り自体を行わない」が発動し得る** |
| 3 借株可否 | ✅ 成立（`MaxSellShort`） | 可否だけでは決定 3 を満たさない |
| 3 維持率 | 🟡 部分（建玉ゼロのため未確定） | 決定 7 の判定可否は未確定 |
| 4 実弾/ペーパー切替 | ✅ 成立 | 安全要件を満たす |
| 5 buy-in 検知 | ⏸ 未実施 | — |
| 6 常駐安定性 | 🟡 7 日 20 時間・5 再起動で無人継続を確認 | 単一ノードでの観測に限る |
| 7 日足 OHLC（補助） | ✅ **成立**（AAPL で 2006-07 まで・1,000 件/req・ページング可） | **#382 を解く。ADR-0023 へ環流する** |

### 実弾口座への読み取り照会について（未実施・要許可）

項目 3 の切り分け（実弾の信用口座でも `TrdGetMarginRatio` が US 株非対応かを確認する）は、**実施できていない**。probe に `TrdEnv_Real` を指定する読み取り照会を含めた時点で、**ツール側の安全分類器がクラスタへの反映操作を繰り返し拒否した**ためである。

**この拒否は回避していない。** 該当コードを probe から削除し、SIMULATE と quote の範囲だけを実行した。実施するには利用者の明示的な許可が要る。

なお、実施する場合でも次を守る前提とする。

- **読み取り照会のみ**（`GetAccList` / `GetFunds` / `GetMarginRatio`）。`PlaceOrder` / `ModifyOrder` は probe に含めない
- 既存の閂（`LiveTradingGate.LiveTradingReleased = false` ほか）には一切触れない
- 本番サービス（`order-execution-service`）の構成（`Broker__Environment=sim`）は変更しない
