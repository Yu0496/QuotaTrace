"""Small manual, per-file cleaner. No automatic retirement or inherited exemptions."""
import argparse
import contextlib
import ctypes
import json
import os
from pathlib import Path, PurePosixPath
import stat
import subprocess
import sys
import time

import artifact_runtime as runtime

sys.dont_write_bytecode = True

ROOT = Path(__file__).resolve().parents[1]
DAY = 86400 * 10**9


Blocked = runtime.Blocked


def read(path, default=None):
    try:
        return json.loads(path.read_text('utf-8'))
    except FileNotFoundError:
        if default is not None:
            return default
        raise


def fingerprint(s, path):
    changed = s.st_ctime_ns
    if os.name == 'nt':
        # Python 3.10's Windows ctime is creation time. NTFS ChangeTime also
        # invalidates a mark after edits that restore the previous mtime/size.
        from ctypes import wintypes as w
        k = ctypes.WinDLL('kernel32', use_last_error=True)
        k.CreateFileW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, ctypes.c_void_p, w.DWORD, w.DWORD, w.HANDLE]
        k.CreateFileW.restype = w.HANDLE
        k.GetFileInformationByHandleEx.argtypes = [w.HANDLE, ctypes.c_int, ctypes.c_void_p, w.DWORD]
        k.CloseHandle.argtypes = [w.HANDLE]
        class Basic(ctypes.Structure):
            _fields_ = [('created', ctypes.c_int64), ('accessed', ctypes.c_int64),
                        ('written', ctypes.c_int64), ('changed', ctypes.c_int64), ('attrs', w.DWORD)]
        handle = k.CreateFileW(str(path), 0x80, 7, None, 3, 0x02000000, None)
        if handle == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            basic = Basic()
            if not k.GetFileInformationByHandleEx(handle, 0, ctypes.byref(basic), ctypes.sizeof(basic)):
                raise ctypes.WinError(ctypes.get_last_error())
            if basic.attrs & 0x400:
                raise Blocked('reparse 文件')
            changed = basic.changed
        finally:
            k.CloseHandle(handle)
    return [s.st_mtime_ns, s.st_size, changed, s.st_dev, s.st_ino]


def no_links(path):
    """Check lexical ancestors, including junctions and other Windows reparse points."""
    for p in [path, *path.parents]:
        try:
            s = p.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(s.st_mode) or getattr(s, 'st_file_attributes', 0) & 0x400:
            raise Blocked('链接/reparse 路径: ' + str(p))
    return path


@contextlib.contextmanager
def held_file(path, directory=False):
    """Lock ancestors against rename; deny file writes/deletion until disposition."""
    if os.name != 'nt':
        raise Blocked('实际删除只支持已验证的 Windows 句柄实现')
    from ctypes import wintypes as w
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    k.CreateFileW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, ctypes.c_void_p, w.DWORD, w.DWORD, w.HANDLE]
    k.CreateFileW.restype = w.HANDLE
    k.CloseHandle.argtypes = [w.HANDLE]
    k.GetFileInformationByHandle.argtypes = [w.HANDLE, ctypes.c_void_p]
    k.SetFileInformationByHandle.argtypes = [w.HANDLE, ctypes.c_int, ctypes.c_void_p, w.DWORD]
    class Info(ctypes.Structure):
        _fields_ = [('attrs', w.DWORD), ('created', w.FILETIME), ('accessed', w.FILETIME),
                    ('written', w.FILETIME), ('volume', w.DWORD), ('high', w.DWORD),
                    ('low', w.DWORD), ('links', w.DWORD), ('indexHigh', w.DWORD), ('indexLow', w.DWORD)]
    handles = []
    try:
        for p in [*reversed(path.parents), path]:
            isfile = p == path and not directory
            h = k.CreateFileW(str(p), 0x10000 if p == path else 0, 1 if isfile else 3,
                              None, 3, 0x02200000, None)
            if h == ctypes.c_void_p(-1).value:
                raise ctypes.WinError(ctypes.get_last_error())
            handles.append(h)
            info = Info()
            if not k.GetFileInformationByHandle(h, ctypes.byref(info)):
                raise ctypes.WinError(ctypes.get_last_error())
            if info.attrs & 0x400 or (isfile and (info.attrs & 0x10 or info.links != 1)):
                raise Blocked('锁定后发现链接或非普通文件: ' + str(p))
        def remove():
            flag = w.BOOL(True)
            if not k.SetFileInformationByHandle(handles[-1], 4, ctypes.byref(flag), ctypes.sizeof(flag)):
                raise ctypes.WinError(ctypes.get_last_error())
        yield remove
    finally:
        for h in reversed(handles):
            k.CloseHandle(h)


