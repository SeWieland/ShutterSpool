"""Check pinned Community SFTPGo explicit FTPS behavior with disposable local certificates.

Run: python tests/verify-ftps-edge.py (Docker Desktop and OpenSSL required).
The script creates and removes only its own test container and temporary files.
"""
import ftplib
import argparse
import hashlib
import http.server
import io
import json
import os
import secrets
import shutil
import socket
import ssl
import subprocess
import tempfile
import threading
import time
import urllib.parse
from image_pins import load_image_pins

IMAGE = load_image_pins()['camera-edge']
KEY = secrets.token_hex(32)
PASSWORD = secrets.token_hex(16)
files = {}
requests = []


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def body(self):
        if self.headers.get('Transfer-Encoding') != 'chunked':
            return self.rfile.read(int(self.headers.get('Content-Length', 0)))
        chunks = []
        while True:
            size = int(self.rfile.readline().split(b';')[0], 16)
            if not size:
                self.rfile.readline()
                return b''.join(chunks)
            chunks.append(self.rfile.read(size))
            self.rfile.read(2)

    def reply(self, status, value=None):
        payload = json.dumps(value).encode() if value is not None else b''
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_POST(self):
        if self.headers.get('X-API-KEY') != KEY:
            return self.reply(403)
        requests.append(('POST', self.path))
        if self.path == '/auth':
            request = json.loads(self.body())
            if request.get('username') != 'cam_probe' or request.get('password') != PASSWORD:
                return self.reply(403)
            return self.reply(200, {'username': 'cam_probe', 'status': 1, 'home_dir': '/tmp/cam_probe',
                'permissions': {'/': ['list', 'upload', 'overwrite', 'create_dirs']},
                'filesystem': {'provider': 6, 'httpconfig': {'endpoint': BASE + '/fs',
                    'api_key': {'status': 'Plain', 'payload': KEY}}}})
        if self.path.startswith('/fs/create/'):
            name = urllib.parse.unquote(urllib.parse.urlsplit(self.path).path[len('/fs/create/'):])
            files['/' + name.lstrip('/')] = self.body()
            return self.reply(200)
        return self.reply(501)

    def do_GET(self):
        if self.headers.get('X-API-KEY') != KEY:
            return self.reply(403)
        requests.append(('GET', self.path))
        op, name = self.path[4:].split('/', 1)
        name = '/' + urllib.parse.unquote(name)
        if op in ('stat', 'lstat'):
            if name not in files:
                return self.reply(404)
            return self.reply(200, {'name': name.rsplit('/', 1)[-1], 'size': len(files[name]),
                'mode': 420, 'last_modified': '2026-10-17T00:00:00Z'})
        if op == 'readdir':
            return self.reply(200, [])
        if op == 'dirsize':
            return self.reply(200, {'files': len(files), 'size': sum(map(len, files.values()))})
        return self.reply(501)


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def openssl(*args):
    subprocess.run(['openssl', *args], check=True, capture_output=True)


def create_certificate(directory, version):
    key = os.path.join(directory, f'server-{version}.key')
    request = os.path.join(directory, f'server-{version}.csr')
    cert = os.path.join(directory, f'server-{version}.pem')
    extensions = os.path.join(directory, f'server-{version}.ext')
    with open(extensions, 'w', encoding='ascii') as output:
        output.write('subjectAltName=DNS:ingest.shutterspool.com\n')
        output.write('extendedKeyUsage=serverAuth\n')
        output.write('keyUsage=critical,digitalSignature,keyEncipherment\n')
        output.write('basicConstraints=critical,CA:FALSE\n')
    openssl('req', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', request,
        '-subj', '/CN=ingest.shutterspool.com')
    openssl('x509', '-req', '-in', request, '-CA', os.path.join(directory, 'root.pem'),
        '-CAkey', os.path.join(directory, 'root.key'), '-CAcreateserial', '-out', cert,
        '-days', '10', '-sha256', '-extfile', extensions)
    bundle = os.path.join(directory, 'server.pem')
    with open(bundle, 'wb') as output:
        for path in (cert, key):
            with open(path, 'rb') as source:
                output.write(source.read() + b'\n')
    return bundle


