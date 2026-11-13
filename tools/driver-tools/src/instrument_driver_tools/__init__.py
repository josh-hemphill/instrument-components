"""Build-time tooling; no device implementation or runtime dependency."""
VERSION = "0.3.0-alpha.1"
CONTRACT_MAJOR = 1


def python_version(version):
    """Normalize the checked SDK preview identity for Python wheel filenames."""
    import re
    match = re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-(alpha|beta|rc)\.(0|[1-9][0-9]*)", version)
    if match is None:
        raise ValueError("Expected a prerelease SDK version")
    return ".".join(match.groups()[:3]) + {"alpha": "a", "beta": "b", "rc": "rc"}[match[4]] + match[5]
