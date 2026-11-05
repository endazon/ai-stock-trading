namespace RiskManagementService.Features.RiskManagement;

// kill switch 操作の要求（理由必須・FR-11・ADR-0003）。
// **2 段目に残る**——起動（engage）と解除（disengage）の 2 操作が使う（platform ADR-0068 決定2）。
// 🔴 FR-10, FR-14, #1534, IADR-0620: ExpectedVersion は「見た状態」の版（照会の応答の version）。渡すと、現在の版と違えば 409 で拒否し状態を変えない。
// 未指定は段 1（既定）で従前どおり受ける。段 2 では未指定の解除を拒否する（発動は安全側の操作のため求めない）。
public sealed record KillSwitchRequest(string Reason, string? ExpectedVersion = null);

// NFR, #1534, IADR-0620: 要求から見た状態の版を取り出す（本文の expectedVersion ＞ If-Match ヘッダー。ETag の引用符と弱い印 W/ は外す）。
internal static class KillSwitchPrecondition
{
    public static string? ExpectedVersionOf(KillSwitchRequest req, Microsoft.AspNetCore.Http.HttpContext http)
    {
        if (!string.IsNullOrWhiteSpace(req.ExpectedVersion))
            return req.ExpectedVersion.Trim();

        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(ifMatch))
            return null;

        // `*`・複数の値（カンマ区切り・ヘッダーの重複）は解釈しない（400）。
        var v = ifMatch.Trim();
        if (http.Request.Headers.IfMatch.Count > 1 || v.Contains(',', StringComparison.Ordinal) || v == "*")
            throw new KillSwitchIfMatchUnsupportedException();
        if (v.StartsWith("W/", StringComparison.Ordinal))
            v = v[2..];
        v = v.Trim('"');
        if (v.Length == 0 || v == "*")
            throw new KillSwitchIfMatchUnsupportedException();
        return v;
    }
}
