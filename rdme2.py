import io
KIT='planning/tools/impl-handoff-kit/repo-template/scripts/README.md'
CUR='scripts/README.md'
kit=io.open(KIT,encoding='utf-8').read()
cur=io.open(CUR,encoding='utf-8').read()
out = kit
own_tbl = cur[cur.index('**本リポジトリ固有**'):cur.index('## プロファイルの適用')]
hdr = '| スクリプト | 役割 | 出力 |'
assert hdr in out
out = out.replace(hdr, '**キット共通**（impl-handoff-kit の `repo-template/scripts/` 由来。文面・挙動をキットに揃える）:\n\n'+hdr, 1)
out = out.replace('## プロファイルの適用', own_tbl + '## プロファイルの適用', 1)
ccm_kit = [l for l in out.split('\n') if l.startswith('| `check-commit-messages.js` |')][0]
ccm_ast = [l for l in cur.split('\n') if l.startswith('| `check-commit-messages.js` |')][0]
out = out.replace(ccm_kit, ccm_ast, 1)
out = out.replace('`STRICT_AI_WORKFLOW_CONFIG=1` で警告を失敗として扱える（既定はオフ）。',
                  '`STRICT_AI_WORKFLOW_CONFIG=1` で警告を失敗として扱える（既定はオフ。**本リポジトリは有効化済み**）。', 1)
out = out.replace('| companion あり ＋ `REQUIRE_REPO_TESTS` 未設定 | `notice:` で設定を促す |',
                  '| companion あり ＋ `REQUIRE_REPO_TESTS` 未設定 | `notice:` で設定を促す（**本リポジトリは設定済み**のため出ない） |', 1)
out = out.replace('| `pipeline-config` | `validate-pipeline-config.js --self-test`（任意コンポーネント。採否は HOWTO Part B-6） |',
 '| `pipeline-config` | `validate-pipeline-config.js --self-test` ＋ 実ファイル（`PIPELINE_CONFIG`。本リポは採用する） |\n'
 '| `consumer-endpoint-names` | `check-consumer-endpoint-names.js --self-test` と本検査（本リポ固有） |\n'
 '| `runtime-scaffold` | `validate-runtime-scaffold.js`（本リポ固有） |\n'
 '| `shell-scripts` | `k8s-local-deploy.test.sh` / `deploy/opend/entrypoint.test.sh`（本リポ固有） |', 1)
tail = cur[cur.index('## スタック・プロジェクト依存の置換点'):]
out = out.rstrip('\n') + '\n\n' + tail
io.open(CUR,'w',encoding='utf-8',newline='\n').write(out)
print('rebuilt from kit base')
