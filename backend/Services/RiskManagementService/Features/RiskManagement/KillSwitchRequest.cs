namespace RiskManagementService.Features.RiskManagement;

// kill switch 操作の要求（理由必須・FR-11・ADR-0003）。
// **2 段目に残る**——起動（engage）と解除（disengage）の 2 操作が使う（platform ADR-0068 決定2）。
// NFR-06, #1542, IADR-0615: SecondFactorCode（任意・末尾に追加）は解除のときだけ見る（起動＝止める向きには課さない）。
public sealed record KillSwitchRequest(string Reason, string? SecondFactorCode = null);
