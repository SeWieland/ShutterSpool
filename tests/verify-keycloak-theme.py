"""Verify native enrollment defaults and login-theme selection with a controlled kcadm stub.
Run: python tests/verify-keycloak-theme.py (requires Docker and the supported Keycloak image).
"""
import json
import pathlib
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'src/ShutterSpool.AppHost/Realms/select-login-theme.sh'
RESOURCES = ['theme.properties', 'resources/css/shutterspool.v1.css',
             'resources/img/aperture.svg', 'resources/img/shutter-color.png', 'resources/img/favicon.ico',
             'resources/fonts/Manrope.ttf', 'resources/fonts/Manrope-OFL.txt']


class ThemeSelectionTests(unittest.TestCase):
    def test_fresh_realms_require_verification_disable_registration_and_allow_native_recovery(self):
        realm = json.loads((SCRIPT.parent / 'shutterspool-realm.json').read_text())
        self.assertIs(realm['registrationAllowed'], False)
        self.assertIs(realm['verifyEmail'], True)
        self.assertIs(realm['resetPasswordAllowed'], True)
        self.assertNotIn('actionTokenGeneratedByUserLifespan', realm)

    def test_access_tokens_explicitly_map_native_verification_as_boolean(self):
        mapper = json.loads((SCRIPT.parent / 'shutterspool-email-verified-mapper.json').read_text())
        self.assertEqual(mapper['protocol'], 'openid-connect')
        self.assertEqual(mapper['protocolMapper'], 'oidc-usermodel-property-mapper')
        config = mapper['config']
        self.assertEqual(config['user.attribute'], 'emailVerified')
        self.assertEqual(config['claim.name'], 'email_verified')
        self.assertEqual(config['jsonType.label'], 'boolean')
        self.assertEqual(config['access.token.claim'], 'true')

    def run_selection(self, theme='shutterspool', missing=None, check=False, pointer=False):
        with tempfile.TemporaryDirectory() as directory:
            fixture = pathlib.Path(directory)
            theme_root = fixture / 'theme/login'
            for name in RESOURCES:
                target = theme_root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text('fixture')
            if missing:
                (theme_root / missing).unlink()
            if pointer:
                (theme_root / 'resources/img/shutter-color.png').write_bytes(b'version https://git-lfs.github.com/spec/v1\n')
            mock = fixture / 'kcadm.sh'
            mock.write_bytes(b'#!/bin/sh\nprintf "%s\\n" "$*" >> /test/calls\n')
            command = ['docker', 'run', '--rm', '--entrypoint', '/bin/sh',
                       '--mount', f'type=bind,source={fixture},target=/test',
                       '--mount', f'type=bind,source={mock},target=/opt/keycloak/bin/kcadm.sh,readonly',
                       '--mount', f'type=bind,source={fixture / "theme"},target=/opt/keycloak/themes/shutterspool,readonly',
                       '--mount', f'type=bind,source={SCRIPT.parent},target=/opt/shutterspool,readonly',
                       '-e', f'SHUTTERSPOOL_LOGIN_THEME={theme}', 'quay.io/keycloak/keycloak:26.6',
                       '/opt/shutterspool/select-login-theme.sh']
            if check:
                command.append('--check')
            result = subprocess.run(command, capture_output=True, text=True, timeout=60)
            calls = (fixture / 'calls').read_text() if (fixture / 'calls').exists() else ''
            return result, calls

    def test_custom_theme_selection_changes_only_login_theme(self):
        result, calls = self.run_selection()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, 'update realms/shutterspool --no-merge -s loginTheme=shutterspool\n')

    def test_check_is_non_mutating(self):
        result, calls = self.run_selection(check=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, '')

    def test_missing_required_resource_never_selects_theme(self):
        for resource in RESOURCES:
            with self.subTest(resource=resource):
                result, calls = self.run_selection(missing=resource)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('Theme resource unavailable', result.stderr)
                self.assertEqual(calls, '')

    def test_bundled_fallback_does_not_require_custom_assets(self):
        result, calls = self.run_selection(theme='keycloak.v2', missing='theme.properties')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, 'update realms/shutterspool --no-merge -s loginTheme=keycloak.v2\n')

    def test_unhydrated_lfs_resource_never_selects_theme(self):
        result, calls = self.run_selection(pointer=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls, '')
        self.assertIn('Git LFS', result.stderr)

    def test_unknown_theme_is_rejected_before_mutation(self):
        result, calls = self.run_selection(theme='../unknown')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Unsupported login theme', result.stderr)
        self.assertEqual(calls, '')


if __name__ == '__main__':
    unittest.main()
