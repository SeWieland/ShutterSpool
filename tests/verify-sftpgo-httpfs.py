"""Characterize the pinned Community edge with synthetic bytes, not ShutterSpool durability.

Run: python tests/verify-sftpgo-httpfs.py (Docker Desktop and OpenSSL required).
Only disposable probe resources are removed; no existing service or media is touched.
"""
import ftplib
import http.server
import io
import json
import secrets
import socket
import ssl
import subprocess
import tempfile
import threading
import time
import urllib.parse
from pathlib import Path
from image_pins import load_image_pins

IMAGE = load_image_pins()['camera-edge']
KEY = secrets.token_hex(32)
PASSWORD = secrets.token_hex(16)
AUTH_PATH = '/auth'
events, files, dirs, flags_seen = [], {}, {'/'}, []
auth_key_checks = []
expected_key = KEY
fail_hook = False
hold_hook = threading.Event()
hold_hook.set()
hook_started = threading.Event()

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        # Mute routine request logs so the probe results stay visible.
        pass
    def body(self):
        if self.headers.get('Transfer-Encoding') == 'chunked':
            chunks = []
            while True:
                size = int(self.rfile.readline().split(b';')[0], 16)
                if size == 0:
                    self.rfile.readline()
                    return b''.join(chunks)
                chunks.append(self.rfile.read(size))
                self.rfile.read(2)
        return self.rfile.read(int(self.headers.get('Content-Length', 0)))
    def reply(self, status, value=None):
        data = json.dumps(value).encode() if value is not None else b''
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    def do_POST(self):
        correct_key = self.headers.get('X-API-KEY') == expected_key
        if self.path == AUTH_PATH:
            auth_key_checks.append(correct_key)
        if not correct_key: return self.reply(403)
        if self.path == AUTH_PATH:
            body = json.loads(self.body())
            if body.get('username') != 'cam_probe' or body.get('password') != PASSWORD: return self.reply(403)
            return self.reply(200, {'username':'cam_probe', 'status':1,'home_dir':'/tmp/cam_probe',
                'permissions':{'/':['list','upload','overwrite','create_dirs']},
                'filesystem':{'provider':6,'httpconfig':{'endpoint': BASE+'/fs', 'api_key':{'status':'Plain','payload':KEY}}}})
        if self.path == '/hook':
            event = json.loads(self.body())
            events.append(event)
            if event['action'] == 'upload':
                hook_started.set()
                hold_hook.wait(10)
                return self.reply(500 if fail_hook else 200)
            return self.reply(200)
        parsed = urllib.parse.urlsplit(self.path)
        op, name = parsed.path[4:].split('/',1)
        name = urllib.parse.unquote(name)
        if op == 'mkdir': dirs.add(name); return self.reply(200)
        if op == 'create':
            flags = int(urllib.parse.parse_qs(parsed.query)['flags'][0])
            flags_seen.append(flags)
            if not flags & 512 or flags & 1024: return self.reply(403)
            files[name] = self.body()
            return self.reply(200)
        return self.reply(501)
    def do_GET(self):
        if self.headers.get('X-API-KEY') != KEY: return self.reply(403)
        op, name = self.path[4:].split('/',1)
        name = urllib.parse.unquote(name)
        if op in ('stat','lstat'):
            if name not in dirs and name not in files: return self.reply(404)
            return self.reply(200, {'name':name.rsplit('/',1)[-1], 'size':len(files.get(name,b'')),
                'mode':2147484141 if name in dirs else 420, 'last_modified':'2026-09-30T00:00:00Z'})
        if op == 'readdir': return self.reply(200, [])
        if op == 'dirsize': return self.reply(200, {'files':len(files), 'size':sum(map(len,files.values()))})
        return self.reply(501)
    def do_DELETE(self):
        # Retain uncertain data even when the edge attempts hook-failure cleanup.
        self.reply(403)

def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1',0))
        return sock.getsockname()[1]


def openssl(*args):
    subprocess.run(['openssl', *args], check=True, capture_output=True)


def issue_leaf(directory, name, hostname, ca=None):
    key = directory / f'{name}.key'
    cert = directory / f'{name}.pem'
    extensions = f'subjectAltName=DNS:{hostname}'
    request = ['req', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-subj', f'/CN={hostname}',
               '-addext', extensions, '-addext', 'extendedKeyUsage=serverAuth']
    if ca is None:
        openssl(*request, '-x509', '-days', '2', '-out', cert)
    else:
        csr = directory / f'{name}.csr'
        openssl(*request, '-out', csr)
        openssl('x509', '-req', '-in', csr, '-CA', ca[0], '-CAkey', ca[1], '-CAcreateserial',
                '-copy_extensions', 'copy', '-days', '2', '-out', cert)
    return cert, key


def connect(control_port):
    ftp = ftplib.FTP_TLS(context=client_tls)
    try:
        ftp.connect('127.0.0.1', control_port, timeout=1)
        ftp.host = 'localhost'
        ftp.timeout = 10
        ftp.sock.settimeout(10)
        return ftp
    except ftplib.all_errors:
        ftp.close()
        raise


