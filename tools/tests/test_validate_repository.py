import importlib.util
import tempfile
import unittest
from pathlib import Path


VALIDATOR_PATH = Path(__file__).resolve().parents[1] / "validate_repository.py"
SPEC = importlib.util.spec_from_file_location("validate_repository", VALIDATOR_PATH)
validator = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(validator)


class MetaGuidValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def write_meta(self, relative_path, guid_line, newline="\n"):
        path = self.root / relative_path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(("fileFormatVersion: 2" + newline + guid_line + newline).encode("utf-8"))
        return path

    def check_profile(self, base="Packages", profile="source"):
        report = validator.Reporter()
        validator.meta_check_profile(self.root, base, report, profile)
        return report

    def test_accepts_valid_lf_crlf_and_case_insensitive_guids(self):
        self.write_meta("Packages/One.cs.meta", "guid: 0123456789abcdef0123456789ABCDEF")
        self.write_meta("Packages/Two.cs.meta", "guid:\tabcdef0123456789abcdef0123456789  ", "\r\n")
        report = self.check_profile()
        self.assertEqual([], report.errors)

    def test_rejects_splitline_missing_nonhex_and_wrong_length_guids(self):
        self.write_meta("Packages/Split.cs.meta", "guid: " + "\n" + "0123456789abcdef0123456789abcdef")
        self.write_meta("Packages/Missing.cs.meta", "fileFormatVersion: 2")
        self.write_meta("Packages/NonHex.cs.meta", "guid: 0123456789abcdef0123456789abcdeg")
        self.write_meta("Packages/Short.cs.meta", "guid: 0123456789abcdef0123456789abcde")
        report = self.check_profile()
        self.assertEqual(4, len(report.errors))
        self.assertTrue(all("missing or invalid source guid" in error for error in report.errors))

    def test_rejects_duplicate_guid_within_a_profile(self):
        guid = "0123456789abcdef0123456789abcdef"
        self.write_meta("Packages/One.cs.meta", "guid: " + guid)
        self.write_meta("Packages/Two.cs.meta", "guid: " + guid.upper())
        report = self.check_profile()
        self.assertEqual(1, len(report.errors))
        self.assertIn("duplicate source guid", report.errors[0])

    def test_allows_same_guid_across_independent_distribution_profiles(self):
        guid = "0123456789abcdef0123456789abcdef"
        self.write_meta("Distribution/urp/One.cs.meta", "guid: " + guid)
        self.write_meta("Distribution/hdrp/One.cs.meta", "guid: " + guid)
        urp = self.check_profile("Distribution/urp", "distribution-urp")
        hdrp = self.check_profile("Distribution/hdrp", "distribution-hdrp")
        self.assertEqual([], urp.errors)
        self.assertEqual([], hdrp.errors)

    def test_reports_utf8_read_errors(self):
        path = self.root / "Packages/InvalidEncoding.cs.meta"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"guid: \xff\n")
        report = self.check_profile()
        self.assertEqual(1, len(report.errors))
        self.assertIn("unreadable source meta", report.errors[0])


if __name__ == "__main__":
    unittest.main()
