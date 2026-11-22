#!/usr/bin/env python3
"""Print a readable summary of VSTest .trx files (totals + every failed test).

Usage: trx_summary.py <results-directory>
Writes plain text to stdout and Markdown to $GITHUB_STEP_SUMMARY when set.
Never fails the job itself (always exits 0); the test step decides pass/fail.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
MAX_CHARS = 3000


def clip(text):
    text = (text or "").strip()
    return text if len(text) <= MAX_CHARS else text[:MAX_CHARS] + "\n... (truncated)"


def main():
    results_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    files = sorted(glob.glob(os.path.join(results_dir, "**", "*.trx"), recursive=True))
    plain, md = [], ["## Test results", ""]

    if not files:
        msg = f"No .trx files found under {results_dir} (tests did not run, or the build failed first)."
        plain.append(msg)
        md.append(msg)

    for path in files:
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as exc:
            plain.append(f"{path}: could not parse ({exc})")
            md.append(f"`{path}`: could not parse ({exc})")
            continue

        counters = root.find("t:ResultSummary/t:Counters", NS)
        get = (lambda k: counters.get(k, "0")) if counters is not None else (lambda k: "?")
        line = (f"{os.path.basename(path)}: total {get('total')}, passed {get('passed')}, "
                f"failed {get('failed')}, not executed {get('notExecuted')}")
        plain.append(line)
        md.append(f"**{line}**")
        md.append("")

        failed = [r for r in root.findall("t:Results/t:UnitTestResult", NS) if r.get("outcome") == "Failed"]
        for r in failed:
            name = r.get("testName", "(unknown test)")
            message = clip(r.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS))
            stack = clip(r.findtext("t:Output/t:ErrorInfo/t:StackTrace", default="", namespaces=NS))
            plain += ["", f"FAILED: {name}", message, stack]
            md += [f"### Failed: `{name}`", "", "```text", message, stack, "```", ""]

        # Run-level errors (for example a test host crash) are reported outside the test results.
        for info in root.findall("t:ResultSummary/t:RunInfos/t:RunInfo", NS):
            text = clip(info.findtext("t:Text", default="", namespaces=NS))
            if text:
                plain += ["", f"RUN {info.get('outcome', 'info').upper()}: {text}"]
                md += ["```text", text, "```", ""]

    print("==================== TEST SUMMARY ====================")
    print("\n".join(plain))
    print("======================================================")

    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as fh:
            fh.write("\n".join(md) + "\n")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:  # the summary must never mask the real result
        print(f"trx_summary.py failed: {exc}")
    sys.exit(0)
