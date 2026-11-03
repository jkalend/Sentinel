#!/usr/bin/env python3
"""Build the data-only SENTINEL_TEST ISO with Joliet long names."""

import argparse
import os
import sys

try:
    import pycdlib
except ImportError:
    sys.exit("pycdlib is required: python3 -m pip install --user pycdlib")


def add_tree(iso, directory, joliet_directory, counter):
    for entry in sorted(os.listdir(directory)):
        if entry == "autounattend.xml" and joliet_directory == "/":
            continue
        local = os.path.join(directory, entry)
        joliet = f"{joliet_directory}/{entry}" if joliet_directory != "/" else f"/{entry}"
        counter[0] += 1
        if os.path.isdir(local):
            iso.add_directory(f"/D{counter[0]:07d}", joliet_path=joliet)
            add_tree(iso, local, joliet, counter)
        else:
            iso.add_file(local, iso_path=f"/F{counter[0]:07d}.BIN", joliet_path=joliet)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    arguments = parser.parse_args()

    source = os.path.abspath(arguments.source)
    output = os.path.abspath(arguments.output)
    answer_file = os.path.join(source, "autounattend.xml")
    if not os.path.isfile(answer_file):
        sys.exit(f"missing {answer_file}")

    iso = pycdlib.PyCdlib()
    iso.new(interchange_level=3, joliet=3, vol_ident="SENTINEL_TEST")
    iso.add_file(answer_file, iso_path="/AUTOUNATTEND.XML", joliet_path="/autounattend.xml")
    add_tree(iso, source, "/", [0])
    iso.write(output)
    iso.close()
    print(f"created {output} ({os.path.getsize(output)} bytes, volume SENTINEL_TEST)")


if __name__ == "__main__":
    main()
