#!/usr/bin/env python3
"""Copy the SENTINELOUT FAT image tree to a host directory without mounting."""

import argparse
import os
import posixpath
import shutil
import sys

try:
    from pyfatfs.PyFatFS import PyFatFS
except ImportError:
    sys.exit("pyfatfs is required: python3 -m pip install --user pyfatfs")


def copy_tree(fs, source, destination):
    os.makedirs(destination, exist_ok=True)
    for name in fs.listdir(source):
        source_path = posixpath.join(source, name)
        destination_path = os.path.join(destination, name)
        if fs.isdir(source_path):
            copy_tree(fs, source_path, destination_path)
        else:
            with fs.open(source_path, "rb") as reader, open(destination_path, "wb") as writer:
                shutil.copyfileobj(reader, writer)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--offset", type=int, default=0)
    arguments = parser.parse_args()

    fs = PyFatFS(arguments.image, offset=arguments.offset, read_only=True)
    try:
        copy_tree(fs, "/", arguments.output)
    finally:
        fs.close()
    print(f"extracted {arguments.image} to {arguments.output}")


if __name__ == "__main__":
    main()