def login(ftp):
    ftp.login('cam_probe', PASSWORD)
    ftp.prot_p()


def assert_login_rejected(control_port):
    with connect(control_port) as ftp:
        try:
            login(ftp)
        except ftplib.error_perm:
            return
        raise AssertionError('FTP login accepted a rejected callback')


hostname = 'host.docker.internal'
certificates = tempfile.TemporaryDirectory(prefix='shutterspool-callback-probe-')
cert_dir = Path(certificates.name)
ca = (cert_dir / 'ca.pem', cert_dir / 'ca.key')
openssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-out', ca[0], '-keyout', ca[1],
        '-subj', '/CN=ShutterSpool Callback Probe CA', '-days', '2', '-addext', 'basicConstraints=critical,CA:TRUE',
        '-addext', 'keyUsage=critical,keyCertSign,cRLSign')
original = issue_leaf(cert_dir, 'original', hostname, ca)
replacement = issue_leaf(cert_dir, 'replacement', hostname, ca)
wrong_hostname = issue_leaf(cert_dir, 'wrong-hostname', 'wrong.example.test', ca)
untrusted = issue_leaf(cert_dir, 'untrusted', hostname)
camera_cert, camera_key = issue_leaf(cert_dir, 'camera', 'localhost', ca)
# The container reads this synthetic key through a direct read-only mount of the private temporary directory's file.
camera_key.chmod(0o644)
tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
tls.minimum_version = ssl.TLSVersion.TLSv1_2
tls.load_cert_chain(*original)
client_tls = ssl.create_default_context(cafile=str(ca[0]))
client_tls.minimum_version = ssl.TLSVersion.TLSv1_2
server = http.server.ThreadingHTTPServer(('0.0.0.0',0), Handler)
server.socket = tls.wrap_socket(server.socket, server_side=True)
BASE = f'https://{hostname}:{server.server_port}'
threading.Thread(target=server.serve_forever,daemon=True).start()
control, passive = port(), port()
name = 'shutterspool-m109-probe-'+secrets.token_hex(4)
env = {
 'SFTPGO_SFTPD__BINDINGS__0__PORT':'0', 'SFTPGO_HTTPD__BINDINGS__0__PORT':'0',
 'SFTPGO_FTPD__BINDINGS__0__PORT':'2121', 'SFTPGO_FTPD__BINDINGS__0__FORCE_PASSIVE_IP':'127.0.0.1',
 'SFTPGO_FTPD__BINDINGS__0__TLS_MODE':'1', 'SFTPGO_FTPD__BINDINGS__0__MIN_TLS_VERSION':'12',
 'SFTPGO_FTPD__BINDINGS__0__CERTIFICATE_FILE':'/etc/sftpgo/camera.pem',
 'SFTPGO_FTPD__BINDINGS__0__CERTIFICATE_KEY_FILE':'/etc/sftpgo/camera.key',
 'SFTPGO_FTPD__PASSIVE_PORT_RANGE__START':str(passive), 'SFTPGO_FTPD__PASSIVE_PORT_RANGE__END':str(passive+1),
 'SFTPGO_FTPD__DISABLE_ACTIVE_MODE':'true','SFTPGO_DATA_PROVIDER__DRIVER':'memory',
 'SFTPGO_DATA_PROVIDER__EXTERNAL_AUTH_HOOK':BASE+AUTH_PATH,'SFTPGO_DATA_PROVIDER__EXTERNAL_AUTH_SCOPE':'1',
 'SFTPGO_COMMON__ACTIONS__EXECUTE_ON':'pre-upload,upload','SFTPGO_COMMON__ACTIONS__EXECUTE_SYNC':'upload',
 'SFTPGO_COMMON__ACTIONS__HOOK':BASE+'/hook','SFTPGO_HTTP__HEADERS__0__KEY':'X-API-KEY',
 'SFTPGO_HTTP__HEADERS__0__VALUE':KEY,'SFTPGO_HTTP__RETRY_MAX':'0', 'SFTPGO_HTTP__TIMEOUT':'10',
 'SSL_CERT_DIR':'/etc/ssl/certs:/etc/sftpgo/callback-trust',
}
command = ['docker','run','-d','--name',name,'-p',f'127.0.0.1:{control}:2121',
           '-p',f'127.0.0.1:{passive}-{passive+1}:{passive}-{passive+1}',
           '--mount', f'type=bind,source={camera_cert},target=/etc/sftpgo/camera.pem,readonly',
           '--mount', f'type=bind,source={camera_key},target=/etc/sftpgo/camera.key,readonly',
           '--mount', f'type=bind,source={ca[0]},target=/etc/sftpgo/callback-trust/root.pem,readonly']
