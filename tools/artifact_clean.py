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
            h = k.CreateFileW(str(p), 0x10000 if p == path else 1, 1 if isfile else 3,
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


class StateUnavailable(Blocked):
    pass


class ContentCache:
    """Reuse parsing only after comparing freshly locked bytes, never metadata."""
    def __init__(self):
        self.values = {}

    def get(self, key, contents, load):
        previous = self.values.get(key)
        if previous is None or previous[0] != contents:
            previous = (contents, load())
            self.values[key] = previous
        return previous[1]


class StableFiles:
    """Per-target read handles, denying writes/replacement through target deletion.

    Ancestor handles are deduplicated within this scope and deny renames. The
    project transaction lock serializes registry/claim publication and optional
    missing files. Existing files additionally exclude uncooperative writers.
    """
    def __init__(self):
        if os.name != 'nt':
            raise Blocked('实际删除只支持 Windows')
        from ctypes import wintypes as w
        self.w = w
        self.api = ctypes.WinDLL('kernel32', use_last_error=True)
        self.api.CreateFileW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, ctypes.c_void_p, w.DWORD, w.DWORD, w.HANDLE]
        self.api.CreateFileW.restype = w.HANDLE
        self.api.CloseHandle.argtypes = [w.HANDLE]
        self.api.GetFileInformationByHandle.argtypes = [w.HANDLE, ctypes.c_void_p]
        self.api.ReadFile.argtypes = [w.HANDLE, ctypes.c_void_p, w.DWORD, ctypes.POINTER(w.DWORD), ctypes.c_void_p]
        class Info(ctypes.Structure):
            _fields_ = [('attrs', w.DWORD), ('created', w.FILETIME), ('accessed', w.FILETIME),
                        ('written', w.FILETIME), ('volume', w.DWORD), ('high', w.DWORD),
                        ('low', w.DWORD), ('links', w.DWORD), ('indexHigh', w.DWORD), ('indexLow', w.DWORD)]
        self.Info, self.handles, self.directories = Info, [], set()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        for handle in reversed(self.handles):
            self.api.CloseHandle(handle)

    def open(self, path, directory=False):
        # FILE_LIST_DIRECTORY participates in Windows sharing checks. A zero
        # desired-access metadata handle does not reliably exclude renames.
        handle = self.api.CreateFileW(str(path), 1 if directory else 0x80000000,
                                      3 if directory else 1, None, 3, 0x02200000, None)
        if handle == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        self.handles.append(handle)
        info = self.Info()
        if not self.api.GetFileInformationByHandle(handle, ctypes.byref(info)):
            raise ctypes.WinError(ctypes.get_last_error())
        if info.attrs & 0x400 or bool(info.attrs & 0x10) != directory or (not directory and info.links != 1):
            raise Blocked('状态路径含链接或非普通文件: ' + str(path))
        return handle, info

    def directory(self, path):
        # Reads in this scope repeatedly name the same already pinned parents.
        if path in self.directories:
            return
        path = Path(os.path.abspath(path))
        if path in self.directories:
            return
        if path.parent != path:
            self.directory(path.parent)
        self.open(path, directory=True)
        self.directories.add(path)

    def read(self, path, optional=False):
        self.directory(path.parent)
        try:
            handle, info = self.open(path)
        except OSError as error:
            if optional and getattr(error, 'winerror', None) in (2, 3):
                return None
            raise
        # The same locked handle already supplied size with its identity/type.
        size = (info.high << 32) | info.low
        if not 0 <= size <= 16 * 1024 * 1024:
            raise Blocked('状态文件大小异常: ' + str(path))
        buffer = ctypes.create_string_buffer(size)
        count = self.w.DWORD()
        if not self.api.ReadFile(handle, buffer, size, ctypes.byref(count), None):
            raise ctypes.WinError(ctypes.get_last_error())
        if count.value != size:
            raise Blocked('状态文件未能完整读取: ' + str(path))
        return buffer.raw

    def json_paths(self, folder):
        try:
            self.directory(folder)
        except OSError as error:
            if getattr(error, 'winerror', None) not in (2, 3):
                raise
            # Admission/consumer/claim publishers hold the same transaction lock.
            return []
        return sorted(folder.glob('*.json'))


