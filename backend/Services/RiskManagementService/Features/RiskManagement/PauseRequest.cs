namespace RiskManagementService.Features.RiskManagement;

// 一時停止/再開操作の要求（理由必須・FR-11・ADR-0009）。
// **2 段目に残る**——停止（pause）と再開（resume）の 2 操作が使う（platform ADR-0068 決定2）。
public sealed record PauseRequest(string Reason);
