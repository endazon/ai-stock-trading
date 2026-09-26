'use strict';
// helm-release-drift.js の自己試験・試験が使う、資格情報の見張り値（PR #1043 の監査 F1 のプローブ 10 件＋追加分）。
// 実在の値ではない。見張り値はすべて大文字の "FIXTURE" を含み、出力に "FIXTURE" が 1 つでも出たら漏れである。
// 🔴 **字面をそのままソースへ置かない**（`scheme://user:pass@host` 等を連結で組む）。秘密の走査（gitleaks）は PR の全コミットを
// 見るため、偽値でも一度当たると force push なしでは消せない（#1042 で実際に起きた）。
const AT = '@';
const SEP = '://';
const S = (n) => `FIXTURE${'X'.repeat(4)}${n}`;

module.exports = {
  // 伏せなければならない（名前か値のどちらかが機密らしい）。
  sensitive: [
    // 1. userinfo のユーザー名が空（redis）
    { name: 'Cache__Url', value: `redis${SEP}:${S(1)}${AT}redis:6379` },
    // 2. userinfo がトークンだけ（ユーザー名なし）
    { name: 'Git__Remote', value: `https${SEP}${S(2)}${AT}github.com/org/repo` },
    // 3. Sentry の DSN（公開鍵を userinfo に持つ）
    { name: 'Telemetry__Target', value: `https${SEP}${S(3)}${AT}o0.ingest.sentry.io/42` },
    // 4. パスワードに "/" を含む
    { name: 'Bus__Url', value: `amqp${SEP}u:pa/${S(4)}${AT}h:5672` },
    // 5. キー名に webhook を含まない Webhook の URL（/webhooks/<id>/<token>）
    { name: 'Notify__Target', value: `https${SEP}chat.example/api/webhooks/123456789/${S(5)}` },
    // 6〜8. 名前だけで分かるもの（__ の区切り・camelCase の境目）
    { name: 'Finnhub__Key', value: S(6) },
    { name: 'Llm__AccessKey', value: S(7) },
    { name: 'Auth__Bearer', value: S(8) },
    // 9. 接続文字列の Pass=
    { name: 'Db__Main', value: `Host=db;Username=u;Pass=${S(9)}` },
    // 10. ユーザー名とパスワードのある URL（修正前も伏せていた形）
    { name: 'Queue__Url', value: `amqp${SEP}guest:${S(10)}${AT}rabbitmq:5672` },
    // 追加: 接続文字列の AccountKey= / Pwd= / Password=
    { name: 'Storage__Main', value: `DefaultEndpointsProtocol=https;AccountName=a;AccountKey=${S(11)}` },
    { name: 'Legacy__Db', value: `Server=s;Uid=u;Pwd=${S(12)}` },
    { name: 'Other__Db', value: `Host=h;Password=${S(13)}` },
    // 追加: クエリの鍵・トークンらしいパス要素・トークンらしい長い値・名前の dsn / pwd / secret / token
    { name: 'Api__Endpoint', value: `https${SEP}api.example/v1/data?to${'ken'}=${S(14)}` },
    { name: 'Hook__Target', value: `https${SEP}hooks.example/services/${'FIXTURE'}9a8b7c6d5e4f3g2h` },
    { name: 'Misc__Value', value: `${'FIXTURE'}9x8Y7w6V5u4T3s2R1q0` },
    { name: 'SENTRY_DSN', value: S(15) },
    { name: 'OPEND_LOGIN_PWD_MD5', value: S(16) },
    { name: 'App__ClientSecret', value: S(17) },
    { name: 'Discord__BotToken', value: S(18) },
  ],
  // 伏せてはならない（突合で見たい設定。伏せすぎて差が読めなくなるのを防ぐ陰性対照）。
  visible: [
    { name: 'Auth__Authority', value: 'http://keycloak:8080/realms/platform' },
    { name: 'ServiceAuth__TokenEndpoint', value: 'http://keycloak:8080/realms/platform/protocol/openid-connect/token' },
    { name: 'TOKEN_ENDPOINT', value: 'http://keycloak:8080/realms/ai-stock-trading/protocol/openid-connect/token' },
    { name: 'KnowledgeBase__Documents__BaseUrl', value: 'http://document-service.microservices-platform:8080' },
    { name: 'Reconciliation__IntervalHours', value: '1' },
    { name: 'Collection__Source__SecEdgar__Ciks__0', value: '0000320193' },
    { name: 'Monitor__SeedSymbols__0__Symbol', value: 'AAPL' },
    { name: 'ASPNETCORE_URLS', value: 'http://+:8080' },
    { name: 'LlmPricing__PerModel__claude_opus_5__InputPer1kTokens', value: '0.015' },
  ],
};
