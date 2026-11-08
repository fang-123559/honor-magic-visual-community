"""Offline generator checks. Never reads tokens, contacts GitHub or publishes."""
import importlib.util
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("manifest", Path(__file__).with_name("write-update-manifest.py"))
manifest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manifest)


def release(tag, **fields):
    return dict(tag_name=tag, draft=False, prerelease="-" in tag,
                published_at="2026-10-27T00:00:00Z", body="公开更新说明", **fields)


class ManifestTests(unittest.TestCase):
    def test_empty(self):
        result = manifest.generate([])
        self.assertEqual(result["repository"], manifest.REPOSITORY)
        self.assertEqual(result["schemaVersion"], 1)
        self.assertEqual(result["releases"], [])

    def test_independent_channels_and_numeric_order(self):
        data = [release("0.3.9"), release("0.4.0-preview.9"), release("0.4.0-preview.10"), release("0.3.8")]
        result = manifest.generate(data)
        self.assertEqual([r["tag_name"] for r in result["releases"]], ["0.3.9", "0.4.0-preview.10"])
        for row in result["releases"]:
            self.assertEqual(set(row), {"tag_name", "draft", "prerelease", "published_at", "body"})

    def test_draft_and_unrelated_tags(self):
        draft = release("9.0.0"); draft["draft"] = True
        self.assertEqual(manifest.generate([draft, release("not-a-version")])["releases"], [])

    def test_reject_unpublished_and_missing_channel(self):
        for field in ("published_at", "prerelease", "draft"):
            data = release("0.3.9"); del data[field]
            with self.subTest(field=field), self.assertRaises((ValueError, TypeError)):
                manifest.generate([data])

    def test_invalid_versions(self):
        for tag in ("", "v", "01.0.0", "1.0.0-01", "1.0.0+", "1.0.0-..", "1.0.0+a..b", "../1.0.0"):
            with self.subTest(tag=tag):
                self.assertIsNone(manifest.version_key(tag))
        self.assertEqual(manifest.version_key("v1.0.0+build.001"), manifest.version_key("1.0.0"))

    def test_bounded_notes_and_no_asset_or_account_data(self):
        data = release("0.3.9"); data.update(body="字" * 100000, author={"private": "never publish"}, assets=[{"url": "https://invalid.test"}])
        result = manifest.generate([data])
        self.assertEqual(len(result["releases"][0]["body"]), 16000)
        self.assertNotIn("never publish", json.dumps(result))
        self.assertLess(len(json.dumps(result, ensure_ascii=False).encode()), manifest.MAX_BYTES)


if __name__ == "__main__":
    unittest.main()