def validate_config(root, config):
    if not isinstance(config, dict) or set(config) - {'schema', 'roots', 'activity', 'integration'} or config.get('schema') != 1:
        raise Blocked('清理配置必须为 schema 1 / roots / 可选 activity')
    keys = config.get('roots')
    if not isinstance(keys, list) or not keys or len(keys) > 16:
        raise Blocked('明确指定少量专用可弃目录')
    roots = []
    for key in keys:
        p = runtime.inside(root, key)
        rel = p.relative_to(root)
        if len(rel.parts) < 2 or rel.parts[0] != '.validation' or rel.parts[1].casefold() in {
                'state', 'active', 'cleanup.json', 'cleanup.writing', '.gdignore'}:
            raise Blocked('可弃目录限 .validation 内独立子目录；不能指定项目/资料/状态根')
        if any(p == old or p.is_relative_to(old) or old.is_relative_to(p) for old in roots):
            raise Blocked('可弃目录重复或重叠')
        roots.append(p)
    state = no_links(root / '.validation/cleanup.json')
    activity = config.get('activity', {'kind': 'local'})
    if activity != {'kind': 'local'}:
        raise Blocked('只支持本项目本地活动登记')
    registry = root / '.validation/active'
    runtime.verify_local_bridge(root)
    return roots, state, activity, registry


