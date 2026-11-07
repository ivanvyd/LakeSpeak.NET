import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPT = Path(__file__).with_name("check_nuget_vulnerabilities.py")


def load_checker():
    spec = importlib.util.spec_from_file_location("check_nuget_vulnerabilities", SCRIPT)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"could not load {SCRIPT}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def report(*, top_level=(), transitive=()):
    framework = {"framework": "net10.0"}
    if top_level:
        framework["topLevelPackages"] = list(top_level)
    if transitive:
        framework["transitivePackages"] = list(transitive)
    return {
        "version": 1,
        "parameters": "--vulnerable --include-transitive",
        "sources": ["https://api.nuget.org/v3/index.json"],
        "projects": [{"path": "fixture.csproj", "frameworks": [framework]}],
    }


def package(package_id, severity):
    return {
        "id": package_id,
        "resolvedVersion": "1.2.3",
        "vulnerabilities": [
            {
                "severity": severity,
                "advisoryurl": "https://github.com/advisories/GHSA-fixture",
            }
        ],
    }


class PackageAuditTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.checker = load_checker()

    def test_no_findings_passes(self):
        self.assertEqual([], self.checker.find_blocking_vulnerabilities(report()))

    def test_top_level_moderate_fails(self):
        findings = self.checker.find_blocking_vulnerabilities(
            report(top_level=[package("Top.Level", "Moderate")])
        )
        self.assertEqual("Top.Level", findings[0].package_id)

    def test_transitive_high_fails(self):
        findings = self.checker.find_blocking_vulnerabilities(
            report(transitive=[package("Transitive.Package", "High")])
        )
        self.assertEqual("Transitive.Package", findings[0].package_id)

    def test_low_severity_does_not_block(self):
        self.assertEqual(
            [],
            self.checker.find_blocking_vulnerabilities(
                report(top_level=[package("Low.Risk", "Low")])
            ),
        )

    def test_invalid_json_fails_closed(self):
        with self.assertRaises(self.checker.AuditError):
            self.checker.parse_report("not json")

    def test_invalid_schema_fails_closed(self):
        with self.assertRaises(self.checker.AuditError):
            self.checker.find_blocking_vulnerabilities({"version": 1})

    def test_audit_command_failure_is_not_treated_as_clean(self):
        failed = subprocess.CompletedProcess(
            args=["dotnet"], returncode=42, stdout="", stderr="audit source unavailable"
        )
        with patch.object(self.checker.subprocess, "run", return_value=failed):
            with self.assertRaises(self.checker.AuditError):
                self.checker.run_audit("fixture.slnx", "dotnet")

    def test_incomplete_report_parameters_fail_closed(self):
        for parameters in (None, "", "--vulnerable", "--include-transitive"):
            with self.subTest(parameters=parameters):
                value = report()
                value["parameters"] = parameters
                with self.assertRaises(self.checker.AuditError):
                    self.checker.find_blocking_vulnerabilities(value)

    def test_boolean_report_version_fails_closed(self):
        value = report()
        value["version"] = True
        with self.assertRaises(self.checker.AuditError):
            self.checker.find_blocking_vulnerabilities(value)

    def test_audit_logs_are_checked_at_each_scope(self):
        for scope in ("root", "project", "framework", "package", "advisory"):
            for level in ("Warning", "Error", "Unknown"):
                with self.subTest(scope=scope, level=level):
                    value = report(top_level=[package("Low.Risk", "Low")])
                    project = value["projects"][0]
                    framework = project["frameworks"][0]
                    item = framework["topLevelPackages"][0]
                    target = {"root": value, "project": project, "framework": framework,
                              "package": item, "advisory": item["vulnerabilities"][0]}[scope]
                    target["logs"] = [{"level": level, "message": "audit incomplete"}]
                    with self.assertRaises(self.checker.AuditError):
                        self.checker.find_blocking_vulnerabilities(value)

    def test_informational_audit_logs_do_not_block(self):
        value = report()
        value["logs"] = [{"level": "Information", "message": "audit complete"}]
        self.assertEqual([], self.checker.find_blocking_vulnerabilities(value))

    def test_malformed_audit_logs_fail_closed(self):
        for logs in (None, "warning", ["warning"], [{}]):
            with self.subTest(logs=logs):
                value = report()
                value["logs"] = logs
                with self.assertRaises(self.checker.AuditError):
                    self.checker.find_blocking_vulnerabilities(value)

    def test_successful_audit_command_with_stderr_fails_closed(self):
        result = subprocess.CompletedProcess(
            args=["dotnet"], returncode=0, stdout=json.dumps(report()), stderr="audit incomplete"
        )
        with patch.object(self.checker.subprocess, "run", return_value=result):
            with self.assertRaises(self.checker.AuditError):
                self.checker.run_audit("fixture.slnx", "dotnet")

    def test_main_reports_a_blocking_finding(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(
                json.dumps(report(top_level=[package("Blocked.Package", "Critical")])),
                encoding="utf-8",
            )
            self.assertEqual(1, self.checker.main(["--report", str(path)]))


if __name__ == "__main__":
    unittest.main()
