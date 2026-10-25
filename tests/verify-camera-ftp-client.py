"""Focused checks for the shared synthetic camera probe client; no network or secrets."""
import ftplib
import ssl
import unittest
from unittest import mock

import camera_ftp


class CameraFtpClientChecks(unittest.TestCase):
    def test_management_profile_is_explicit_and_matches_the_client_security_mode(self):
        self.assertEqual('ModernFtps', camera_ftp.connection_profile({}))
        self.assertEqual('PlainFtpLan', camera_ftp.connection_profile({'ftp': {'mode': 'PlainFtpLan'}}))
        self.assertEqual('LegacyFtps', camera_ftp.connection_profile({'ftp': {'mode': 'LegacyFtps'}}))
        self.assertEqual('/auth', camera_ftp.authentication_path({}))
        self.assertEqual('/plain-auth', camera_ftp.authentication_path({'ftp': {'mode': 'PlainFtpLan'}}))
        self.assertEqual('/legacy-auth', camera_ftp.authentication_path({'ftp': {'mode': 'LegacyFtps'}}))
        with self.assertRaises(ValueError):
            camera_ftp.connection_profile({'ftp': {'mode': 'automatic'}})

    def test_legacy_requires_validated_tls_and_the_qualified_cipher_policy(self):
        with mock.patch.object(ftplib, 'FTP_TLS') as factory:
            client = camera_ftp.connect_camera_ftp({'ftp': {'mode': 'LegacyFtps'}}, {'username': 'synthetic', 'secret': 'synthetic'})
            context = factory.call_args.kwargs['context']
            self.assertTrue(context.check_hostname)
            self.assertEqual(ssl.CERT_REQUIRED, context.verify_mode)
            self.assertEqual(ssl.TLSVersion.TLSv1_2, context.maximum_version)
            ciphers = {cipher['name'] for cipher in context.get_ciphers() if cipher['protocol'] != 'TLSv1.3'}
            self.assertEqual({'AES128-SHA', 'AES256-SHA'}, ciphers)
            client.prot_p.assert_called_once_with()

    def test_default_requires_validated_tls_for_login_and_data(self):
        with mock.patch.object(ftplib, 'FTP_TLS') as factory:
            client = camera_ftp.connect_camera_ftp({}, {'username': 'synthetic', 'secret': 'synthetic'})
            context = factory.call_args.kwargs['context']
            self.assertTrue(context.check_hostname)
            self.assertEqual(ssl.CERT_REQUIRED, context.verify_mode)
            client.connect.assert_called_once_with('ingest.shutterspool.com', 2121, timeout=90)
            client.login.assert_called_once_with('synthetic', 'synthetic')
            client.prot_p.assert_called_once_with()

    def test_connection_address_does_not_replace_certificate_hostname(self):
        with mock.patch.object(ftplib, 'FTP_TLS') as factory:
            client = camera_ftp.connect_camera_ftp({'ftp': {'address': '127.0.0.1', 'port': 2131}}, {'username': 'synthetic', 'secret': 'synthetic'})
            client.connect.assert_called_once_with('127.0.0.1', 2131, timeout=90)
            self.assertEqual('ingest.shutterspool.com', client.host)

    def test_tls_rejection_closes_client_without_plaintext_retry(self):
        with mock.patch.object(ftplib, 'FTP_TLS') as factory, mock.patch.object(ftplib, 'FTP') as plaintext:
            factory.return_value.login.side_effect = ssl.SSLCertVerificationError('synthetic rejection')
            with self.assertRaises(ssl.SSLCertVerificationError):
                camera_ftp.connect_camera_ftp({}, {'username': 'synthetic', 'secret': 'synthetic'})
            factory.return_value.close.assert_called_once_with()
            plaintext.assert_not_called()

    def test_plaintext_requires_explicit_compatibility_mode(self):
        with mock.patch.object(ftplib, 'FTP') as factory, mock.patch.object(ftplib, 'FTP_TLS') as secure:
            client = camera_ftp.connect_camera_ftp({'ftp': {'mode': 'PlainFtpLan'}}, {'username': 'synthetic', 'secret': 'synthetic'})
            client.login.assert_called_once_with('synthetic', 'synthetic')
            secure.assert_not_called()
            with self.assertRaises(ValueError):
                camera_ftp.connect_camera_ftp({'ftp': {'mode': 'automatic'}}, {'username': 'synthetic', 'secret': 'synthetic'})


if __name__ == '__main__':
    unittest.main()