def control(port):
    client = socket.create_connection(('127.0.0.1', port), timeout=5)
    client.settimeout(10)
    greeting = client.makefile('rb').readline()
    if not greeting:
        client.close()
        raise ConnectionError('FTP listener is open but the server greeting is not ready.')
    if not greeting.startswith(b'220'):
        client.close()
        raise RuntimeError(f'Unexpected FTP greeting: {greeting!r}')
    return client


def connect_ftps(port, context):
    client = ftplib.FTP_TLS(context=context)
    client.connect('127.0.0.1', port, timeout=10)
    client.host = 'ingest.shutterspool.com'
    return client


def reply(sock):
    return sock.makefile('rb').readline()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--legacy', action='store_true', help='Check the separate older-camera cipher profile.')
    options = parser.parse_args()
    if not shutil.which('openssl') or not shutil.which('docker'):
        raise RuntimeError('OpenSSL and Docker Desktop must be available on PATH.')
    with tempfile.TemporaryDirectory(prefix='shutterspool-ftps-') as directory:
        certificate_directory = os.path.join(directory, 'certificates')
        os.mkdir(certificate_directory)
        openssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', os.path.join(directory, 'root.key'),
            '-out', os.path.join(directory, 'root.pem'), '-days', '30', '-sha256', '-subj', '/CN=Probe Root',
            '-addext', 'basicConstraints=critical,CA:TRUE', '-addext', 'keyUsage=critical,keyCertSign,cRLSign')
        create_certificate(directory, 1)
        first_bundle = os.path.join(directory, 'server-first.pem')
        os.replace(os.path.join(directory, 'server.pem'), first_bundle)
        create_certificate(directory, 2)
        second_bundle = os.path.join(directory, 'server-next.pem')
        os.replace(os.path.join(directory, 'server.pem'), second_bundle)
        shutil.copyfile(os.path.join(directory, 'root.pem'), os.path.join(certificate_directory, 'root.pem'))
        shutil.copyfile(first_bundle, os.path.join(certificate_directory, 'server.pem'))

        handler = http.server.ThreadingHTTPServer(('0.0.0.0', 0), Handler)
        global BASE
        BASE = f'http://host.docker.internal:{handler.server_port}'
        threading.Thread(target=handler.serve_forever, daemon=True).start()
        ftp_port, passive_start = free_port(), free_port()
        passive_end = passive_start + 1
        name = 'shutterspool-ftps-probe-' + secrets.token_hex(4)
        env = {
            'SFTPGO_SFTPD__BINDINGS__0__PORT': '0',
            'SFTPGO_HTTPD__BINDINGS__0__PORT': '0',
            'SFTPGO_FTPD__BINDINGS__0__PORT': '2121',
            'SFTPGO_FTPD__BINDINGS__0__TLS_MODE': '1',
            'SFTPGO_FTPD__BINDINGS__0__MIN_TLS_VERSION': '12',
            'SFTPGO_FTPD__BINDINGS__0__CLIENT_AUTH_TYPE': '0',
            'SFTPGO_FTPD__BINDINGS__0__CERTIFICATE_FILE': '/etc/sftpgo/certs/server.pem',
            'SFTPGO_FTPD__BINDINGS__0__CERTIFICATE_KEY_FILE': '/etc/sftpgo/certs/server.pem',
            'SFTPGO_FTPD__BINDINGS__0__FORCE_PASSIVE_IP': '127.0.0.1',
            'SFTPGO_FTPD__PASSIVE_PORT_RANGE__START': str(passive_start),
            'SFTPGO_FTPD__PASSIVE_PORT_RANGE__END': str(passive_end),
            'SFTPGO_FTPD__DISABLE_ACTIVE_MODE': 'true',
            'SFTPGO_DATA_PROVIDER__DRIVER': 'memory',
            'SFTPGO_DATA_PROVIDER__EXTERNAL_AUTH_HOOK': BASE + '/auth',
            'SFTPGO_DATA_PROVIDER__EXTERNAL_AUTH_SCOPE': '1',
            'SFTPGO_HTTP__HEADERS__0__KEY': 'X-API-KEY',
            'SFTPGO_HTTP__HEADERS__0__VALUE': KEY,
        }
        if options.legacy:
            env['SFTPGO_FTPD__BINDINGS__0__TLS_CIPHER_SUITES'] = 'TLS_RSA_WITH_AES_128_CBC_SHA,TLS_RSA_WITH_AES_256_CBC_SHA'
        command = ['docker', 'run', '-d', '--name', name, '-p', f'127.0.0.1:{ftp_port}:2121',
            '-p', f'127.0.0.1:{passive_start}-{passive_end}:{passive_start}-{passive_end}',
            '-v', certificate_directory + ':/etc/sftpgo/certs:ro']
        for key, value in env.items():
            command += ['-e', key + '=' + value]
        command += [IMAGE, 'sftpgo', 'serve', '--log-file-path', '/dev/null', '--log-level', 'error']
        try:
            try:
                subprocess.run(command, check=True, capture_output=True)
            except subprocess.CalledProcessError:
                raise RuntimeError('Pinned SFTPGo container could not start; command output was suppressed.') from None
            for _ in range(60):
                try:
                    plain = control(ftp_port)
                    break
                except OSError:
                    time.sleep(.2)
            else:
                raise RuntimeError('Pinned SFTPGo FTP listener did not start.')

            plain.sendall(b'USER cam_probe\r\n')
            user_reply = reply(plain)
            assert not user_reply.startswith(b'331'), f'Plaintext USER began login: {user_reply!r}'
            plain.close()
            print('PASS: plaintext USER is rejected before password authentication')

            context = ssl.create_default_context(cafile=os.path.join(directory, 'root.pem'))
            context.minimum_version = ssl.TLSVersion.TLSv1_2
            for cipher in ('AES128-SHA', 'AES256-SHA'):
                rsa_context = ssl.create_default_context(cafile=os.path.join(directory, 'root.pem'))
                rsa_context.minimum_version = rsa_context.maximum_version = ssl.TLSVersion.TLSv1_2
                rsa_context.set_ciphers(cipher)
                rsa_client = connect_ftps(ftp_port, rsa_context)
                try:
                    try:
                        rsa_client.auth()
                    except ssl.SSLError:
                        assert not options.legacy, f'Compatibility listener rejected {cipher}'
                    else:
                        assert options.legacy, f'Modern listener accepted legacy cipher {cipher}'
                        assert rsa_client.sock.cipher()[0] == cipher
                        rsa_client.login('cam_probe', PASSWORD)
                        rsa_client.prot_p()
                        content = b'legacy TLS still protects control and data'
                        filename = 'legacy-' + cipher + '.bin'
                        assert rsa_client.storbinary('STOR ' + filename, io.BytesIO(content)).startswith('226')
                        assert files['/' + filename] == content
                finally:
                    rsa_client.close()
            print('PASS: legacy RSA ciphers are ' + ('explicitly accepted with protected data' if options.legacy else 'rejected by modern defaults'))
            ftp = connect_ftps(ftp_port, context)
            assert ftp.auth().startswith('234')
            ftp.login('cam_probe', PASSWORD)
            try:
                ftp.prot_c()
                ftp.storbinary('STOR clear-data.bin', io.BytesIO(b'must not be sent in clear'))
            except ftplib.error_temp as error:
                if not str(error).startswith('421'):
                    raise
                ftp.close()
                ftp = connect_ftps(ftp_port, context)
                ftp.auth(); ftp.login('cam_probe', PASSWORD)
            except ftplib.error_perm:
                pass
            else:
                raise AssertionError('PROT C upload was accepted on the secure listener')
            assert not any(path.endswith('/clear-data.bin') for path in files), 'Clear data reached the filesystem callback'
            ftp.prot_p()
            assert ftp.storbinary('STOR tls-data.bin', io.BytesIO(b'protected data')).startswith('226')
            uploaded = [data for path, data in files.items() if path.endswith('/tls-data.bin')]
            assert uploaded and uploaded[0] == b'protected data', f'No synthetic upload callback recorded; operations: {requests!r}'
            print('PASS: explicit TLS login works, PROT C cannot upload, and PROT P does')

            verified = connect_ftps(ftp_port, context)
            verified.auth()
            verified.close()
            print('PASS: a validating FTPS client accepts the certificate served by the pinned edge')

            no_sni = control(ftp_port)
            no_sni.sendall(b'AUTH TLS\r\n')
            assert reply(no_sni).startswith(b'234')
            no_sni_context = ssl.create_default_context(cafile=os.path.join(directory, 'root.pem'))
            no_sni_context.check_hostname = False
            no_sni_tls = no_sni_context.wrap_socket(no_sni, server_hostname=None)
            with open(os.path.join(directory, 'server-1.pem'), encoding='ascii') as source:
                original_fingerprint = hashlib.sha256(ssl.PEM_cert_to_DER_cert(source.read())).digest()
            assert hashlib.sha256(no_sni_tls.getpeercert(binary_form=True)).digest() == original_fingerprint
            no_sni_tls.close()
            print('PASS: server presents its certificate when the client sends no SNI')

            legacy = control(ftp_port)
            legacy.sendall(b'AUTH TLS\r\n')
            assert reply(legacy).startswith(b'234')
            old_tls = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            old_tls.check_hostname = False
            old_tls.verify_mode = ssl.CERT_NONE
            old_tls.minimum_version = ssl.TLSVersion.TLSv1_1
            old_tls.maximum_version = ssl.TLSVersion.TLSv1_1
            old_tls.set_ciphers('DEFAULT:@SECLEVEL=0')
            try:
                old_tls.wrap_socket(legacy, server_hostname=None)
            except (ssl.SSLError, OSError):
                legacy.close()
            else:
                raise AssertionError('TLS 1.1 handshake succeeded')
            print('PASS: TLS versions below 1.2 are rejected')

            ftp.quit()
            ftp = connect_ftps(ftp_port, context)
            ftp.auth(); ftp.login('cam_probe', PASSWORD); ftp.prot_p()
            assert ftp.storbinary('STOR before-replacement.bin', io.BytesIO(b'drained before replacement')).startswith('226')
            ftp.quit()
            os.replace(second_bundle, os.path.join(certificate_directory, 'server.pem'))
            subprocess.run(['docker', 'restart', '--time', '30', name], check=True, capture_output=True)

            with open(os.path.join(directory, 'server-2.pem'), encoding='ascii') as source:
                expected_fingerprint = hashlib.sha256(ssl.PEM_cert_to_DER_cert(source.read())).digest()
            for _ in range(50):
                fresh = None
                try:
                    fresh = connect_ftps(ftp_port, context)
                    fresh.auth()
                    renewed_fingerprint = hashlib.sha256(fresh.sock.getpeercert(binary_form=True)).digest()
                except (OSError, ftplib.Error):
                    if fresh is not None:
                        fresh.close()
                    time.sleep(.2)
                    continue
                fresh.close()
                if renewed_fingerprint == expected_fingerprint:
                    break
                time.sleep(.2)
            else:
                raise AssertionError('Fresh session is not serving the replacement certificate')
            print('PASS: after transfers drain and the edge restarts, it serves the supplied replacement certificate')
        finally:
            subprocess.run(['docker', 'rm', '-f', name], capture_output=True)
            handler.shutdown()
            handler.server_close()


if __name__ == '__main__':
    main()
