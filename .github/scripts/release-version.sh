#!/usr/bin/env bash
# The version a release build reports, as GITHUB_OUTPUT lines (version=,
# release=, prerelease=). release.yml runs it; release-version-test.sh checks it.
#
#   release-version.sh tag v1.2.3          version=1.2.3 release=true prerelease=false
#   release-version.sh tag v1.2.3-rc.1     version=1.2.3-rc.1 release=true prerelease=true
#   release-version.sh branch <commit sha> version=0.0.0-main.sha-<sha7> release=false prerelease=false
#
# Untagged builds use `sha-<sha7>` rather than the bare short sha: a short sha
# can be all digits with a leading zero (0123456), which SemVer forbids in a
# numeric pre-release identifier, and Helm and NuGet would reject it.
# Fails, printing the reason to stderr, for a tag that isn't valid SemVer.
set -euo pipefail

# SemVer 2.0.0 without build metadata (https://semver.org/#is-there-a-suggested-regular-expression-regex-to-check-a-semver-string).
ident='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
semver="^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-${ident}(\.${ident})*)?$"

if [[ $# -ne 2 ]]; then
  echo "usage: $0 tag|branch <tag name or commit sha>" >&2
  exit 2
fi

case $1 in
  tag)
    version="${2#v}"
    release=true
    if [[ "$version" == *-* ]]; then prerelease=true; else prerelease=false; fi
    ;;
  branch)
    if [[ ! "$2" =~ ^[0-9a-f]{7,}$ ]]; then
      echo "not a commit sha: $2" >&2
      exit 2
    fi
    version="0.0.0-main.sha-${2:0:7}"
    release=false
    prerelease=false
    ;;
  *)
    echo "usage: $0 tag|branch <tag name or commit sha>" >&2
    exit 2
    ;;
esac

if [[ ! "$version" =~ $semver ]]; then
  echo "$version is not a SemVer version (vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-PRERELEASE)" >&2
  exit 1
fi

echo "version=$version"
echo "release=$release"
echo "prerelease=$prerelease"
