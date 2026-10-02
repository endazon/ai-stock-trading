using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;

namespace OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 追記）, #1000, IADR-0482 決定2: 実弾口座の読み取り専用の照会に使う
// moomoo SDK の取引接続（MMAPI_Trd）への**狭い**シーム。
//
// 🔴 **面は「口座一覧」と「借株可否・維持率の束（TrdGetMarginRatio）」の 2 つの照会だけである。**
// 発注・訂正・取消・取引の解錠（PlaceOrder / ModifyOrder / PlaceComboOrder / UnlockTrade）を**型として持たない**。
// 発注の面を持つ IMoomooTradeConnection（SIMULATE の発注経路）とは別の型であり、相互に継承・変換しない。
// 本シームの実装は SDK の MMAPI_Trd を private に包むだけで、外へ渡さない（MMAPI_Trd 自体は発注メソッドを持つため）。
// 面を広げる（照会を足す）ときも、発注系のメソッドは足さない（RealReadOnlyIsolationTests が赤くなる）。
public interface IMoomooMarginQueryConnection : IDisposable
{
    void SetClientInfo(string clientId, int clientVersion);

    void SetConnCallback(MMSPI_Conn callback);

    void SetTrdCallback(MMSPI_Trd callback);

    // 鍵の内容（PKCS#1 PEM 文字列）を渡す（パスではない）。**ログへ出さないこと。**
    void SetRsaPrivateKey(string privateKeyPem);

    bool InitConnect(string host, ushort port, bool encrypt);

    void Close();

    uint GetAccList(TrdGetAccList.Request request);

    uint GetMarginRatio(TrdGetMarginRatio.Request request);
}

// 接続オブジェクトの生成点。接続試行が失敗するたびに Create() し直す（IADR-0327 と同じ理由）。
public interface IMoomooMarginQueryConnectionFactory
{
    IMoomooMarginQueryConnection Create();
}

// 本番実装。SDK の MMAPI_Trd を 1 つ包むだけで、状態も判断も持たない。**MMAPI_Trd を外へ出さない。**
public sealed class MMApiMarginQueryConnectionFactory : IMoomooMarginQueryConnectionFactory
{
    public IMoomooMarginQueryConnection Create() => new MMApiMarginQueryConnection();

    private sealed class MMApiMarginQueryConnection : IMoomooMarginQueryConnection
    {
        private readonly MMAPI_Trd _trd = new();

        public void SetClientInfo(string clientId, int clientVersion) => _trd.SetClientInfo(clientId, clientVersion);

        public void SetConnCallback(MMSPI_Conn callback) => _trd.SetConnCallback(callback);

        public void SetTrdCallback(MMSPI_Trd callback) => _trd.SetTrdCallback(callback);

        public void SetRsaPrivateKey(string privateKeyPem) => _trd.SetRSAPrivateKey(privateKeyPem);

        public bool InitConnect(string host, ushort port, bool encrypt) => _trd.InitConnect(host, port, encrypt);

        public void Close() => _trd.Close();

        public uint GetAccList(TrdGetAccList.Request request) => _trd.GetAccList(request);

        public uint GetMarginRatio(TrdGetMarginRatio.Request request) => _trd.GetMarginRatio(request);

        public void Dispose() => _trd.Dispose();
    }
}
