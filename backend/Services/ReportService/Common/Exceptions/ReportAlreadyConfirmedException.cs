namespace ReportService.Common.Exceptions;

// FR-07, ADR-0003, NFR-06, IADR-0503, #1206: 確定済みの報告書を変更しようとした（業務のエラー）。ホストのエンドポイントは
// これを 409 Conflict に写し、文言を応答へ載せる。🔴 **409 と文言を返すのはこの型だけ**であり、ほかの
// InvalidOperationException（EF・フレームワーク由来を含む）は写さない（未処理例外として ProblemDetails の 500 になる）。
// InvalidOperationException の派生にして、既存の捕捉（自動生成の「直前に確定された」）の挙動を変えない。
public sealed class ReportAlreadyConfirmedException(string periodKey)
    : InvalidOperationException($"確定済み報告書 {periodKey} は変更できません。")
{
    public string PeriodKey { get; } = periodKey;
}
