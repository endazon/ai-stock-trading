namespace ReportService.Features.Reports;

// FR-07, IADR-0071 決定5: レビュー操作の要求（版番号付き楽観排他）。
// NFR, IADR-0289 決定3: present / request-changes の 2 操作が使うため 2 段目に置く。
internal sealed record ReviewCommandRequest(int ExpectedVersion);
