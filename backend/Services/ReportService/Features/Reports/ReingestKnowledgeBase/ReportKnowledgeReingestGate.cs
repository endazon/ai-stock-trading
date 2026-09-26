namespace ReportService.Features.Reports.ReingestKnowledgeBase;

// FR-08, #1028, IADR-0436 決定 1: 入れ直しを同時に 1 本だけにする（同時の 2 本が同じ「無い」を見て両方作ると重複する）。
// プロセス内の排他（report-service は 1 レプリカの前提。残余リスクは IADR-0436）。singleton で登録する。
public sealed class ReportKnowledgeReingestGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool TryEnter() => _semaphore.Wait(0);

    public void Exit() => _semaphore.Release();
}
