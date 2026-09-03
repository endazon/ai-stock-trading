namespace RiskManagementService.Features.RiskManagement;

// kill switch 操作の要求（理由必須・FR-11・ADR-0003）。
// **2 段目に残る**——起動（engage）と解除（disengage）の 2 操作が使う（platform ADR-0068 決定2）。
public sealed record KillSwitchRequest(string Reason);
