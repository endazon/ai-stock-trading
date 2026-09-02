// MSP/ADR-0066 決定 4 / 計画 13_frontend-stack §ディレクトリ構成: **feature の公開面**。
// feature の外から触ってよいのは、このファイルが再輸出したものだけである。
// `api/` `components/` `hooks/` `routes/` `stores/` `types/` へ feature の外から直接 import しない。
//
// 🔴 **barrel は Bulletproof React の現行版では非推奨である**（tree shaking を妨げる）。本 SPA は
// 合成点アーキテクチャを採っており feature に「外から呼んでよい面」が要るため、この一点で
// 意図的に外れる（MSP/ADR-0066 決定 4 が逸脱として記録している）。
export { createSc03ControlsRoute, sc03ControlsNav } from './routes/sc03ControlsRoute';
