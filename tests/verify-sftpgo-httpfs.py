"""Characterize the pinned Community edge with synthetic bytes, not ShutterSpool durability.

Run: python tests/verify-sftpgo-httpfs.py (Docker Desktop required).
Only disposable probe resources are removed; no existing service or media is touched.
"""
import ftplib
import http.server
import io
import json
import secrets
import socket
import subprocess
import threading
import time
import urllib.parse

IMAGE = 'drakkan/sftpgo@sha256:9011fe608d336d3daf6ed6224b16fd40443aab8f3335e0b20853d6a539c58738'
KEY = secrets.token_hex(32)
PASSWORD = secrets.token_hex(16)
events, files, dirs, flags_seen = [], {}, {'/'}, []
fail_hook = False
hold_hook = threading.Event()
hold_hook.set()
hook_started = threading.Event()

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
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
        if self.headers.get('X-API-KEY') != KEY: return self.reply(403)
        if self.path == '/auth':
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

server = http.server.ThreadingHTTPServer(('0.0.0.0',0), Handler)
BASE = f'http://host.docker.internal:{server.server_port}'
threading.Thread(target=server.serve_forever,daemon=True).start()
control, passive = port(), port()
name = 'shutterspool-m109-probe-'+secrets.token_hex(4)
env = {
 'SFTPGO_SFTPD__BINDINGS__0__PORT':'0', 'SFTPGO_HTTPD__BINDINGS__0__PORT':'0',
 'SFTPGO_FTPD__BINDINGS__0__PORT':'2121', 'SFTPGO_FTPD__BINDINGS__0__FORCE_PASSIVE_IP':'127.0.0.1',
 'SFTPGO_FTPD__PASSIVE_PORT_RANGE__START':str(passive), 'SFTPGO_FTPD__PASSIVE_PORT_RANGE__END':str(passive+1),
 'SFTPGO_FTPD__DISABLE_ACTIVE_MODE':'true','SFTPGO_DATA_PROVIDER__DRIVER':'memory',
 'SFTPGO_DATA_PROVIDER__EXTERNAL_AUTH_HOOK':BASE+'/auth','SFTPGO_DATA_PROVIDER__EXTERNAL_AUTH_SCOPE':'1',
 'SFTPGO_COMMON__ACTIONS__EXECUTE_ON':'pre-upload,upload','SFTPGO_COMMON__ACTIONS__EXECUTE_SYNC':'upload',
 'SFTPGO_COMMON__ACTIONS__HOOK':BASE+'/hook','SFTPGO_HTTP__HEADERS__0__KEY':'X-API-KEY',
 'SFTPGO_HTTP__HEADERS__0__VALUE':KEY,'SFTPGO_HTTP__RETRY_MAX':'0', 'SFTPGO_HTTP__TIMEOUT':'10',
}
command = ['docker','run','-d','--name',name,'-p',f'127.0.0.1:{control}:2121','-p',f'127.0.0.1:{passive}-{passive+1}:{passive}-{passive+1}']
for key,value in env.items(): command += ['-e',key+'='+value]
command += [IMAGE,'sftpgo','serve','--log-file-path','/dev/null','--log-level','error']
try:
    subprocess.run(command,check=True,capture_output=True)
    time.sleep(1)
    result = subprocess.run(['docker','inspect',name,'--format','{{.State.Status}}'],capture_output=True,text=True)
    if result.returncode or result.stdout.strip() != 'running':
        logs = subprocess.run(['docker','logs',name],capture_output=True,text=True)
        raise RuntimeError((logs.stdout+logs.stderr).replace(KEY,'[redacted]').replace(PASSWORD,'[redacted]'))
    for attempt in range(60):
        try:
            ftp=ftplib.FTP(); ftp.connect('127.0.0.1',control,timeout=1); ftp.sock.settimeout(10); break
        except (OSError, EOFError): time.sleep(.2)
    else: raise RuntimeError('No probe listener')
    ftp.login('cam_probe',PASSWORD)
    ftp.timeout = 10
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
finally:
    subprocess.run(['docker','rm','-f',name],capture_output=True)
    server.shutdown(); server.server_close()
