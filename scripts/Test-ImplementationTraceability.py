"""Check T103 inventory/links/requirement rows; this is not semantic or line coverage."""
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
MATRIX = ROOT / "artifacts/validation/implementation-test-traceability.md"
SPEC = ROOT / "specs/001-telemetry-ingestion/spec.md"


def validate():
    text = MATRIX.read_text(encoding="utf-8")
    errors = []
    definitions = re.findall(r"^\[([^\]]+)\]:\s+(\S+)\s*$", text, re.MULTILINE)
    refs = {}
    for key, target in definitions:
        if key in refs:
            errors.append("Duplicate reference: " + key)
        path = (MATRIX.parent / target).resolve()
        if not path.is_relative_to(ROOT) or not path.exists():
            errors.append("Missing or outside-repository target: " + key)
        refs[key] = path
    for key in re.findall(r"\[[^\]\n]+\]\[([^\]\n]+)\]", text):
        if key not in refs:
            errors.append("Undefined reference: " + key)

    rows = {}
    for line in text.splitlines():
        match = re.match(r"^\| ((?:FR|SC)-\d{3}) \|", line)
        if match:
            key = match[1]
            if key in rows:
                errors.append("Duplicate requirement row: " + key)
            rows[key] = line
            test_column = line.split("|")[3 if key.startswith("FR-") else 2]
            targets = [refs[k] for k in re.findall(r"\[[^\]]+\]\[([^\]]+)\]", test_column) if k in refs]
            tests = [p for p in targets if p.suffix == ".cs" and p.name.endswith("Tests.cs") and p.is_relative_to(ROOT / "tests")]
            if not tests:
                errors.append("No concrete automated test linked: " + key)
            for path in tests:
                if path.exists() and not re.search(r"\[(?:Fact|Theory)(?:\]|\(|,)", path.read_text(encoding="utf-8-sig")):
                    errors.append("No xUnit test declaration in " + path.name)

    expected = set(re.findall(r"\*\*((?:FR|SC)-\d{3})\*\*:", SPEC.read_text(encoding="utf-8")))
    for key in sorted(expected - rows.keys()):
        errors.append("Unmapped requirement: " + key)
    for key in sorted(rows.keys() - expected):
        errors.append("Unknown requirement: " + key)

    projects = sorted((ROOT / "src").rglob("*.csproj"))
    test_projects = sorted((ROOT / "tests").rglob("*.csproj"))
    # Require each project in a table row, not just an unused reference definition.
    table_refs = set(re.findall(r"\[[^\]]+\]\[([^\]]+)\]", "\n".join(line for line in text.splitlines() if line.startswith("|"))))
    linked_projects = {refs[k] for k in table_refs if k in refs}
    for project in projects + test_projects:
        if project.resolve() not in linked_projects:
            errors.append("Unmapped project: " + str(project.relative_to(ROOT)))
    if errors:
        print("FAIL: traceability structure is incomplete")
        for error in sorted(set(errors)):
            print("- " + error)
        return 1
    counts = {prefix: sum(k.startswith(prefix) for k in rows) for prefix in ("FR-", "SC-")}
    print(f"PASS: {len(projects)} source projects, {len(test_projects)} test projects, {counts['FR-']} FR, {counts['SC-']} SC, {len(refs)} local references")
    print("Scope: inventory and structural traceability only; assertions/results require review.")
    return 0


if __name__ == "__main__":
    sys.exit(validate())
