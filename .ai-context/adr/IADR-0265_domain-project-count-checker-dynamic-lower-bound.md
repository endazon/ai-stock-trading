---
title: IADR-0265 DomainLayerDependencyTests の下限を実ツリーから動的に導く（サービス数のハードコードをやめる）
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0259, IADR-0263, IADR-0264, IADR-0258, IADR-0256]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
---

# IADR-0265: `DomainLayerDependencyTests` の下限を実ツリーから動的に導く

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-08-29
- 決定者: endazon（方針・[IADR-0259](IADR-0259_single-project-vsa-structure.md) の利用者裁定の継承）/
  Claude Code（移送の実施と、下限の動的化の起案）

## 起点・関連

- 起点 ID: **`NFR`（無採番）**。構造移送・検査器の改修＝メタ作業であり、
  `.claude/rules/traceability.md`「起点 ID の種別」の無採番許容ケース 2 に当たる
  （[IADR-0259](IADR-0259_single-project-vsa-structure.md) が確定済みの判断を継承する。環流はしない）。
- 関連する実装仕様書:
  [20260829_w11s4c_costcontrolservice-vsa](../specs/20260829_w11s4c_costcontrolservice-vsa.md)
- 上流: [IADR-0264](IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 5（「アーキテクチャ検査の
  下限は『サービス数』ではなく『実測』で読む」——本 IADR はこの決定が予告した宿題を実装する）・
  [IADR-0258](IADR-0258_structure-aware-checkers-dual-layout.md)（構造依存の検査器を「樹形の実在から
  動的に導く」設計の先例）・[IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md)
  （Domain 依存規律の二重化検査の全体設計）

## コンテキストと課題

`AiStockTrading.Architecture.Tests` の `DomainLayerDependencyTests.Domain_プロジェクトの探索が
空振りしていない()` は、`RepositoryLayout.DomainProjectFiles`（`*.Domain.csproj` の実測）が
**探索の失効で 0 件に落ちていないこと**を守るメタ検査である。この下限は 1 本目（AuditService・
[IADR-0263](IADR-0263_auditservice-vsa-migration-first-of-eleven.md)）着手前は `9`、2 本目
（ConfigurationService・[IADR-0264](IADR-0264_configurationservice-vsa-and-client-abolition.md)）で
`9 → 8` へ手で下げた。**この下限は、単一プロジェクト＋VSA への移送（[IADR-0259](IADR-0259_single-project-vsa-structure.md)）
が 1 サービス進むごとに、機械的に 1 ずつ減る値である。**

残り 9 サービス（CostControlService を含む）の移送でも同じ操作を繰り返すと、**9 回の移送それぞれで
「今回はいくつ減らすべきか」を毎回手で数え直し、ハードコードした数値を書き換える**運用になる。
これは 2 種類の事故を生む機会を 9 回分作る。

1. **減らす数を間違える。** ConfigurationService の移送（[IADR-0264](IADR-0264_configurationservice-vsa-and-client-abolition.md)
   決定 2）のように、1 サービスの移送が **`Domain/` の消滅を伴わない場合もある**
   （型が集約フォルダ側にしか無く、そもそも Domain 層を持たないサービスの移送では下限は動かない）。
   逆に、複数サービスを 1 PR で束ねる将来の変更があれば 2 以上減ることもあり得る。
   「1 移送 = 1 減算」という単純な決め打ちルールでは対応できない。
2. **下げる操作自体を忘れる、または既に下がっている値へさらに重ねて下げる。** 手書きの数値は
   PR をまたいだ履歴を追わないと現在値が分からず、**前の PR が下げ忘れていた場合に気付けない**。

## 検討した選択肢

### 論点: 下限の出し方

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A1: 従来どおり手で数値を書き換え続ける | 各移送 PR で下限の整数リテラルを直接編集する | ✕ 前掲の 2 種類の事故が残り 9 回分の機会を持つ。**土台 3（[IADR-0258](IADR-0258_structure-aware-checkers-dual-layout.md)）が確立した「樹形の実在から動的に導く」考え方に反する**——同 IADR は構造依存の検査器を新旧両対応させる設計思想そのものであり、下限だけを例外的に手書きのまま残す理由が無い |
| A2: `DomainProjectFiles.Count` 自身を下限として使う（`actual >= actual.Count`） | メタ検査の実測値をそのまま下限にする | ✕ **トートロジー**。探索が壊れて 0 件になっても `0 >= 0` は常に真であり、メタ検査（探索の失効を検出する）としての意味を失う。第二の独立した信号が要る |
| **A3: 「未移送で `*.Domain.csproj` を持つサービス」を実ツリーから独立に数え、それを下限にする（採用）** | `backend/Services/<Svc>/src/` の実在（旧構成の印）と、その配下に `*.Domain` ディレクトリが実在するかを、`DomainProjectFiles`（`*.csproj` ファイルの再帰列挙）とは別の探索経路（`src/` 直下のディレクトリ列挙）で数える | ○ 移送のたびに人手を介さず自動で下限が動く ○ A2 と異なり **探索経路を分けている**ため、`DomainProjectFiles` 側の探索が壊れても本カウントは連動して 0 にならず、メタ検査として機能し続ける ○ 「単純な引き算」ではなく「Domain を持つ未移送サービスだけを数える」ため、[IADR-0264](IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 2 のように**移送しても Domain が減らないケース**を自然に扱える（ConfigurationService は移送後 `src/` ごと消えるので数えられなくなるが、その前提の型は既に共有カーネルへ出ており `*.Domain.csproj` 側の実測も減っていたため整合する） |

**A3 を採用した。** A1 は本 IADR が解消したい問題そのものであり、A2 は検査の意味を壊す。

## 決定

### 決定 1 — 下限は `RepositoryLayout.UnmigratedServicesWithDomainProjectCount` から動的に導く

`backend/Services/<Svc>/` を実ディレクトリ列挙で走査し、**`src/` ディレクトリが実在し、かつ
その配下に名前が `.Domain` で終わるディレクトリを 1 つ以上持つサービスの数**を数える
新規プロパティを `RepositoryLayout` に追加した。`DomainLayerDependencyTests.Domain_プロジェクトの
探索が空振りしていない()` は、この値を `HaveCountGreaterThanOrEqualTo` の期待値として使う
（ハードコードした整数リテラルを削除した）。

- **探索経路をあえて分ける。** `DomainProjectFiles` は `backend/Services` 配下の全 `*.csproj` を
  再帰列挙してファイル名の接尾辞で判定するのに対し、本プロパティは `<Svc>/src/` 直下の
  **ディレクトリ名**だけを見る（`*.csproj` の中身は読まない）。両者が同じ実測値に収束することを
  期待しつつ、**片方の探索ロジックが壊れても、もう片方が連動して 0 に落ちない**ようにするための
  意図的な冗長性である（[IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md) が
  csproj 静的解析とソース走査を「二重化」させた設計思想と同じ理由づけ）。

### 決定 2 — 判定ロジックはファイル I/O から切り離した純関数として自己試験する

判定の中身（「`src/` が実在し、かつ `.Domain` 接尾辞のディレクトリを持つか」）を
`RepositoryLayout.CountsAsUnmigratedServiceWithDomainProject(bool, IEnumerable<string>)` という
純関数へ切り出し、`DomainLayerDependencyTests` に `[Theory]` で肯定形 2 件・**否定形 4 件**
（`src/` が無い・`.Domain` 系ディレクトリを持たない・`src/` が空・接尾辞が完全一致しない）を
固定した。実ディスクを読む `UnmigratedServicesWithDomainProjectCount` 自体を単体テストで
差し替えるのは（テスト用の仮想リポジトリツリーを作る負担に対して得るものが小さく）採らず、
**判定ロジックのみを切り出して固定する**——`DomainSourceDependencyTests` が `DomainSourceScan` の
各ヘルパを同じ作法で固定しているのに揃えた。

### 決定 3 — 0 件になったら「探索の失敗」ではなく「役目を終えた」と読み、fail-loud で落とす

移送が全 11 サービス完了すると、`UnmigratedServicesWithDomainProjectCount` は必然的に `0` へ
収束する。このとき `HaveCountGreaterThanOrEqualTo(0, ...)` は**常に真**であり、メタ検査が
無条件に緑へ変わってしまう——既存の `DomainLayerDependencyTests` 冒頭コメントが
「検査器が静かに失効する経路を塞ぐためのメタ検査である（IADR-0127 と同じ性質）」と述べる、
まさにその経路そのものである。**`expected == 0` を先に判定し、
`Assert.Fail` で明示的に落とす。** 失敗メッセージには「この検査（csproj 静的解析による層の強制）は
役目を終えた」「ソース走査版（`DomainSourceDependencyTests`）へ一本化し、本クラスと
`IsAllowedDomainDependency` 等の csproj 依存の仕組みを削除すること」と、次にすべき作業まで書く。

### 決定 4 — `DomainSourceDependencyTests` の下限は実測して確認し、変えなかった

`DomainSourceDependencyTests` の 2 つの下限（Domain ソース領域の数・走査対象ファイル数）も
同じ「サービス数依存」の疑いを持つため、CostControlService の移送前後で実測した。

| 検査 | 移送前 | 移送後 |
| --- | --- | --- |
| Domain ソース領域数（`DomainSourceDirectories`） | 8 | 8（変化なし） |
| 走査対象ファイル数 | 変化なし（`CostGovernor.cs` / `CostReview.cs` の 2 ファイルは
  旧 `src/CostControlService.Domain/` から新 `CostControlService/Domain/` へ**移動しただけ**） |

`RepositoryLayout.DomainSourceDirectories` は「現行構成（層＝プロジェクト）」と「Vertical Slice
構成（層＝フォルダ）」の**両方の形を数える和集合**として設計されている
（[IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md)）。CostControlService は
Domain を持つサービスであり、移送前は `src/CostControlService.Domain/`（形 1）として、移送後は
`CostControlService/Domain/`（形 2）として、**いずれの時点でも必ずどちらか一方の形で数えられる**
——これが [IADR-0264](IADR-0264_configurationservice-vsa-and-client-abolition.md) の実測（設定サービスは
`VersionedAssumptions` が共有カーネルへ抜けて Domain 自体が空になった）と異なる点である。

**実測して「減らない」ことを確かめたので、この 2 つの下限には触れなかった。** 「同じ問題を持つなら
同様に扱う」という指示を、確認せずに機械的な横展開はしないという判断で読んだ
（[IADR-0264](IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 5 の実測も同じ立場）。

## 理由

- **決定 1 は「土台 3 の考え方をここにも適用する」という一貫性を優先した。**
  [IADR-0258](IADR-0258_structure-aware-checkers-dual-layout.md) は構造依存の検査器を
  新旧両対応させる設計を既に確立している。下限だけがその外側で手書きのまま残るのは、
  同じリポジトリの中に「動的に導く」検査器と「手で数える」検査器が混在する状態であり、
  次に触る人が両者の違いに気付けない負債になる。
- **決定 2 は「切り出せる部分だけを切り出す」という現実的な線を選んだ。**
  実ディスクを読む部分（`Directory.EnumerateDirectories`）ごとフェイクするのは、
  仮想ファイルシステムの抽象化を新設する費用に見合わない。判定ロジック（bool と文字列列挙を
  受け取る純粋な述語）だけを外に出せば、肯定・否定の両方を安価に固定できる。
- **決定 3 は「0 件で無条件に緑」という経路を、他の検査器と同じ基準で塞いだ。** 検査が役目を終えたこと自体は歓迎すべき進捗だが、**気付かれずに
  無意味化する**のと**気付いて片付ける**のとでは扱いが違う。後者を強制する。
- **決定 4 は「指示を鵜呑みにしない」ことを優先した。** 「同じ問題を持つなら同様に扱う」という
  指示は仮定形であり、**仮定が成立するかどうかを実測する**のが先である（同じ姿勢は
  `.claude/rules/traceability.repo.md`「是正・追随の母集合の取り方」規則が求める態度と同じ）。

## 結果

- 良い影響:
  - **残り 9 回の移送で、`DomainLayerDependencyTests` の下限を手で書き換える作業が消える。**
    移送を実施すれば、次のビルドで下限も実測値も自動的に追随する。
  - 探索経路を分けたことで、メタ検査としての独立性（「片方が壊れてももう片方が拾う」）を保った。
  - 0 件到達時に「役目を終えた」ことを検査自身が名指しするため、**将来の移送完了時に
    このクラスを片付け忘れるリスクを下げた。**
- 悪い影響・トレードオフ:
  - `RepositoryLayout` に、実測プロパティとほぼ同じ判定を行う経路が 2 本（`DomainProjectFiles` の
    `*.csproj` 走査と `UnmigratedServicesWithDomainProjectCount` の `src/` 直下ディレクトリ走査）
    存在することになり、**将来どちらか一方だけを直して他方を直し忘れる余地**が生まれる
    （[IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md) の csproj 側／ソース側の
    二重化と同種のトレードオフとして許容する）。
  - `CountsAsUnmigratedServiceWithDomainProject` の自己試験はロジックの正しさを固定するが、
    **実ディスクの列挙自体（`Directory.EnumerateDirectories` の呼び出し）は自己試験の対象外**
    のままである。
- フォローアップ:
  1. 全 11 サービスの移送が完了し `UnmigratedServicesWithDomainProjectCount` が `0` に到達したら、
     `DomainLayerDependencyTests` クラス全体（メタ検査・検査 1〜3・`IsAllowedDomainDependency`）を
     削除し、`DomainSourceDependencyTests`（ソース走査版）へ一本化する。
  2. 残り 9 サービスの移送でも、`DomainSourceDependencyTests` の 2 つの下限（領域数・走査ファイル数）
     は**移送のたびに実測して変化の有無を確認する**——本 IADR 決定 4 と同じ手順を踏襲し、
     変化しないことを確認できた場合のみ触らない。

## 関連

- 上流: [IADR-0264](IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 5（宿題の予告）・
  [IADR-0258](IADR-0258_structure-aware-checkers-dual-layout.md)（動的化の設計思想）・
  [IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md)（二重化検査の全体設計）
- 作業仕様書: [20260829_w11s4c_costcontrolservice-vsa](../specs/20260829_w11s4c_costcontrolservice-vsa.md)
- Supersedes: なし（[IADR-0264](IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 5 の
  「実測で読む」方針を継承・具体化するもので、対立する決定を覆すものではない）
- Superseded by: なし
