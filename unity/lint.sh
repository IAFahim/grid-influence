#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C

root=$(cd -- "$(dirname -- "$0")/.." && pwd)
package=${1:-$root/unity/com.gi.entities}
[ -d "$package" ] || { printf 'usage: lint.sh [package-dir]\n' >&2; exit 2; }
command -v python3 >/dev/null 2>&1 || { printf 'python3 not found\n' >&2; exit 2; }

python3 -I - "$package" <<'PY'
import os
import re
import sys

package = sys.argv[1]
production = ["Runtime", "Presentation", "Authoring", "Editor", "Netcode"]
unity_free = [os.path.join("Runtime", "Columns"), os.path.join("Tests", "Support")]
unity_name = re.compile(r"\bUnity(?:Engine|Editor)?\b\s*[.;]")
linq_name = re.compile(r"\bSystem\s*\.\s*Linq\b")
debug_name = re.compile(r"\bUnityEngine\s*\.\s*Debug\b|\bDebug\s*\.\s*Log(?:Warning|Error|Exception|Format)?\s*\(")


def scan(text):
    code = []
    comments = []
    i = 0
    n = len(text)
    line = 1
    while i < n:
        c = text[i]
        two = text[i:i + 2]
        if two == "//":
            comments.append(line)
            while i < n and text[i] != "\n":
                i += 1
            continue
        if two == "/*":
            comments.append(line)
            end = text.find("*/", i + 2)
            end = n if end < 0 else end + 2
            line += text.count("\n", i, end)
            i = end
            continue
        if text.startswith('"""', i):
            end = text.find('"""', i + 3)
            end = n if end < 0 else end + 3
            line += text.count("\n", i, end)
            code.append(" ")
            i = end
            continue
        if text.startswith(('@"', '$@"', '@$"'), i):
            j = text.index('"', i) + 1
            while j < n:
                if text[j] == '"':
                    if text[j:j + 2] == '""':
                        j += 2
                        continue
                    j += 1
                    break
                j += 1
            line += text.count("\n", i, j)
            code.append(" ")
            i = j
            continue
        if c == '"' or (c == "$" and text[i:i + 2] == '$"'):
            j = i + (2 if c == "$" else 1)
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            code.append(" ")
            i = j + 1
            continue
        if c == "'":
            j = i + 1
            while j < n and text[j] != "'":
                j += 2 if text[j] == "\\" else 1
            code.append(" ")
            i = j + 1
            continue
        if c == "\n":
            line += 1
        code.append(c)
        i += 1
    return "".join(code), comments


def files(under):
    base = os.path.join(package, under)
    for directory, names, entries in os.walk(base):
        names.sort()
        for entry in sorted(entries):
            if entry.endswith(".cs"):
                yield os.path.join(directory, entry)


def within(path, folders):
    relative = os.path.relpath(path, package)
    return any(relative == f or relative.startswith(f + os.sep) for f in folders)


findings = set()
checked = 0
seen = set()
for folder in production + ["Tests"]:
    for path in files(folder):
        if path in seen:
            continue
        seen.add(path)
        checked += 1
        relative = os.path.relpath(path, package).replace(os.sep, "/")
        with open(path, encoding="utf-8") as handle:
            text = handle.read()
        code, comments = scan(text)
        in_production = any(relative == p or relative.startswith(p + "/") for p in production)
        if in_production:
            for line in comments:
                findings.add((relative, line, "no-comments", "production code carries no comments"))
        if relative.startswith("Runtime/"):
            for number, source in enumerate(code.split("\n"), 1):
                if linq_name.search(source):
                    findings.add((relative, number, "no-linq", "System.Linq is not allowed in Runtime"))
                if debug_name.search(source):
                    findings.add((relative, number, "no-debug", "UnityEngine.Debug is not allowed in Runtime"))
        if within(path, unity_free):
            for number, source in enumerate(code.split("\n"), 1):
                if unity_name.search(source):
                    findings.add((relative, number, "unity-free", "Unity namespaces are not allowed here"))

for relative, line, rule, message in sorted(findings):
    print("%s:%d: %s: %s" % (relative, line, rule, message))

if findings:
    sys.exit(1)

print("lint: ok (%d files)" % checked)
PY
