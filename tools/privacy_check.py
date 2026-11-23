#!/usr/bin/env python3
"""Check release inputs without printing sensitive matches. Python 3.10+.

This is a release guard, not proof that an arbitrary binary is anonymous.
Compressed/encrypted data requires an independent resource/provenance review.
"""

import argparse
from collections import Counter
from pathlib import Path
import re
import subprocess
import sys


EXCLUDED_DIRECTORIES = {".git", "bin", "obj", "dist", "artifacts", "publish", "packages", "__pycache__", ".vs"}
PRIVATE_NAMES = {"integration.json", "status.json", "session.log", "screen-profile.json", "screen-profile.json.tmp", "ui-preferences.json", "ui-preferences.json.tmp", "private-terms.txt", "private-verification.json", ".env"}
PRIVATE_SUFFIXES = {".log", ".dmp", ".etl", ".apk", ".xapk", ".hbc", ".har", ".pcap", ".pcapng", ".png", ".jpg", ".jpeg", ".mp4", ".webm", ".key", ".pem", ".pdb"}
PATTERNS = {
    "absolute-user-profile-path": re.compile(r"(?i)[a-z]:[\\/]+Users[\\/]+[^\\/\s\"'<>]+"),
    "absolute-unix-home-path": re.compile(r"/(?:home|Users)/[A-Za-z0-9_.-]+/"),
    "literal-control-token": re.compile(r"(?i)[\"'](?:token|api[_-]?key|access[_-]?token)[\"']\s*[:=]\s*[\"'][a-f0-9]{64}[\"']"),
    "literal-bearer-token": re.compile(r"Bearer\s+[A-Za-z0-9_+/.=-]{40,}"),
    "literal-jwt": re.compile(r"\beyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\b"),
    "credential-in-pairing-link": re.compile(r"feel(?:app|vr)://(?:authorize|connect)[^\s\"'<>]*[?&](?:token|code)=[A-Za-z0-9_+/.%=-]{24,}"),
}


def inside(path, root):
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def candidates(root):
    # Git's selection is authoritative once a repository exists, including
    # ignored files explicitly force-added. Untracked source is also reviewed.
    files = set()
    if (root / ".git").exists():
        result = subprocess.run(["git", "-C", str(root), "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=True)
        for raw in result.stdout.split(b"\0"):
            if raw:
                files.add(root / raw.decode("utf-8", errors="strict"))
    else:
        files.update(p for p in root.rglob("*") if p.is_file()
                     and not any(part in EXCLUDED_DIRECTORIES for part in p.relative_to(root).parts[:-1]))
    return sorted(files)


def inspect(path, private_terms, artifact=False, public_name=""):
    findings = Counter()
    if not artifact and (path.name.lower() in PRIVATE_NAMES or path.suffix.lower() in PRIVATE_SUFFIXES
                         or path.name.lower().endswith(".mix.json")):
        findings["private-runtime-or-research-file"] += 1
    payload = path.read_bytes()
    texts = [payload.decode("utf-8", errors="ignore")]
    # Native/managed binaries often store text in UTF-16. Decode both alignments.
    if b"\x00" in payload:
        texts += [payload[offset:].decode("utf-16-le", errors="ignore") for offset in (0, 1)]
    for category, pattern in PATTERNS.items():
        findings[category] += sum(len(pattern.findall(value)) for value in texts)
    for term in private_terms:
        variants = {term.encode("utf-8"), term.encode("utf-16-le"), term.encode("utf-16-be")}
        # Case-insensitive ASCII also catches hexadecimal device identifiers.
        # Count a configured term once per file. UTF-16 byte alignments can
        # overlap, so summing each encoding would double-count one disclosure.
        findings["private-audit-term"] += int(term.casefold() in public_name.casefold()
                                              or any(value.lower() in payload.lower() for value in variants))
    return +findings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--artifact", action="append", type=Path, default=[], help="Also scan a specified compiled release file")
    parser.add_argument("--private-terms-file", type=Path, help="UTF-8 terms file outside the publication root")
    args = parser.parse_args()
    root = args.root.resolve()
    terms = []
    try:
        if not root.is_dir():
            raise ValueError("Publication root is not a directory.")
        if args.private_terms_file:
            private_path = args.private_terms_file.resolve()
            if inside(private_path, root):
                raise ValueError("The private terms file must be outside the publication root.")
            terms = [line.strip() for line in private_path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
            if any(len(term) < 4 for term in terms):
                raise ValueError("Private audit terms must contain at least four characters.")
        files = [(p, False) for p in candidates(root)]
        files += [(p.resolve(), True) for p in args.artifact]
        totals = Counter()
        affected = 0
        for path, artifact in files:
            # Do not follow a repository symlink into a private outside path.
            if not artifact and not inside(path.resolve(), root):
                totals["outside-root-link"] += 1
                affected += 1
                continue
            public_name = path.name if artifact else path.relative_to(root).as_posix()
            findings = inspect(path, terms, artifact, public_name)
            if findings:
                affected += 1
                totals.update(findings)
        # Deliberately omit even paths: filenames can themselves be personal.
        print(f"Scanned {len(files)} files; {affected} files with findings.")
        for category, count in sorted(totals.items()):
            print(f"{category}: {count}")
        if not totals:
            print("No configured privacy markers found. Review bundled resources and release provenance separately.")
        return 1 if totals else 0
    except (OSError, ValueError, subprocess.SubprocessError):
        print("Privacy scan could not complete. Check input paths, permissions and audit options.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
