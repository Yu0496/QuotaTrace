"""Run a synchronous preview test with its optional PNG in the active run's work directory."""
import os
from pathlib import Path
import subprocess
import sys


def main():
    work = os.environ.get('ARTIFACT_WORK_ROOT')
    command = sys.argv[1:]
    if not work or not Path(work).is_dir() or not command:
        print('Run via tools/artifacts.py run -- python -B tools/run_preview_test.py <command>', file=sys.stderr)
        return 2
    env = dict(os.environ, TEST_PREVIEW_DIR=work)
    return subprocess.call(command, env=env)


if __name__ == '__main__':
    sys.exit(main())
