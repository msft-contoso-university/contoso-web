"""Focused tests for the fail-closed candidate evidence gate; no filesystem writes."""
import copy
import unittest

from check_codeql import require_expected_files, validate_report


class CodeQlEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.report = {
            "version": "2.1.0",
            "runs": [{
                "tool": {"driver": {"name": "CodeQL"}},
                "results": [],
                "invocations": [{"executionSuccessful": True}],
            }],
        }

    def test_complete_clean_evidence_is_accepted(self):
        require_expected_files(["actions.sarif", "python.sarif", "csharp.sarif"])
        validate_report(self.report, "csharp.sarif")

    def test_missing_or_unexpected_languages_fail(self):
        for names in ([], ["csharp.sarif", "actions.sarif"],
                      ["csharp.sarif", "actions.sarif", "python.sarif", "other.sarif"]):
            with self.subTest(names=names), self.assertRaises(ValueError):
                require_expected_files(names)

    def test_invalid_version_or_missing_runs_fail(self):
        for patch in ({"version": "2.0.0"}, {"runs": []}, {"runs": None}, {"runs": {}}):
            with self.subTest(patch=patch), self.assertRaises(ValueError):
                validate_report(self.report | patch, "csharp.sarif")

    def test_every_run_must_be_codeql(self):
        self.report["runs"].append({"tool": {"driver": {"name": "Other tool"}}})
        with self.assertRaises(ValueError):
            validate_report(self.report, "csharp.sarif")

    def test_findings_fail_even_when_suppressed(self):
        for results in ([{"ruleId": "example", "suppressions": [{"kind": "inSource"}]}], None):
            report = copy.deepcopy(self.report)
            report["runs"][0]["results"] = results
            with self.subTest(results=results), self.assertRaises(ValueError):
                validate_report(report, "csharp.sarif")

    def test_unsuccessful_or_malformed_invocations_fail(self):
        for invocations in ([{"executionSuccessful": False}], [{"executionSuccessful": "false"}], None):
            report = copy.deepcopy(self.report)
            report["runs"][0]["invocations"] = invocations
            with self.subTest(invocations=invocations), self.assertRaises(ValueError):
                validate_report(report, "csharp.sarif")

    def test_optional_invocation_status_is_not_invented(self):
        self.report["runs"][0]["invocations"] = [{}]
        validate_report(self.report, "csharp.sarif")


if __name__ == "__main__":
    unittest.main()
