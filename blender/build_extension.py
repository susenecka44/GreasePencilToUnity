"""Zip the add-on for Blender's Extensions > Install from Disk.

    python blender/build_extension.py [--output-dir dist]

Blender's own builder produces the same thing and also validates the manifest::

    blender --command extension build --source-dir blender/grease_pencil_to_unity \
            --output-dir dist

Either way the manifest has to sit at the root of the zip.
"""

import argparse
import os
import re
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "grease_pencil_to_unity")
EXCLUDE_DIRS = {"__pycache__", ".git"}
EXCLUDE_SUFFIXES = (".pyc", ".pyo")


def read_manifest_field(name):
    path = os.path.join(SOURCE, "blender_manifest.toml")
    with open(path, encoding="utf-8") as handle:
        match = re.search(r'^%s\s*=\s*"([^"]+)"' % name, handle.read(), re.MULTILINE)
    if match is None:
        raise SystemExit("blender_manifest.toml has no %s field" % name)
    return match.group(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", default=os.path.join(os.path.dirname(HERE), "dist"))
    args = parser.parse_args()

    if not os.path.isdir(SOURCE):
        raise SystemExit("add-on source not found at %s" % SOURCE)

    identifier = read_manifest_field("id")
    version = read_manifest_field("version")
    os.makedirs(args.output_dir, exist_ok=True)
    target = os.path.join(args.output_dir, "%s-%s.zip" % (identifier, version))

    count = 0
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
        for root, dirs, files in os.walk(SOURCE):
            dirs[:] = [d for d in dirs if d not in EXCLUDE_DIRS]
            for name in sorted(files):
                if name.endswith(EXCLUDE_SUFFIXES):
                    continue
                path = os.path.join(root, name)
                archive.write(path, os.path.relpath(path, SOURCE))
                count += 1

    print("wrote %s (%d files, %.1f KB)" % (target, count, os.path.getsize(target) / 1024.0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