class Cleaner:
    def __init__(self, root=ROOT):
        self.root = Path(os.path.abspath(root))
        config = read(no_links(self.root / 'tools/artifact-clean.json'))
        self.roots, self.state, self.activity_config, self.registry = validate_config(self.root, config)
        self.runtime = runtime

    def lock(self):
        # Same short transaction lock as run admission and preview consumers.
        return self.runtime.locked(self.root / '.validation/state/transaction.lock')

    def target(self, key):
        if not isinstance(key, str) or '\\' in key or ':' in key:
            raise Blocked('只接受项目内正斜线相对路径')
        rel = PurePosixPath(key)
        if rel.is_absolute() or '..' in rel.parts or rel.as_posix() != key:
            raise Blocked('非法相对路径: ' + str(key))
        p = runtime.inside(self.root, key)
        if not any(p != base and p.is_relative_to(base) for base in self.roots):
            raise Blocked('目标不在指定可弃根内: ' + key)
        return no_links(p)

    def records(self):
        data = read(no_links(self.state), {'schema': 1, 'marks': {}, 'protect': {}})
        if (not isinstance(data, dict) or set(data) != {'schema', 'marks', 'protect'} or data['schema'] != 1
                or not isinstance(data['marks'], dict) or not isinstance(data['protect'], dict)):
            raise Blocked('损坏的清理标记表')
        for key, value in data['marks'].items():
            self.target(key)
            if not isinstance(value, list) or len(value) != 5 or any(type(v) is not int for v in value):
                raise Blocked('损坏的可删标记')
        for key, reason in data['protect'].items():
            self.target(key)
            if not isinstance(reason, str) or not reason.strip():
                raise Blocked('保护必须有具体理由')
        return data

    def save(self, data):
        # No tombstones or audit archive; remove records for missing objects.
        for group in ('marks', 'protect'):
            data[group] = {k: v for k, v in data[group].items() if self.target(k).exists()}
        data['marks'] = {k: v for k, v in data['marks'].items()
                         if fingerprint(self.target(k).stat(), self.target(k)) == v}
        runtime.write_json(self.state, data)

    def mark(self, action, keys, reason=None):
        with self.lock():
            data = self.records()
            for key in keys:
                p = self.target(key)
                # Store actual Windows casing so a differently cased CLI path
                # cannot silently lose its protection during directory enumeration.
                key = p.resolve().relative_to(self.root.resolve()).as_posix()
                if action == 'mark':
                    s = p.stat()
                    if not stat.S_ISREG(s.st_mode):
                        raise Blocked('可删标记必须逐文件指定')
                    data['marks'][key] = fingerprint(s, p)
                elif action == 'protect':
                    if not p.exists() or not reason or not reason.strip():
                        raise Blocked('保护目标须存在，并写具体理由')
                    data['protect'][key] = reason.strip()
                else:
                    data['protect'].pop(key, None)
            self.save(data)

    def git_files(self):
        def git(*args):
            env = dict(os.environ, GIT_OPTIONAL_LOCKS='0')
            for name in ('GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_COMMON_DIR',
                         'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES'):
                env.pop(name, None)
            result = subprocess.run(['git', '-C', str(self.root), *args], capture_output=True, timeout=20, env=env)
            if result.returncode:
                raise Blocked('Git 状态无法可靠取得；不执行删除: ' + result.stderr.decode('utf-8', 'replace'))
            return result.stdout
        top = Path(os.fsdecode(git('rev-parse', '--show-toplevel')).strip()).resolve()
        if top != self.root.resolve():
            raise Blocked('Git 工作树根与项目不符')
        # HEAD union index: staged deletions remain protected, staged additions too.
        paths = git('ls-tree', '-r', '--name-only', '-z', 'HEAD') + git('ls-files', '--cached', '-z')
        return {os.path.normcase(os.fsdecode(p).replace('/', os.sep)) for p in paths.split(b'\0') if p}

    def activity(self):
        """Only live runs/consumers and their direct current inputs; no dependency closure."""
        active = set()
        for path in no_links(self.registry).glob('*.json'):
            row = read(no_links(path))
            if row.get('schema') != 1 or not isinstance(row.get('paths'), list):
                raise Blocked('活动登记损坏，保留产物')
            identities = [row.get('owner'), *row.get('children', [])]
            if any(not identity or self.runtime.alive(identity) for identity in identities):
                for key in row['paths']:
                    self.target(key)
                    active.add(key)
        return active

    def reason(self, key, s, data, tracked, active, now):
        if any(key == p or key.startswith(p + '/') for p in data['protect']):
            return '显式保护'
        normalized = os.path.normcase(key.replace('/', os.sep))
        if any(normalized == p or normalized.startswith(p + os.sep) for p in tracked):
            return 'Git HEAD/index'
        if any(key == p or key.startswith(p + '/') for p in active):
            return '正在使用'
        if s.st_nlink != 1:
            return '硬链接'
        age = now - s.st_mtime_ns
        if age > 7 * DAY:
            return None
        mark = data['marks'].get(key)
        if mark is not None and mark != fingerprint(s, self.root / key):
            return '可删标记已失效'
        if mark is not None and age > DAY:
            return None
        return '未满时间/未标可删'

    def scan(self, now=None):
        now = time.time_ns() if now is None else now
        data, tracked, active = self.records(), self.git_files(), self.activity()
        result = {'roots': [p.relative_to(self.root).as_posix() for p in self.roots],
                  'files': 0, 'bytes': 0, 'candidates': [], 'skipped': {}, 'protected': []}
        def walk(folder):
            no_links(folder)
            if not folder.exists():
                return
            for p in sorted(folder.iterdir()):
                key = p.relative_to(self.root).as_posix()
                try:
                    runtime.inside(self.root, key)
                    no_links(p)
                    s = p.lstat()
                    if stat.S_ISDIR(s.st_mode):
                        walk(p)
                        continue
                    if not stat.S_ISREG(s.st_mode):
                        raise Blocked('非普通文件')
                    result['files'] += 1
                    result['bytes'] += s.st_size
                    reason = self.reason(key, s, data, tracked, active, now)
                    if reason is None:
                        result['candidates'].append({'path': key, 'bytes': s.st_size, 'fingerprint': fingerprint(s, p)})
                    else:
                        result['skipped'][reason] = result['skipped'].get(reason, 0) + 1
                    for protected in data['protect']:
                        if key == protected or key.startswith(protected + '/'):
                            protected_bytes[protected] += s.st_size
                except (Blocked, OSError) as error:
                    reason = '路径/访问安全: ' + str(error)
                    result['skipped'][reason] = result['skipped'].get(reason, 0) + 1
        protected_bytes = dict.fromkeys(data['protect'], 0)
        for folder in self.roots:
            walk(folder)
        result['protected'] = [{'path': k, 'reason': v, 'bytes': protected_bytes[k]} for k, v in data['protect'].items()]
        result['candidateBytes'] = sum(c['bytes'] for c in result['candidates'])
        return result

    def delete(self, preview):
        deleted, skipped, released = 0, {}, 0
        touched = set()
        for candidate in preview['candidates']:
            key = candidate['path']
            try:
                with self.lock():
                    p = self.target(key)
                    with held_file(p) as remove:
                        s = p.stat()
                        if fingerprint(s, p) != candidate['fingerprint']:
                            raise Blocked('预览后文件已变化')
                        data = self.records()
                        reason = self.reason(key, s, data, self.git_files(), self.activity(), time.time_ns())
                        if reason:
                            raise Blocked(reason)
                        remove()
                    deleted += 1
                    released += s.st_size
                    parent = p.parent
                    while parent not in self.roots:
                        touched.add(parent)
                        parent = parent.parent
                    data['marks'].pop(key, None)
                    data['protect'].pop(key, None)
                    self.save(data)
            except (OSError, ValueError, RuntimeError) as error:
                reason = str(error)
                skipped[reason] = skipped.get(reason, 0) + 1
        # Only rmdir, never recursive removal; preserve protected/active/Git directories and roots.
        with self.lock():
            data, tracked, active = self.records(), self.git_files(), self.activity()
            # Finish only ancestors of files actually removed. Sweeping unrelated
            # fresh runs' empty directories would invalidate their input inventory.
            for p in sorted(touched, key=lambda p: len(p.parts), reverse=True):
                try:
                    self.target(p.relative_to(self.root).as_posix())
                    key = p.relative_to(self.root).as_posix()
                    if any(key == r or key.startswith(r + '/') or r.startswith(key + '/')
                           for r in [*data['protect'], *active]):
                        continue
                    if any(os.path.normcase(key.replace('/', os.sep)) == r or
                           r.startswith(os.path.normcase(key.replace('/', os.sep)) + os.sep) for r in tracked):
                        continue
                    with held_file(p, directory=True) as remove:
                        # Directory disposition succeeds only if it is still empty.
                        if not p.is_dir() or next(p.iterdir(), None) is not None:
                            continue
                        remove()
                except (OSError, Blocked):
                    pass
        return {'deleted': deleted, 'releasedBytes': released, 'skipped': skipped}


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser(description='专用可弃目录的手动逐文件清理')
    sub = parser.add_subparsers(dest='action', required=True)
    clean = sub.add_parser('clean', aliases=['preview'])
    clean.add_argument('--interactive', action='store_true')
    clean.add_argument('--json', action='store_true')
    for action in ('mark', 'protect', 'unprotect'):
        cmd = sub.add_parser(action)
        cmd.add_argument('paths', nargs='+')
        if action == 'protect':
            cmd.add_argument('--reason', required=True)
    args = parser.parse_args(argv)
    try:
        cleaner = Cleaner(runtime.project_root())
        if args.action not in {'clean', 'preview'}:
            cleaner.mark(args.action, args.paths, getattr(args, 'reason', None))
            print('已更新指定对象的标记。')
            return 0
        if args.action == 'preview' and args.interactive:
            raise Blocked('preview 始终只读；请通过根 BAT 交互确认')
        preview = cleaner.scan()
        if args.json:
            if args.interactive:
                raise Blocked('--json 只读预览，不能同时交互删除')
            print(json.dumps(preview, ensure_ascii=False, indent=2))
            return 0
        print('固定可弃根: ' + ', '.join(preview['roots']))
        print(f"共 {preview['files']} 文件 / {preview['bytes']} 字节；候选 {len(preview['candidates'])} 文件 / {preview['candidateBytes']} 字节")
        print('跳过原因: ' + json.dumps(preview['skipped'], ensure_ascii=False))
        print('显式保护对象/理由/占用: ' + json.dumps(preview['protected'], ensure_ascii=False))
        for item in preview['candidates']:
            print(f"候选 {item['bytes']} 字节  {item['path']}")
        if args.interactive and preview['candidates']:
            answer = input('永久删除以上候选？输入 DELETE 确认，其余输入取消: ')
            if answer == 'DELETE':
                print(json.dumps(cleaner.delete(preview), ensure_ascii=False))
            else:
                print('已取消，未删除。')
        else:
            print('仅预览，未删除。双击根目录「清理验证产物.bat」可手动确认。')
        return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError, subprocess.SubprocessError) as error:
        print('BLOCKED 清理: ' + str(error), file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
