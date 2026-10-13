#!/usr/bin/env python3
"""Require an app version newer than the latest stable GitHub release."""

import argparse
import json
import os
from pathlib import Path
import re
import sys
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET


SEMVER = re.compile(
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
)


def version_key(version):
    match = SEMVER.fullmatch(version)
    if match is None:
        raise ValueError(f"Invalid semantic version: {version!r}.")
    core = tuple(int(match[index]) for index in (1, 2, 3))
    prerelease = match[4]
    if prerelease is None:
        return core, 1, ()
    identifiers = []
    for identifier in prerelease.split("."):
        if identifier.isascii() and identifier.isdigit():
            if len(identifier) > 1 and identifier.startswith("0"):
                raise ValueError(f"Invalid semantic version: {version!r}.")
            identifiers.append((0, int(identifier)))
        else:
            identifiers.append((1, identifier))
    return core, 0, tuple(identifiers)


def read_project_version(props):
    root = ET.parse(props).getroot()
    versions = root.findall("./PropertyGroup/Version")
    if len(versions) != 1 or not versions[0].text:
        raise ValueError(f"{props} must contain exactly one <Version>.")
    return versions[0].text.strip()


def release_version(tag):
    # Main builds have distinct tags, but stamp the app with the base version.
    version = tag[1:] if tag.startswith(("v", "V")) else tag
    return re.sub(r"-build\.[0-9]+$", "", version)


def github_json(url, token, allow_missing=False):
    headers = {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "windrunner-launcher-version-check",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"
    try:
        with urlopen(Request(url, headers=headers), timeout=30) as response:
            return json.load(response)
    except HTTPError as error:
        error.close()
        if allow_missing and error.code == 404:
            return None
        raise ValueError(f"Cannot read GitHub releases (HTTP {error.code}).") from error
    except (URLError, TimeoutError) as error:
        raise ValueError("Cannot read GitHub releases; check the network and retry.") from error


def latest_release(repository, api_url, token):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Set GITHUB_REPOSITORY or --repository to owner/repo.")
    releases_url = f"{api_url.rstrip('/')}/repos/{repository}/releases"
    release = github_json(f"{releases_url}/latest", token, allow_missing=True)
    if release is None:
        # Verify access before treating a 404 as a repository's first release.
        releases = github_json(f"{releases_url}?per_page=1", token)
        if not isinstance(releases, list):
            raise ValueError("GitHub returned an invalid releases response.")
        if any(not item.get("draft") and not item.get("prerelease") for item in releases):
            raise ValueError("GitHub could not resolve the latest stable release.")
        return None
    if not isinstance(release, dict) or not isinstance(release.get("tag_name"), str):
        raise ValueError("GitHub returned a release without a valid tag_name.")
    return release


def check_version(current, release):
    current_key = version_key(current)
    if release is None:
        return f"Version {current}: no stable release exists yet."
    tag = release["tag_name"]
    previous = release_version(tag)
    if current_key <= version_key(previous):
        raise ValueError(
            f"Version {current} must be newer than the latest release version "
            f"{previous} ({tag}). Bump <Version> in Directory.Build.props "
            "before merging or releasing."
        )
    return f"Version {current} is newer than the latest release {previous} ({tag})."


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--props", type=Path, default=Path("Directory.Build.props"))
    parser.add_argument("--version", help="Check the resolved release version instead of the props file.")
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", ""))
    args = parser.parse_args(argv)
    try:
        current = args.version if args.version is not None else read_project_version(args.props)
        version_key(current)
        release = latest_release(
            args.repository,
            os.environ.get("GITHUB_API_URL", "https://api.github.com"),
            os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN"),
        )
        print(check_version(current, release))
    except (ValueError, OSError, ET.ParseError) as error:
        message = str(error).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::error title=Release version check::{message}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