class Cleaner:
    def __init__(self, root=ROOT):
        self.root = Path(os.path.abspath(root))
        config = read(no_links(self.root / 'tools/artifact-clean.json'))
        self.roots, self.state, self.activity_config, self.registry = validate_config(self.root, config)
        self.runtime = runtime

    def lock(self):
        # Same short transaction lock as run admission and preview consumers.
        return self.runtime.locked(self.root / '.validation/state/transaction.lock')

    def target(self, key, *, check_links=True):
        if not isinstance(key, str) or '\\' in key or ':' in key:
            raise Blocked('只接受项目内正斜线相对路径')
        rel = PurePosixPath(key)
        if rel.is_absolute() or '..' in rel.parts or rel.as_posix() != key:
            raise Blocked('非法相对路径: ' + str(key))
        p = runtime.inside(self.root, key, check_links=check_links)
        if not any(p != base and p.is_relative_to(base) for base in self.roots):
            raise Blocked('目标不在指定可弃根内: ' + key)
        return p

    def records(self, data=None):
        if data is None:
            data = read(no_links(self.state), {'schema': 1, 'marks': {}, 'protect': {}})
        if (not isinstance(data, dict) or set(data) != {'schema', 'marks', 'protect'} or data['schema'] != 1
                or not isinstance(data['marks'], dict) or not isinstance(data['protect'], dict)):
            raise Blocked('损坏的清理标记表')
        for key, value in data['marks'].items():
            # Records describe paths, not open objects. Check the actual target's
            # ancestors at use time; an unrelated stale/link mark cannot affect A.
            self.target(key, check_links=False)
            if not isinstance(value, list) or len(value) != 5 or any(type(v) is not int for v in value):
                raise Blocked('损坏的可删标记')
        for key, reason in data['protect'].items():
            self.target(key, check_links=False)
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

    def git(self, *args):
        env = dict(os.environ, GIT_OPTIONAL_LOCKS='0', GIT_NO_REPLACE_OBJECTS='1')
        for name in ('GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_COMMON_DIR',
                     'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES'):
            env.pop(name, None)
        result = subprocess.run(['git', '-C', str(self.root), *args], capture_output=True, timeout=20, env=env)
        if result.returncode:
            raise Blocked('Git 状态无法可靠取得；不执行删除: ' + result.stderr.decode('utf-8', 'replace'))
        return result.stdout

    def stable_state(self, files, cache):
        try:
            body = files.read(self.state, optional=True)
            body = b'{"schema":1,"marks":{},"protect":{}}' if body is None else body
            data = cache.get('marks', body, lambda: self.records(json.loads(body)))
            gitdir = self.root / '.git'
            files.directory(gitdir)
            if files.read(gitdir / 'commondir', optional=True) is not None:
                raise Blocked('清理内容缓存暂不支持 linked worktree')
            head = files.read(gitdir / 'HEAD')
            index = files.read(gitdir / 'index')
            config = files.read(gitdir / 'config')
            reference = None
            if head.startswith(b'ref: '):
                ref = head[5:].strip().decode('utf-8')
                parts = PurePosixPath(ref)
                if (not ref.startswith('refs/heads/') or parts.as_posix() != ref
                        or '..' in parts.parts or '\\' in ref or ':' in ref):
                    raise Blocked('未知 Git HEAD 引用布局')
                # An existing loose branch is pinned. Packed-only branches and
                # split indexes fail closed rather than inventing a missing-file
                # lock or depending on notifications about future ref creation.
                reference = files.read(gitdir.joinpath(*parts.parts))
            object_id = (reference if reference is not None else head).strip()
            if len(object_id) not in (40, 64) or any(c not in b'0123456789abcdefABCDEF' for c in object_id):
                raise Blocked('未知 Git 引用链；只支持直接对象ID')
            def tracked_files():
                if self.git('rev-parse', '--shared-index-path').strip():
                    raise Blocked('清理内容缓存暂不支持 split index')
                return self.git_files()
            tracked = cache.get('git', (head, index, config, reference), tracked_files)
            active = self.activity(files=files, cache=cache)
            return data, tracked, active
        except (OSError, ValueError, RuntimeError, KeyError, TypeError, subprocess.SubprocessError) as error:
            raise StateUnavailable('无法稳定读取删除状态: ' + str(error)) from error

    def git_files(self):
        top = Path(os.fsdecode(self.git('rev-parse', '--show-toplevel')).strip()).resolve()
        if top != self.root.resolve():
            raise Blocked('Git 工作树根与项目不符')
        # HEAD union index: staged deletions remain protected, staged additions too.
        paths = self.git('ls-tree', '-r', '--name-only', '-z', 'HEAD') + self.git('ls-files', '--cached', '-z')
        return {os.path.normcase(os.fsdecode(p).replace('/', os.sep)) for p in paths.split(b'\0') if p}

    def activity(self, files=None, cache=None):
        """Only live runs/consumers and their direct current inputs; no dependency closure."""
        if files and self.activity_config['kind'] != 'local':
            files.directory(self.registry)
        def paths(folder):
            return files.json_paths(folder) if files else no_links(folder).glob('*.json')
        def row_at(path):
            if not files:
                return read(no_links(path))
            body = files.read(path)
            return cache.get(('json', path), body, lambda: json.loads(body))
        if self.activity_config['kind'] == 'local':
            active = set()
            for path in paths(self.registry):
                row = row_at(path)
                if row.get('schema') != 1 or not isinstance(row.get('paths'), list):
                    raise Blocked('活动登记损坏，保留产物')
                identities = [row.get('owner'), *row.get('children', [])]
                if any(not identity or runtime.alive(identity) for identity in identities):
                    for key in row['paths']:
                        self.target(key)
                        active.add(key)
            return active
        active = set()
        folder = self.registry / 'runs'
        if not files:
            no_links(folder)
        for path in paths(folder):
            row = row_at(path)
            if row.get('state') != 'running':
                continue
            identities = [row.get('owner'), *row.get('children', [])]
            if any(not identity or self.runtime.alive(identity) for identity in identities):
                active.add(row['id'])
                active.update(row.get('inputs', []))
                journals = self.root / '.validation/runs' / row['id'] / '.artifact-inputs'
                if not files:
                    no_links(journals)
                for journal in paths(journals):
                    active.update(row_at(journal).get('runs', []))
        for path in paths(self.registry / 'consumers'):
            row = row_at(path)
            if self.runtime.alive(row['identity']):
                active.add(row['runId'])
        return {'.validation/runs/' + run for run in active}

    @staticmethod
    def git_protected(key, tracked):
        # Same equal-or-descendant relation as the old full-set scan. Git's
        # normalized paths are a set: probe only this path and its ancestors.
        normalized = os.path.normcase(key.replace('/', os.sep))
        while normalized:
            if normalized in tracked:
                return True
            normalized = normalized.rpartition(os.sep)[0]
        return False

    def reason(self, key, s, data, tracked, active, now, fingerprint_of=None):
        if any(key == p or key.startswith(p + '/') for p in data['protect']):
            return '显式保护'
        if self.git_protected(key, tracked):
            return 'Git HEAD/index'
        if any(key == p or key.startswith(p + '/') for p in active):
            return '正在使用'
        if s.st_nlink != 1:
            return '硬链接'
        age = now - s.st_mtime_ns
        if age > 7 * DAY:
            return None
        mark = data['marks'].get(key)
        if mark is not None and mark != (fingerprint_of() if fingerprint_of else fingerprint(s, self.root / key)):
            return '可删标记已失效'
        if mark is not None and age > DAY:
            return None
        return '未满时间/未标可删'

    @staticmethod
    def error_reason(error):
        # Keep the default summary bounded even when many paths are inaccessible.
        if isinstance(error, OSError):
            return f"访问失败（errno={error.errno}, winerror={getattr(error, 'winerror', None)}）"
        return str(error).split(': ', 1)[0]

    def scan(self, now=None, progress=None):
        now = time.time_ns() if now is None else now
        if progress: progress('读取清理标记', 0)
        data = self.records()
        if progress: progress('读取 Git 状态', 0)
        tracked = self.git_files()
        if progress: progress('读取活动登记', 0)
        active = self.activity()
        if progress: progress('开始扫描文件', 0)
        result = {'roots': [p.relative_to(self.root).as_posix() for p in self.roots],
                  'files': 0, 'bytes': 0, 'candidates': [], 'skipped': {}, 'protected': []}
        def walk(folder, pinned=()):
            # Pin directory ancestry for this recursive scope, so leaf checks
            # cannot reuse a stale ancestor after a rename/junction swap.
            with contextlib.ExitStack() as scope:
                if os.name == 'nt':
                    files = scope.enter_context(StableFiles())
                    # Borrow only ancestors whose handles the calling recursive
                    # scope still owns. Sibling handles are never cached.
                    files.directories.update(pinned)
                    try:
                        files.directory(folder)
                    except FileNotFoundError:
                        return
                else:
                    no_links(folder)
                    if not folder.exists():
                        return
                walk_entries(folder, files.directories if os.name == 'nt' else ())

        def walk_entries(folder, pinned):
            for p in sorted(folder.iterdir()):
                key = p.relative_to(self.root).as_posix()
                try:
                    runtime.inside(self.root, key, check_links=False)
                    if os.name != 'nt':
                        no_links(p)
                    s = p.lstat()
                    if stat.S_ISLNK(s.st_mode) or getattr(s, 'st_file_attributes', 0) & 0x400:
                        raise Blocked('链接/reparse 路径: ' + str(p))
                    if stat.S_ISDIR(s.st_mode):
                        walk(p, pinned)
                        continue
                    if not stat.S_ISREG(s.st_mode):
                        raise Blocked('非普通文件')
                    result['files'] += 1
                    if progress: progress('扫描文件', result['files'])
                    result['bytes'] += s.st_size
                    # Reuse only this leaf's observation within this iteration;
                    # deletion still obtains a fresh fingerprint under its lock.
                    observed = []
                    def snapshot():
                        if not observed:
                            observed.append(fingerprint(s, p))
                        return observed[0]
                    reason = self.reason(key, s, data, tracked, active, now, snapshot)
                    if reason is None:
                        result['candidates'].append({'path': key, 'bytes': s.st_size, 'fingerprint': snapshot()})
                    else:
                        result['skipped'][reason] = result['skipped'].get(reason, 0) + 1
                    for protected in data['protect']:
                        if key == protected or key.startswith(protected + '/'):
                            protected_bytes[protected] += s.st_size
                except (Blocked, OSError) as error:
                    reason = '路径/访问安全: ' + self.error_reason(error)
                    result['skipped'][reason] = result['skipped'].get(reason, 0) + 1
        protected_bytes = dict.fromkeys(data['protect'], 0)
        for folder in self.roots:
            walk(folder)
        result['protected'] = [{'path': k, 'reason': v, 'bytes': protected_bytes[k]} for k, v in data['protect'].items()]
        result['candidateBytes'] = sum(c['bytes'] for c in result['candidates'])
        return result

    def delete(self, preview, progress=None):
        deleted, skipped, released = 0, {}, 0
        touched = set()
        removed_marks = {}
        cache = ContentCache()
        try:
            for processed, candidate in enumerate(preview['candidates'], 1):
                key = candidate['path']
                try:
                    with self.lock():
                        try:
                            # held_file validates and pins every ancestor and
                            # the leaf before any authoritative state is read.
                            p = self.target(key, check_links=False)
                            with StableFiles() as files, held_file(p) as remove:
                                data, tracked, active = self.stable_state(files, cache)
                                s = p.stat()
                                current_fingerprint = fingerprint(s, p)
                                if current_fingerprint != candidate['fingerprint']:
                                    raise Blocked('预览后文件已变化')
                                reason = self.reason(key, s, data, tracked, active, time.time_ns(), lambda: current_fingerprint)
                                if reason:
                                    raise Blocked(reason)
                                remove()
                            deleted += 1
                            released += s.st_size
                            parent = p.parent
                            while parent not in self.roots:
                                touched.add(parent)
                                parent = parent.parent
                            if key in data['marks']:
                                removed_marks[key] = data['marks'][key]
                        except StateUnavailable:
                            raise
                        except (OSError, ValueError, RuntimeError) as error:
                            reason = self.error_reason(error)
                            skipped[reason] = skipped.get(reason, 0) + 1
                finally:
                    if progress:
                        progress(processed, len(preview['candidates']), deleted, released, sum(skipped.values()))
        finally:
            # Also checkpoint completed deletions on Ctrl+C. A crash before
            # this point leaves harmless missing-file marks; reruns are safe.
            if removed_marks:
                with self.lock():
                    self.forget_deleted(removed_marks)
        # Only rmdir actual deleted files' ancestors, never a recursive sweep.
        for p in sorted(touched, key=lambda p: len(p.parts), reverse=True):
            with self.lock():
                try:
                    self.target(p.relative_to(self.root).as_posix())
                    key = p.relative_to(self.root).as_posix()
                    with StableFiles() as files, held_file(p, directory=True) as remove:
                        data, tracked, active = self.stable_state(files, cache)
                        if any(key == r or key.startswith(r + '/') or r.startswith(key + '/')
                               for r in [*data['protect'], *active]):
                            continue
                        normalized = os.path.normcase(key.replace('/', os.sep))
                        if any(normalized == r or r.startswith(normalized + os.sep) for r in tracked):
                            continue
                        if not p.is_dir() or next(p.iterdir(), None) is not None:
                            continue
                        remove()
                except StateUnavailable:
                    raise
                except (OSError, Blocked):
                    pass
        return {'deleted': deleted, 'releasedBytes': released, 'skipped': skipped}

    def forget_deleted(self, removed_marks):
        data = self.records()
        # Only remove unchanged marks for files actually deleted; concurrent
        # writers may have installed a new mark after an earlier deletion.
        for key, mark in removed_marks.items():
            if data['marks'].get(key) == mark and not self.target(key).exists():
                data['marks'].pop(key, None)
        self.runtime.write_json(self.state, data)


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser(description='专用可弃目录的手动逐文件清理')
    sub = parser.add_subparsers(dest='action', required=True)
    clean = sub.add_parser('clean', aliases=['preview'])
    clean.add_argument('--interactive', action='store_true')
    clean.add_argument('--json', action='store_true')
    clean.add_argument('--details', action='store_true')
    for action in ('mark', 'protect', 'unprotect'):
        cmd = sub.add_parser(action)
        cmd.add_argument('paths', nargs='+')
        if action == 'protect':
            cmd.add_argument('--reason', required=True)
    args = parser.parse_args(argv)
    deletion = [0, 0, 0, 0, 0]
    try:
        started = time.monotonic()
        last_progress = [0.0]
        def show(message, force=False):
            now = time.monotonic()
            if force or now - last_progress[0] >= 2.0:
                print(f"\r{message}  用时 {now - started:.1f} 秒", flush=True)
                last_progress[0] = now
        if args.action in {'clean', 'preview'} and not args.json:
            show('正在初始化清理程序', True)
        cleaner = Cleaner(runtime.project_root())
        if args.action not in {'clean', 'preview'}:
            cleaner.mark(args.action, args.paths, getattr(args, 'reason', None))
            print('已更新指定对象的标记。')
            return 0
        if args.action == 'preview' and args.interactive:
            raise Blocked('preview 始终只读；请通过根 BAT 交互确认')
        preview = cleaner.scan(progress=None if args.json else
                               lambda stage, count: show(f'{stage}：已检查 {count} 个文件', count == 0))
        if args.json:
            if args.interactive:
                raise Blocked('--json 只读预览，不能同时交互删除')
            print(json.dumps(preview, ensure_ascii=False, indent=2))
            return 0
        print('固定可弃根: ' + ', '.join(preview['roots']))
        show(f"扫描完成：已检查 {preview['files']} 个文件", True)
        print(f"共 {preview['files']} 文件 / {preview['bytes']} 字节；候选 {len(preview['candidates'])} 文件 / {preview['candidateBytes']} 字节")
        print('跳过原因: ' + json.dumps(preview['skipped'], ensure_ascii=False))
        totals = {root: [0, 0] for root in preview['roots']}
        for item in preview['candidates']:
            root = next(root for root in preview['roots'] if item['path'].startswith(root + '/'))
            totals[root][0] += 1
            totals[root][1] += item['bytes']
        for root, (count, size) in totals.items():
            print(f'{root}：候选 {count} 个 / {size} 字节')
        if args.details:
            print('显式保护路径/原因/占用: ' + json.dumps(preview['protected'], ensure_ascii=False))
            for item in preview['candidates']:
                print(f"候选 {item['bytes']} 字节  {item['path']}")
        if args.interactive and preview['candidates']:
            try:
                answer = input('永久删除以上候选？输入 DELETE 确认，其余输入取消: ')
            except EOFError:
                answer = ''
            if answer == 'DELETE':
                show('开始删除：检查状态并尝试获取事务锁', True)
                def deleting(done, total, deleted, size, skipped):
                    deletion[:] = [done, total, deleted, size, skipped]
                    show(f'处理 {done}/{total}，已删除 {deleted} 个 / {size} 字节，跳过或失败 {skipped}')
                result = cleaner.delete(preview, progress=deleting)
                show(f"完成：已删除 {result['deleted']} 个 / {result['releasedBytes']} 字节，跳过或失败 {sum(result['skipped'].values())} 个", True)
                if result['skipped']: print('跳过原因: ' + json.dumps(result['skipped'], ensure_ascii=False))
            else:
                print('已取消，未删除。')
        else:
            print('仅预览，未删除。双击根目录「清理验证产物.bat」可手动确认。')
        return 0
    except KeyboardInterrupt:
        print(f'\n已中断；已确认删除 {deletion[2]} 个 / {deletion[3]} 字节，不会恢复；下次可重新扫描。',
              file=sys.stderr, flush=True)
        return 130
    except (OSError, ValueError, RuntimeError, KeyError, TypeError, subprocess.SubprocessError) as error:
        print(f'BLOCKED 清理: {error}；已确认删除 {deletion[2]} 个 / {deletion[3]} 字节，下次可重新扫描。',
              file=sys.stderr, flush=True)
        return 2


if __name__ == '__main__':
    sys.exit(main())
