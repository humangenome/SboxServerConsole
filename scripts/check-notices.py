#!/usr/bin/env python3
"""Fail if THIRD-PARTY-NOTICES.txt misses a notice for code built into the program, or an archive lacks the notices.

The release is one self-contained file, so every package it is built from ships inside it: the .NET runtime pack and
every NuGet package in the build's deps.json. For each of them this reads the package as restored (the NuGet cache) and
requires its licence file and its own third-party notices file, when it has them, to appear word for word in
THIRD-PARTY-NOTICES.txt (line endings ignored), and its name to appear there. A new dependency therefore fails the
check until its notice is added.

Usage: check-notices.py --deps BUILD_DIR/SboxServerConsole.deps.json [--deps ...] [ARCHIVE ...]
  --deps     a deps.json from a Release build for one runtime (bin/Release/net10.0/<rid>/SboxServerConsole.deps.json)
  ARCHIVE    a release .zip or .tar.gz; its root must hold LICENSE and THIRD-PARTY-NOTICES.txt, byte for byte the
             repository's copies
The NuGet cache is $NUGET_PACKAGES, or ~/.nuget/packages.
"""
import json
import os
import sys
import tarfile
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NOTICES = "THIRD-PARTY-NOTICES.txt"
LICENCE_FILES = ("LICENSE", "LICENSE.TXT", "LICENSE.txt", "License.txt", "LICENSE.md")
PACKAGE_NOTICE_FILES = ("THIRD-PARTY-NOTICES.TXT", "THIRD-PARTY-NOTICES.txt", "ThirdPartyNotices.txt")


def norm(b: bytes) -> str:
    return b.decode("utf-8-sig").replace("\r\n", "\n").strip("\n")


def libraries(deps_path: str):
    with open(deps_path, encoding="utf-8") as f:
        deps = json.load(f)
    for key, lib in deps.get("libraries", {}).items():
        if lib.get("type") == "project":
            continue
        name, _, version = key.partition("/")
        yield name, version


def main(argv):
    deps, archives = [], []
    it = iter(argv)
    for a in it:
        if a == "--deps":
            deps.append(next(it, ""))
        else:
            archives.append(a)
    if not deps and not archives:
        print(__doc__, file=sys.stderr)
        return 2

    with open(os.path.join(ROOT, NOTICES), "rb") as f:
        notices_bytes = f.read()
    with open(os.path.join(ROOT, "LICENSE"), "rb") as f:
        licence_bytes = f.read()
    notices = norm(notices_bytes)
    cache = os.environ.get("NUGET_PACKAGES") or os.path.expanduser("~/.nuget/packages")
    fail = 0

    seen = set()
    for d in deps:
        if not os.path.isfile(d):
            print(f"::error::no deps.json at {d!r}")
            fail = 1
            continue
        for name, version in libraries(d):
            package = name[len("runtimepack."):] if name.startswith("runtimepack.") else name
            if (package, version) in seen:
                continue
            seen.add((package, version))
            folder = os.path.join(cache, package.lower(), version)
            if not os.path.isdir(folder):
                print(f"::error::{package} {version} is not in the NuGet cache ({folder})")
                fail = 1
                continue
            found = []
            for group in (LICENCE_FILES, PACKAGE_NOTICE_FILES):
                for fn in group:
                    p = os.path.join(folder, fn)
                    if os.path.isfile(p):
                        found.append(fn)
                        with open(p, "rb") as f:
                            if norm(f.read()) not in notices:
                                print(f"::error::{NOTICES} does not carry {package} {version}'s {fn} word for word")
                                fail = 1
                        break
            label = "the .NET runtime" if package.startswith("Microsoft.NETCore.App.Runtime.") else package
            if not package.startswith("Microsoft.NETCore.App.Runtime.") and package not in notices:
                print(f"::error::{NOTICES} does not name {package}")
                fail = 1
            print(f"{package} {version}: {', '.join(found) or 'no licence file in the package (licence in its nuspec)'}"
                  f" -> {label} covered")
    if deps and not seen:
        print("::error::the deps.json files list no packages; the check would prove nothing")
        fail = 1

    for a in archives:
        members = {}
        try:
            if a.endswith(".zip"):
                with zipfile.ZipFile(a) as z:
                    for n in (NOTICES, "LICENSE"):
                        if n in z.namelist():
                            members[n] = z.read(n)
            elif a.endswith(".tar.gz"):
                with tarfile.open(a, "r:gz") as t:
                    for m in t.getmembers():
                        n = m.name[2:] if m.name.startswith("./") else m.name
                        if n in (NOTICES, "LICENSE") and m.isfile():
                            members[n] = t.extractfile(m).read()
            else:
                print(f"::error::{a}: not a .zip or .tar.gz")
                fail = 1
                continue
        except Exception as e:  # an unreadable archive fails, never passes
            print(f"::error::{a}: cannot read ({e})")
            fail = 1
            continue
        for n, want in ((NOTICES, notices_bytes), ("LICENSE", licence_bytes)):
            if n not in members:
                print(f"::error::{a} has no {n} at its root")
                fail = 1
            elif members[n] != want:
                print(f"::error::{a}: {n} differs from the repository's copy")
                fail = 1
            else:
                print(f"{a}: {n} present, same as the repository's")

    print("notices check " + ("FAILED" if fail else "clean"))
    return fail


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
