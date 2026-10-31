"""Local activity and transaction helpers; no automatic cleanup."""
import contextlib
import ctypes
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import sys
import uuid

TOOL_VERSION = '2026-10-31.manual.2'
sys.dont_write_bytecode = True

LOCAL_ENTRY_SHA256 = '0cf3edb562d944529f0132f4c808e35e21fc2f22d25cd715337eb3700177ba8c'



class Blocked(ValueError):
    pass


def project_root():
    return Path(os.path.abspath(Path(__file__).parent.parent))


def safe_path(path):
    path = Path(os.path.abspath(path))
    for p in [path, *path.parents]:
        try:
            s = p.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(s.st_mode) or getattr(s, 'st_file_attributes', 0) & 0x400:
            raise Blocked('链接/reparse 路径: ' + str(p))
    return path


def inside(base, relative):
    if (not isinstance(relative, str) or not relative or '\\' in relative or
            any(c in relative for c in ':*?"<>|\x00') or any(ord(c) < 32 for c in relative)):
        raise Blocked('只接受项目内正斜线相对路径')
    rel = PurePosixPath(relative)
    if (rel.is_absolute() or rel.as_posix() != relative or any(
            part in {'.', '..'} or part.endswith((' ', '.')) or part.casefold() == '.git' or
            re.fullmatch(r'(?i)(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?', part)
            for part in rel.parts)):
        raise Blocked('非法相对路径: ' + relative)
    p = safe_path(Path(base).joinpath(*rel.parts))
    if p == base or not p.is_relative_to(base):
        raise Blocked('路径越界: ' + relative)
    return p


def source_digest(path):
    # Small bridge files only; never hashes project source trees or output files.
    path = safe_path(path)
    if not path.is_file() or path.stat().st_nlink != 1:
        raise Blocked('运行桥须为独立普通文件: ' + str(path))
    return hashlib.sha256(path.read_text('utf-8').encode('utf-8')).hexdigest()


def verify_local_bridge(root):
    if (root / 'tools/artifacts.json').exists():
        raise Blocked('发现旧运行配置；须先完成空闲切换和实际输出/输入适配，不能把 local 空表当无活动')
    bridge = root / 'tools/artifacts.py'
    if bridge.exists() and source_digest(bridge) != LOCAL_ENTRY_SHA256:
        raise Blocked('未知运行桥；不能默认 local 活动登记，须先完成项目接入')


def write_json(path, row):
    path = safe_path(path)
    safe_path(path.parent).mkdir(parents=True, exist_ok=True)
    temp = safe_path(path.with_suffix('.writing'))
    with temp.open('x', encoding='utf-8', newline='\n') as stream:
        stream.write(json.dumps(row, ensure_ascii=False, indent=2) + '\n')
    os.replace(temp, path)


def process_identity(pid):
    """None means unknown (protect); False means proven absent."""
    if os.name == 'nt':
        from ctypes import wintypes as w
        k = ctypes.WinDLL('kernel32', use_last_error=True)
        k.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
        k.OpenProcess.restype = w.HANDLE
        k.GetProcessTimes.argtypes = [w.HANDLE, *([ctypes.POINTER(w.FILETIME)] * 4)]
        k.GetExitCodeProcess.argtypes = [w.HANDLE, ctypes.POINTER(w.DWORD)]
        k.CloseHandle.argtypes = [w.HANDLE]
        handle = k.OpenProcess(0x1000, False, pid)
        if not handle:
            return False if ctypes.get_last_error() == 87 else None
        times = [w.FILETIME() for _ in range(4)]
        try:
            exit_code = w.DWORD()
            if not k.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
                return None
            if exit_code.value != 259:
                return False
            if not k.GetProcessTimes(handle, *[ctypes.byref(x) for x in times]):
                return None
            return str((times[0].dwHighDateTime << 32) | times[0].dwLowDateTime)
        finally:
            k.CloseHandle(handle)
    try:
        # starttime comes after the final ')' even for names containing spaces.
        return Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()[19]
    except FileNotFoundError:
        return False
    except OSError:
        return None


