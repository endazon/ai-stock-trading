using System.Text.RegularExpressions;

namespace NotificationService.Domain;

// FR-14, FR-20, FR-07, UC-06, #861, #868, IADR-0240 決定11, IADR-0383 決定4:
// **Bot が本文で運ぶ「代理される利用者」（onBehalfOf）の値域。純関数。**
//
// 権威側（報告書サービスの `ConfirmingActorResolver`・リスク管理の `DelegatedActorResolver`）は、
// 信頼するクライアントが**値域外**の名前を送ったとき **400 で処理しない**（操作者を記録できない操作は行わない）。
// 値の出所は運用者が設定した `Notifications:Discord:Bot:UserMapping` であり、Discord は唯一の確定・承認の窓口
// である。したがって**値域外の対応付けは「たまに失敗する」ではなく「その利用者は恒常的に何も確定・承認できない」**
// を意味する（#861 の監査が稼働環境で実測: `'山田'`（非 ASCII）・`'dev owner'`（空白入り）がいずれも Rejected）。
//
// 🔴 **起動時に気付けるようにするのが本型の目的である。** 実行時の 400 は、押した人にしか見えない。
//
// 🔴 **値域の定義は 3 サービスに同じ形で存在する**（本型・報告書・リスク管理）。送り手が「通る」と判定した名前を
// 受け手が弾く、という静かな破れを避けるため、変えるときは 3 箇所を同時に変える。**機械検査は無い**
// （C# 同士だが、サービスを跨いでコードを共有しない方針のため。IADR-0383 の残余リスク）。
public static partial class DelegatedActorName
{
    /// <summary>値域の説明（警告文・テストが参照する単一情報源）。</summary>
    public const string RangeDescription = "英数字と . _ @ + - の 1〜64 文字";

    // **`\A…\z`**（.NET の `$` は末尾 LF の直前にもマッチする。IADR-0240 決定6 の追記）。
    [GeneratedRegex(@"\A[A-Za-z0-9._@+-]{1,64}\z")]
    private static partial Regex Pattern();

    /// <summary>値域内か。null・空は false（対応付けが無いのと同じく、操作者を特定できない）。</summary>
    public static bool IsInRange(string? value) => value is not null && Pattern().IsMatch(value);

    /// <summary>
    /// FR-14, #868: 多層認証の対応付け（Discord ユーザー ID → Keycloak 利用者名）のうち、
    /// **値域外の利用者名を持つもの**を返す（Discord ユーザー ID の昇順・安定）。
    /// <para>
    /// 空なら問題なし。返す組の <c>KeycloakUser</c> は**そのまま警告文へ載る**ため、呼び出し側でログの
    /// サニタイズを通すこと（外部由来ではなく運用者設定だが、改行を含み得る）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> OutOfRangeMappings(
        IEnumerable<KeyValuePair<string, string>> userMapping)
    {
        ArgumentNullException.ThrowIfNull(userMapping);

        return [.. userMapping
            .Where(pair => !IsInRange(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)];
    }
}