for key,value in env.items(): command += ['-e',key+'='+value]
command += [IMAGE,'sftpgo','serve','--log-file-path','/dev/null','--log-level','error']
try:
    started = subprocess.run(command, capture_output=True)
    if started.returncode:
        raise RuntimeError('The disposable SFTPGo probe container could not start.')
    time.sleep(1)
    result = subprocess.run(['docker','inspect',name,'--format','{{.State.Status}}'],capture_output=True,text=True)
    if result.returncode or result.stdout.strip() != 'running':
        logs = subprocess.run(['docker','logs',name],capture_output=True,text=True)
        raise RuntimeError((logs.stdout+logs.stderr).replace(KEY,'[redacted]').replace(PASSWORD,'[redacted]'))
    for attempt in range(60):
        try:
            probe = connect(control)
            probe.close()
            break
        except (OSError, EOFError): time.sleep(.2)
    else: raise RuntimeError('No probe listener')
    for rejected in (untrusted, wrong_hostname):
        tls.load_cert_chain(*rejected)
        assert_login_rejected(control)
        assert not auth_key_checks, 'The auth request reached the callback with invalid TLS'
    tls.load_cert_chain(*original)
    expected_key = secrets.token_hex(32)
    assert_login_rejected(control)
    assert auth_key_checks == [False], 'The invalid-key attempt did not reach the HTTPS auth callback'
    expected_key = KEY
    ftp = connect(control)
    login(ftp)
    assert auth_key_checks[-1], 'The retry did not reach the HTTPS auth callback with its valid key'
    print('PASS: untrusted CA, hostname mismatch and wrong key each refused; verified HTTPS login then succeeded')
    assert ftp.storbinary('STOR DSC0001.JPG',io.BytesIO(b'first')).startswith('226')
    assert files['/DSC0001.JPG'] == b'first'
    assert ftp.storbinary('STOR DSC0001.JPG',io.BytesIO(b'second')).startswith('226')
    assert ftp.storbinary('STOR empty.JPG',io.BytesIO(b'')).startswith('226')
    assert ftp.storbinary('STOR ../../contained.JPG',io.BytesIO(b'virtual root')).startswith('226')
    assert files['/contained.JPG'] == b'virtual root'
    hold_hook.clear()
    hook_started.clear()
    completed = threading.Event()
    upload_result = []
    def upload_with_blocked_handoff():
        try:
            upload_result.append(ftp.storbinary('STOR blocked.JPG', io.BytesIO(b'blocked')))
        except Exception as error:
            upload_result.append(error)
        finally:
            completed.set()
    uploader = threading.Thread(target=upload_with_blocked_handoff)
    uploader.start()
    assert hook_started.wait(10), 'Completion hook did not start'
    assert not completed.wait(.25), 'FTP replied before the synchronous handoff completed'
    hold_hook.set()
    uploader.join(10)
    assert completed.is_set() and isinstance(upload_result[0], str) and upload_result[0].startswith('226')
    print('PASS: Community HTTPFs; two sequential same-name uploads; completion hook before FTP success')
    for target in ('absent.JPG','empty.JPG','DSC0001.JPG'):
        for command in ('APPE','STOR'):
            try:
                ftp.storbinary(command+' '+target,io.BytesIO(b'unsupported'),rest=1 if command=='STOR' else None)
            except ftplib.error_perm: pass
            else: raise AssertionError('Unsupported append/resume acknowledged')
    assert files['/DSC0001.JPG'] == b'second' and '/absent.JPG' not in files
    assert files['/empty.JPG'] == b''
    print('PASS: append/resume absent, empty and occupied targets rejected without mutation')
    fail_hook=True
    try: ftp.storbinary('STOR uncertain.JPG',io.BytesIO(b'retain'))
    except ftplib.error_perm: pass
    else: raise AssertionError('Failure acknowledged')
    assert files['/uncertain.JPG']==b'retain'
    print('PASS: synchronous hook failure denies FTP success; refused cleanup retains staging')
    assert events[-1]['fs_provider'] == 6 and events[-1]['endpoint'] == BASE+'/fs'
    assert events[0].get('open_flags',0) == 0
    assert flags_seen[0] == 577 and 1089 in flags_seen
    assert len({event['timestamp'] for event in events}) == len(events)
    print('PASS: callback schema includes provider endpoint, session and distinct event timestamps')
    ftp.close()
    fail_hook = False
    for filename, identity in (('rotated.JPG', replacement), ('restored.JPG', original)):
        tls.load_cert_chain(*identity)
        with (
            socket.create_connection(('127.0.0.1', server.server_port), timeout=5) as peer,
            client_tls.wrap_socket(peer, server_hostname=hostname) as verified,
        ):
            assert verified.getpeercert(binary_form=True) == ssl.PEM_cert_to_DER_cert(identity[0].read_text(encoding='ascii'))
            verified.sendall(b'GET / HTTP/1.0\r\n\r\n')
            with verified.makefile('rb') as response:
                assert response.readline().startswith(b'HTTP/1.0 403')
                response.read()
        with connect(control) as ftp:
            login(ftp)
            assert ftp.storbinary('STOR ' + filename, io.BytesIO(b'certificate probe')).startswith('226')
        assert files['/' + filename] == b'certificate probe'
    print('PASS: verified callback certificate replacement and rollback after completed uploads')
finally:
    subprocess.run(['docker','rm','-f',name],capture_output=True)
    server.shutdown(); server.server_close()
    certificates.cleanup()