def owner():
    value = process_identity(os.getpid())
    if not value:
        raise Blocked('Cannot bind own PID/start time')
    return {'pid': os.getpid(), 'started': value}


def alive(identity):
    if not identity or identity.get('started') is None:
        return True
    value = process_identity(identity['pid'])
    return value is None or (value is not False and value == identity['started'])


@contextlib.contextmanager
def locked(path):
    path = safe_path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('a+b') as f:
        if os.fstat(f.fileno()).st_nlink != 1:
            raise Blocked('事务锁不能是硬链接')
        try:
            f.seek(0)
            if not f.read(1):
                f.write(b'0'); f.flush()
            f.seek(0)
            if os.name == 'nt':
                import msvcrt
                msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(f.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as e:
            raise Blocked('Artifact transaction busy; retry after it completes') from e
        try:
            yield
        finally:
            f.seek(0)
            if os.name == 'nt':
                msvcrt.locking(f.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(f.fileno(), fcntl.LOCK_UN)


def claim(paths, root=None, lease=None):
    """Call before opening a dynamic input; publish under the deletion transaction lock."""
    from artifact_clean import Cleaner
    cleaner = Cleaner(project_root() if root is None else root)
    lease = lease or os.environ.get('ARTIFACT_LEASE_ID', '')
    if not re.fullmatch(r'[0-9a-f]{32}', lease):
        raise Blocked('动态输入须在本项目 run 上下文内登记')
    record = safe_path(cleaner.registry / (lease + '.json'))
    with cleaner.lock():
        row = json.loads(record.read_text('utf-8'))
        if row.get('id') != lease or not alive(row['owner']):
            raise Blocked('活动上下文已结束')
        selected = []
        for key in paths:
            p = cleaner.target(key)
            if not p.exists():
                raise Blocked('输入已被清理或不存在: ' + key)
            selected.append(p.resolve().relative_to(cleaner.root.resolve()).as_posix())
        row['paths'] = sorted(set(row['paths'] + selected))
        write_json(record, row)
    return selected


def run(command, inputs=(), root=None):
    """Only registers a synchronous command's output/selected inputs. Never cleans."""
    from artifact_clean import Cleaner
    cleaner = Cleaner(project_root() if root is None else root)
    if not command:
        raise Blocked('run -- 后必须有实际命令')
    run_id = uuid.uuid4().hex
    key = cleaner.roots[0].relative_to(cleaner.root).as_posix() + '/' + run_id
    record = safe_path(cleaner.registry / (run_id + '.json'))
    row = {'schema': 1, 'id': run_id, 'owner': owner(), 'children': [None], 'paths': [key]}
    child = None
    with cleaner.lock():
        # No historical dependency graph; only paths selected for this activity.
        for selected in inputs:
            p = cleaner.target(selected)
            if not p.exists():
                raise Blocked('输入已被清理或不存在: ' + selected)
            row['paths'].append(p.resolve().relative_to(cleaner.root.resolve()).as_posix())
        output = cleaner.target(key)
        output.mkdir(parents=True)
        work, build = output / 'work', output / 'build'
        work.mkdir(); build.mkdir()
        write_json(record, row)
        env = dict(os.environ, ARTIFACT_LEASE_ID=run_id, ARTIFACT_RUN_DIR=str(output),
                   ARTIFACT_WORK_ROOT=str(work), ARTIFACT_BUILD_ROOT=str(build))
        # Child inherits stdout/stderr: no unbounded log/evidence archive.
        startup_failed = False
        try:
            child = subprocess.Popen(command, cwd=cleaner.root, env=env)
            row['children'] = [{'pid': child.pid, 'started': process_identity(child.pid)}]
            write_json(record, row)
        except (OSError, ValueError, TypeError):
            startup_failed = child is None
            raise
        finally:
            if startup_failed:
                # Popen failed before returning a child: no process to protect.
                # Still in the admission transaction; retain all output/input data.
                safe_path(record).unlink(missing_ok=True)

    try:
        return child.wait()
    finally:
        # On interruption keep the record while a known/unknown child may live.
        if child.poll() is not None:
            with cleaner.lock():
                safe_path(record).unlink(missing_ok=True)
