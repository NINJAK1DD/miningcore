#!/usr/bin/env python3
"""Run a test command in a disposable loopback-only MIT Kerberos realm."""
import argparse
import os
from pathlib import Path
import secrets
import shutil
import signal
import socket
import subprocess
import tempfile
import time

REALM = 'MININGCORE.TEST'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tools', required=True, type=Path)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if os.geteuid() == 0 or not command:
        parser.error('Run as an ordinary lab user and provide -- COMMAND [ARGS...]')
    tools = args.tools.resolve(strict=True)
    os.umask(0o077)
    # Only generated scratch resources are removed; never modify host Kerberos config.
    with tempfile.TemporaryDirectory(prefix='miningcore-gss-') as temporary:
        root = Path(temporary)
        env = os.environ.copy()
        env.update(KRB5_CONFIG=str(root / 'krb5.conf'), KRB5_KDC_PROFILE=str(root / 'kdc.conf'),
                   KRB5CCNAME='FILE:' + str(root / 'client.ccache'), KRB5RCACHEDIR=str(root),
                   KRB5_CLIENT_KTNAME='FILE:' + str(root / 'no-default-client.keytab'),
                   KRB5_KTNAME='FILE:' + str(root / 'no-default-server.keytab'))
        env.pop('KRB5_TRACE', None)
        env.pop('LISTEN_PID', None)
        env.pop('LISTEN_FDS', None)
        env['LD_LIBRARY_PATH'] = str(tools / 'usr/lib/x86_64-linux-gnu') + (
            ':' + env['LD_LIBRARY_PATH'] if env.get('LD_LIBRARY_PATH') else '')
        env['PATH'] = str(tools / 'usr/bin') + ':' + str(tools / 'usr/sbin') + ':' + env['PATH']
        for name in ('krb5kdc', 'kdb5_util', 'kadmin.local', 'kinit', 'klist'):
            if not shutil.which(name, path=env['PATH']):
                raise RuntimeError('Missing Kerberos lab tool: ' + name)
        with socket.socket() as probe:
            probe.bind(('127.0.0.1', 0))
            port = probe.getsockname()[1]
        (root / 'krb5.conf').write_text(f'''[libdefaults]
 default_realm = {REALM}
 dns_lookup_kdc = false
 dns_lookup_realm = false
 rdns = false
 dns_canonicalize_hostname = false
 qualify_shortname = ""
 udp_preference_limit = 1
[realms]
 {REALM} = {{
  kdc = 127.0.0.1:{port}
 }}
[domain_realm]
 localhost = {REALM}
 127.0.0.1 = {REALM}
''')
        (root / 'kdc.conf').write_text(f'''[kdcdefaults]
 kdc_listen = 127.0.0.1:{port}
 kdc_tcp_listen = 127.0.0.1:{port}
[realms]
 {REALM} = {{
  database_name = {root}/principal
  key_stash_file = {root}/stash
  acl_file = {root}/kadm5.acl
  max_life = 1h
 }}
[dbmodules]
 db_module_dir = {tools}/usr/lib/x86_64-linux-gnu/krb5/plugins/kdb
''')
        (root / 'kadm5.acl').write_text('')

        def run(argv, **kwargs):
            # Setup output stays private; failure reports do not print credentials.
            result = subprocess.run(argv, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    timeout=30, **kwargs)
            with (root / 'setup.log').open('ab') as log:
                log.write(result.stdout)
            if result.returncode:
                raise RuntimeError('Kerberos setup command failed: ' + argv[0])
            return result

        master = secrets.token_urlsafe(40)
        run(['kdb5_util', 'create', '-s', '-r', REALM], input=(master + '\n' + master + '\n').encode())
        for principal, keytab in [('postgres/localhost', 'server.keytab'),
                                  ('postgres/127.0.0.1', 'server.keytab'),
                                  ('miningcore', 'client.keytab'), ('denied', 'denied.keytab')]:
            run(['kadmin.local', '-r', REALM, '-q', f'addprinc -randkey {principal}@{REALM}'])
            run(['kadmin.local', '-r', REALM, '-q', f'ktadd -k {root / keytab} {principal}@{REALM}'])
        processes = []
        try:
            with (root / 'kdc.log').open('wb') as log:
                kdc = subprocess.Popen(['krb5kdc', '-n', '-r', REALM], env=env,
                                       stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                processes.append(kdc)
                deadline = time.monotonic() + 10
                while True:
                    if kdc.poll() is not None:
                        raise RuntimeError('Disposable KDC exited during startup')
                    try:
                        with socket.create_connection(('127.0.0.1', port), timeout=.2):
                            break
                    except OSError:
                        if time.monotonic() >= deadline:
                            raise RuntimeError('Disposable KDC did not become ready')
                        time.sleep(.1)
                run(['kinit', '-k', '-t', str(root / 'client.keytab'), 'miningcore@' + REALM])
                run(['kinit', '-c', 'FILE:' + str(root / 'denied.ccache'), '-k', '-t',
                     str(root / 'denied.keytab'), 'denied@' + REALM])
                run(['klist', '-s'])
                env.update(MININGCORE_TEST_GSS_ROOT=str(root), MININGCORE_TEST_GSS_REALM=REALM)
                child = subprocess.Popen(command, env=env, start_new_session=True)
                processes.append(child)
                return child.wait()
        finally:
            for process in reversed(processes):
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait()


def interrupted(_signum, _frame):
    raise KeyboardInterrupt()


if __name__ == '__main__':
    # Translate termination into normal cleanup of our own process groups/resources.
    signal.signal(signal.SIGTERM, interrupted)
    raise SystemExit(main())
