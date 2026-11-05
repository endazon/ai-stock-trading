namespace RiskManagementService.Features.RiskManagement;

// 一時停止/再開操作の要求（理由必須・FR-11・ADR-0009）。
// **2 段目に残る**——停止（pause）と再開（resume）の 2 操作が使う（platform ADR-0068 決定2）。
// NFR-06, #1542, IADR-0615: SecondFactorCode（任意・末尾に追加）は再開のときだけ見る（一時停止＝止める向きには課さない）。
public sealed record PauseRequest(string Reason, string? SecondFactorCode = null);
