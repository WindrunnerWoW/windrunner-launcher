import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError


SCRIPT = Path(__file__).resolve().parents[1] / "check-release-version.py"
SPEC = importlib.util.spec_from_file_location("check_release_version", SCRIPT)
guard = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(guard)


class VersionCheckTests(unittest.TestCase):
    def test_duplicate_and_older_versions_fail(self):
        for current, tag in (
            ("0.1.1", "v0.1.1-build.5"),
            ("0.1.1", "v0.1.1"),
            ("0.1.0", "v0.1.1-build.5"),
            ("0.1.1+new-hash", "v0.1.1+old-hash"),
            ("0.1.1-rc.1", "v0.1.1-build.5"),
            ("0.1.2-beta.2", "v0.1.2-beta.10-build.5"),
        ):
            with self.subTest(current=current, tag=tag):
                with self.assertRaisesRegex(ValueError, "must be newer"):
                    guard.check_version(current, {"tag_name": tag})

    def test_newer_versions_pass(self):
        for current, tag in (
            ("0.1.2", "v0.1.1-build.5"),
            ("0.1.10", "v0.1.9-build.5"),
            ("0.2.0", "v0.1.99"),
            ("1.0.0", "V0.99.99"),
            ("0.1.2", "0.1.2-rc.1"),
            ("0.1.2-beta.10", "v0.1.2-beta.2"),
        ):
            with self.subTest(current=current, tag=tag):
                self.assertIn("is newer", guard.check_version(current, {"tag_name": tag}))

    def test_invalid_versions_fail_even_without_a_release(self):
        for current in ("", "0.1", "01.1.1", "0.1.1-beta.01", "$(Version)", "0.1.1\n"):
            with self.subTest(current=current):
                with self.assertRaisesRegex(ValueError, "Invalid semantic version"):
                    guard.check_version(current, None)

    def test_unknown_release_tag_fails(self):
        with self.assertRaisesRegex(ValueError, "Invalid semantic version"):
            guard.check_version("0.1.2", {"tag_name": "nightly"})

    def test_build_suffix_preserves_real_prerelease(self):
        self.assertEqual(guard.release_version("v0.2.0-rc.1-build.12"), "0.2.0-rc.1")

    def test_props_are_read_as_xml(self):
        with tempfile.TemporaryDirectory() as directory:
            props = Path(directory) / "Directory.Build.props"
            props.write_text(
                "<Project><!-- <Version>9.9.9</Version> -->"
                "<PropertyGroup><Version>\n 0.1.2 \n</Version></PropertyGroup></Project>",
                encoding="utf-8",
            )
            self.assertEqual(guard.read_project_version(props), "0.1.2")
            for xml in (
                "<Project />",
                "<Project><PropertyGroup><Version /></PropertyGroup></Project>",
                "<Project><PropertyGroup><Version>0.1.1</Version>"
                "<Version>0.1.2</Version></PropertyGroup></Project>",
            ):
                props.write_text(xml, encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "exactly one"):
                    guard.read_project_version(props)

    def test_first_release_is_allowed(self):
        self.assertIn("no stable release", guard.check_version("0.1.0", None))


class GitHubReleaseTests(unittest.TestCase):
    def fetch_latest(self):
        return guard.latest_release("owner/repo", "https://api.github.com", "test-token")

    def test_latest_endpoint_and_token(self):
        with patch.object(guard, "urlopen", return_value=io.BytesIO(
            json.dumps({"tag_name": "v0.1.1-build.5"}).encode()
        )) as request:
            self.assertEqual(self.fetch_latest()["tag_name"], "v0.1.1-build.5")
        args, kwargs = request.call_args
        self.assertEqual(args[0].full_url, "https://api.github.com/repos/owner/repo/releases/latest")
        self.assertEqual(args[0].get_header("Authorization"), "Bearer test-token")
        self.assertEqual(kwargs["timeout"], 30)

    def test_no_latest_release_verifies_repository_access(self):
        for releases in ([], [{"draft": False, "prerelease": True}], [{"draft": True}]):
            with self.subTest(releases=releases):
                with patch.object(guard, "github_json", side_effect=[None, releases]) as request:
                    self.assertIsNone(self.fetch_latest())
                self.assertEqual(request.call_args.args[0],
                                 "https://api.github.com/repos/owner/repo/releases?per_page=1")

    def test_inaccessible_repository_does_not_pass_as_first_release(self):
        missing = HTTPError("https://api.github.com", 404, "Not Found", {}, io.BytesIO())
        with patch.object(guard, "urlopen", side_effect=missing):
            with self.assertRaisesRegex(ValueError, "HTTP 404"):
                self.fetch_latest()

    def test_api_and_network_errors_fail(self):
        for status in (401, 403, 429, 500):
            with self.subTest(status=status):
                error = HTTPError("https://api.github.com", status, "Error", {}, io.BytesIO())
                with patch.object(guard, "urlopen", side_effect=error):
                    with self.assertRaisesRegex(ValueError, f"HTTP {status}"):
                        self.fetch_latest()
        for error in (URLError("offline"), TimeoutError()):
            with self.subTest(error=error):
                with patch.object(guard, "urlopen", side_effect=error):
                    with self.assertRaisesRegex(ValueError, "check the network"):
                        self.fetch_latest()

    def test_malformed_api_response_fails(self):
        for response in ({}, [], {"tag_name": None}):
            with self.subTest(response=response):
                with patch.object(guard, "github_json", return_value=response):
                    with self.assertRaisesRegex(ValueError, "valid tag_name"):
                        self.fetch_latest()

    def test_cli_blocks_duplicate_and_allows_bump(self):
        release = {"tag_name": "v0.1.1-build.5"}
        for version, expected_code in (("0.1.1", 1), ("0.1.2", 0)):
            with self.subTest(version=version):
                with patch.object(guard, "latest_release", return_value=release):
                    with contextlib.redirect_stdout(io.StringIO()), \
                            contextlib.redirect_stderr(io.StringIO()):
                        code = guard.main(["--version", version, "--repository", "owner/repo"])
                self.assertEqual(code, expected_code)


if __name__ == "__main__":
    unittest.main()
