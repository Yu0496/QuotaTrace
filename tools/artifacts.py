#!/usr/bin/env python3
"""Project-owned manual tools. No knowledge-base lookup or automatic cleanup."""
import argparse
import sys

sys.dont_write_bytecode = True


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8')
    args = list(sys.argv[1:] if argv is None else argv)
    if args and args[0] in {'clean', 'preview', 'mark', 'protect', 'unprotect'}:
        from artifact_clean import main as clean
        return clean(args)
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='action', required=True)
    cmd = sub.add_parser('run', help='Register a synchronous command; never cleans')
    cmd.add_argument('--input', action='append', default=[], help='Direct selected disposable input path')
    cmd.add_argument('command', nargs=argparse.REMAINDER)
    cmd = sub.add_parser('claim', help='Publish dynamic inputs before opening them inside run')
    cmd.add_argument('paths', nargs='+')
    for name in ('clean', 'preview', 'mark', 'protect', 'unprotect'):
        sub.add_parser(name, add_help=False, help='See this action --help')
    args = parser.parse_args(args)
    import artifact_runtime as runtime
    try:
        if args.action == 'claim':
            runtime.claim(args.paths)
            return 0
        command = args.command[1:] if args.command[:1] == ['--'] else args.command
        return runtime.run(command, args.input)
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        print('BLOCKED project tool: ' + str(error), file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
