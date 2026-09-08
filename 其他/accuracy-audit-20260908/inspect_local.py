import json
import os
import sqlite3
from pathlib import Path
from datetime import datetime, timezone

out = Path(__file__).resolve().parent
source = Path(os.environ['LOCALAPPDATA']) / 'UsageTray' / 'usage.db'
con = sqlite3.connect(source.as_uri() + '?mode=ro', uri=True)
con.row_factory = sqlite3.Row
backup = sqlite3.connect(out / 'audit-copy.db')
con.backup(backup)
backup.close()
result = {'observed_at_utc': datetime.now(timezone.utc).isoformat()}
result['schema'] = {r['name']: [x['name'] for x in con.execute('pragma table_info("' + r['name'] + '")')] for r in con.execute("select name from sqlite_master where type='table'")}
result['latest_quotas'] = [dict(r) for r in con.execute('''select q.* from quota_snapshots q where captured_at_utc=(select max(captured_at_utc) from quota_snapshots t where t.provider=q.provider and t.model_or_pool_id=q.model_or_pool_id and t.window_kind=q.window_kind) order by provider, model_or_pool_id''')]
result['quota_recent'] = [dict(r) for r in con.execute("select * from quota_snapshots where provider='Codex' order by captured_at_utc desc limit 16")]
result['buckets_by_model'] = [dict(r) for r in con.execute('select provider, model_id, count(*) as buckets, sum(input_tokens) as input_tokens, sum(output_tokens) as output_tokens from file_usage group by provider, model_id')]
con.close()
(out / 'local-data.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({k: v for k, v in result.items() if k not in ('schema', 'quota_recent')}, ensure_ascii=False, indent=2))
