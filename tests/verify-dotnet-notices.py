import json
import os
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path


COLLECTOR = Path(__file__).resolve().parents[1] / "tools/collect-dotnet-notices.cs"


class DotnetNoticeTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.deps = self.root / "Application.deps.json"
        self.packages = self.root / "packages"
        self.fallbacks = self.root / "fallbacks"
        self.output = self.root / "licenses"
        self.index_path = self.output / "INDEX.txt"
        self.fallbacks.mkdir()
        (self.fallbacks / "manifest.json").write_text("[]", encoding="utf-8")

    def deps_manifest(self, entries):
        runtime_target = ".NETCoreApp,Version=v11.0"
        self.deps.write_text(json.dumps({
            "runtimeTarget": {"name": runtime_target},
            "targets": {runtime_target: entries},
            "libraries": {name: {"type": "package", "path": name.lower()} for name in entries},
        }), encoding="utf-8")

    def package(self, name, version, license_metadata, documents):
        directory = self.packages / name.lower() / version
        directory.mkdir(parents=True)
        with zipfile.ZipFile(directory / f"{name.lower()}.{version}.nupkg", "w") as package:
            package.writestr(f"{name}.nuspec", f"""<package><metadata>
              <id>{name}</id><version>{version}</version>{license_metadata}
              </metadata></package>""")
            for path, contents in documents.items():
                package.writestr(path, contents)

    def upstream_license(self, identity):
        (self.fallbacks / "upstream.txt").write_bytes(b"Exact upstream license with copyright\n")
        (self.fallbacks / "manifest.json").write_text(json.dumps([{
            "packages": [identity],
            "documents": [{"file": "upstream.txt", "source": "https://example.invalid/LICENSE", "license": True}],
        }]), encoding="utf-8")

    def collect(self):
        return subprocess.run([
            "dotnet", "run", "--property", f"ArtifactsPath={self.root / 'build'}", "--file", COLLECTOR,
            "--", self.deps, self.packages,
            self.fallbacks, self.output,
        ], capture_output=True, text=True, timeout=90, env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})

    def test_collects_exact_embedded_and_fallback_texts_for_runtime_packages(self):
        fallback_identity = "Fallback/2.0.0"
        self.deps_manifest({
            "Embedded/1.0.0": {"runtime": {"lib/net11.0/Embedded.dll": {}},
                               "dependencies": {"Fallback": "2.0.0"}},
            fallback_identity: {"runtime": {"lib/net11.0/Fallback.dll": {}}},
            "BuildOnly/1.0.0": {},
            "Placeholder/1.0.0": {"runtime": {"lib/net11.0/_._": {}}},
        })
        self.package("Embedded", "1.0.0", '<license type="file">docs/terms.md</license>', {
            "docs/terms.md": b"Exact embedded licence\r\n",
            "THIRD PARTY NOTICE.txt": b"Third-party copyright\n",
        })
        self.package("Fallback", "2.0.0", '<license type="expression">MIT</license>', {})
        self.upstream_license(fallback_identity)

        result = self.collect()

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        index = self.index_path.read_text(encoding="utf-8")
        self.assertIn("Embedded/1.0.0", index)
        self.assertIn("NuGet Embedded/1.0.0:THIRD PARTY NOTICE.txt", index)
        self.assertIn(fallback_identity, index)
        self.assertIn("https://example.invalid/LICENSE", index)
        self.assertNotIn("BuildOnly", index)
        self.assertNotIn("Placeholder", index)
        contents = {path.read_bytes() for path in self.output.rglob("*") if path.is_file()}
        self.assertIn(b"Exact embedded licence\r\n", contents)
        notice, = (self.output / "documents").glob("*-THIRD-PARTY-NOTICE.txt")
        self.assertEqual(notice.read_bytes(), b"Third-party copyright\n")
        self.assertIn(b"Exact upstream license with copyright\n", contents)

    def test_rejects_expression_when_only_an_older_version_has_license_text(self):
        self.deps_manifest({"Missing/1.2.3": {"runtime": {"lib/net11.0/Missing.dll": {}}}})
        self.package("Missing", "1.2.3", '<license type="expression">MIT</license>', {
            "NOTICE": b"A notice without the package license\n",
        })
        self.upstream_license("Missing/1.2.2")

        result = self.collect()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Missing/1.2.3", result.stderr)
        self.assertIn("No license text was found", result.stderr)
        self.assertFalse(self.index_path.exists())

    def test_rejects_a_license_file_with_only_a_bom_and_whitespace(self):
        self.deps_manifest({"Empty/1.0.0": {"runtime": {"lib/net11.0/Empty.dll": {}}}})
        self.package("Empty", "1.0.0", '<license type="file">LICENSE</license>', {"LICENSE": b"\xef\xbb\xbf \r\n\t"})

        result = self.collect()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Empty/1.0.0", result.stderr)
        self.assertIn("Invalid license or notice document 'LICENSE'", result.stderr)
        self.assertFalse(self.index_path.exists())


if __name__ == "__main__":
    unittest.main()
