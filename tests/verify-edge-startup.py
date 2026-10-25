#!/usr/bin/env python3
"""Prove FTPS edge startup rejects absent, expired, mismatched-host and mismatched-key certificates."""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ENTRYPOINT = ROOT / "deploy/package/edge-entrypoint.sh"


def run(*args: str, cwd: Path | None = None, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(args, cwd=cwd, env=env, text=True, capture_output=True, check=False)


class EdgeCertificateStartupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not shutil.which("openssl"):
            raise unittest.SkipTest("OpenSSL is required to generate synthetic test certificates")

    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="shutterspool-edge-certs-")
        self.root = Path(self.temp.name)
        self.certs = self.root / "certs"
        self.certs.mkdir()
        self.callback_key = self.root / "camera-callback-key"
        self.callback_key.write_text("synthetic-camera-callback-key", encoding="ascii")
        self.entrypoint = self.root / "edge-entrypoint.sh"
        script = (ENTRYPOINT.read_text(encoding="utf-8")
                  .replace("/etc/sftpgo/certs", str(self.certs))
                  .replace("/run/secrets/camera-callback-key", str(self.callback_key)))
        self.entrypoint.write_text(script, encoding="utf-8")
        fakebin = self.root / "bin"
        fakebin.mkdir()
        fake_sftpgo = fakebin / "sftpgo"
        fake_sftpgo.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
        fake_sftpgo.chmod(0o755)
        self.env = os.environ | {
            "PATH": f"{fakebin}{os.pathsep}{os.environ.get('PATH', '')}",
            "SFTPGO_FTPD__BINDINGS__0__TLS_MODE": "1",
            "CAMERA_HOSTNAME": "ingest.example.test",
        }
        self._make_chain("ingest.example.test")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _openssl(self, *args: str) -> None:
        result = run("openssl", *args, cwd=self.root)
        self.assertEqual(0, result.returncode, result.stderr)

    def _make_chain(self, hostname: str, *, expired: bool = False) -> None:
        self._openssl("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", "ca.key", "-out", "root.pem",
                      "-days", "2", "-subj", "/CN=ShutterSpool Test CA", "-addext", "basicConstraints=critical,CA:TRUE")
        self._openssl("req", "-newkey", "rsa:2048", "-nodes", "-keyout", "server.key", "-out", "server.csr",
                      "-subj", "/CN=" + hostname)
        extension = self.root / "server.ext"
        extension.write_text("subjectAltName=DNS:" + hostname + "\nextendedKeyUsage=serverAuth\nkeyUsage=digitalSignature,keyEncipherment\n", encoding="utf-8")
        now = datetime.now(timezone.utc)
        start = now - timedelta(days=3) if expired else now - timedelta(minutes=1)
        end = now - timedelta(days=2) if expired else now + timedelta(days=1)
        config = self.root / "ca.cnf"
        config.write_text(
            "[ca]\ndefault_ca=issuer\n[issuer]\n"
            f"database={self.root / 'index.txt'}\nnew_certs_dir={self.root}\n"
            f"certificate={self.root / 'root.pem'}\nprivate_key={self.root / 'ca.key'}\nserial={self.root / 'serial'}\n"
            "default_md=sha256\npolicy=policy_any\n[policy_any]\ncommonName=supplied\n[usr_cert]\n"
            "basicConstraints=CA:FALSE\nsubjectKeyIdentifier=hash\nauthorityKeyIdentifier=keyid,issuer\n"
            "subjectAltName=@alt_names\nextendedKeyUsage=serverAuth\nkeyUsage=digitalSignature,keyEncipherment\n"
            "[alt_names]\nDNS.1=" + hostname + "\n", encoding="utf-8")
        (self.root / "index.txt").write_text("", encoding="utf-8")
        (self.root / "serial").write_text("01\n", encoding="utf-8")
        self._openssl("ca", "-batch", "-config", str(config), "-startdate", start.strftime("%Y%m%d%H%M%SZ"),
                      "-enddate", end.strftime("%Y%m%d%H%M%SZ"), "-in", "server.csr", "-out", "leaf.pem",
                      "-extensions", "usr_cert")
        (self.certs / "root.pem").write_bytes((self.root / "root.pem").read_bytes())
        (self.certs / "server.pem").write_bytes((self.root / "leaf.pem").read_bytes() + (self.root / "server.key").read_bytes())

    def _start(self) -> subprocess.CompletedProcess[str]:
        return run("sh", str(self.entrypoint), cwd=self.root, env=self.env)

    def test_accepts_valid_supplied_certificate(self) -> None:
        self.assertEqual(0, self._start().returncode)

    def test_rejects_missing_certificate(self) -> None:
        (self.certs / "server.pem").unlink()
        result = self._start()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("FTPS certificate is missing or invalid", result.stderr)

    def test_rejects_expired_certificate(self) -> None:
        self._make_chain("ingest.example.test", expired=True)
        result = self._start()
        self.assertNotEqual(0, result.returncode)

    def test_rejects_wrong_hostname(self) -> None:
        self._make_chain("other.example.test")
        result = self._start()
        self.assertNotEqual(0, result.returncode)

    def test_rejects_private_key_mismatch(self) -> None:
        self._openssl("genrsa", "-out", "different.key", "2048")
        bundle = (self.root / "leaf.pem").read_bytes() + (self.root / "different.key").read_bytes()
        (self.certs / "server.pem").write_bytes(bundle)
        result = self._start()
        self.assertNotEqual(0, result.returncode)


if __name__ == "__main__":
    unittest.main()
