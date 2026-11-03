#!/usr/bin/env bash
# Checks release-version.sh: every version it prints is SemVer (including
# short shas that are all digits with a leading zero), and invalid tags fail.
#
#   .github/scripts/release-version-test.sh
set -euo pipefail

script="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/release-version.sh"
failures=0

# Independent of the script's own regex: semver.org's suggested pattern, in ERE.
ident='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
semver="^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-${ident}(\.${ident})*)?$"

# expect KIND VALUE OUTPUT: the script prints OUTPUT (one line, spaces for newlines).
expect() {
  local got version
  if ! got=$("$script" "$1" "$2" 2>&1); then
    echo "FAIL  $1 $2: exited with an error: $got"
    failures=$((failures + 1))
    return
  fi
  version=$(sed -n 's/^version=//p' <<<"$got")
  if [[ "$(tr '\n' ' ' <<<"$got" | sed 's/ $//')" != "$3" ]]; then
    echo "FAIL  $1 $2: got $(tr '\n' ' ' <<<"$got")"
    failures=$((failures + 1))
  elif [[ ! "$version" =~ $semver ]]; then
    echo "FAIL  $1 $2: $version is not SemVer"
    failures=$((failures + 1))
  else
    echo "ok    $1 $2 -> $version"
  fi
}

# expect_refused KIND VALUE: the script exits non-zero.
expect_refused() {
  if "$script" "$1" "$2" >/dev/null 2>&1; then
    echo "FAIL  $1 $2: accepted"
    failures=$((failures + 1))
  else
    echo "ok    $1 $2 refused"
  fi
}

expect branch 0123456789abcdef0123456789abcdef01234567 "version=0.0.0-main.sha-0123456 release=false prerelease=false"
expect branch 0000000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa "version=0.0.0-main.sha-0000000 release=false prerelease=false"
expect branch 1234567ffffffffffffffffffffffffffffffff0 "version=0.0.0-main.sha-1234567 release=false prerelease=false"
expect branch abcdef0123456789abcdef0123456789abcdef01 "version=0.0.0-main.sha-abcdef0 release=false prerelease=false"
expect tag v1.2.3 "version=1.2.3 release=true prerelease=false"
expect tag v0.1.0-rc.1 "version=0.1.0-rc.1 release=true prerelease=true"
expect tag v2.0.0-beta.2.x-y "version=2.0.0-beta.2.x-y release=true prerelease=true"
expect_refused tag v1.2
expect_refused tag v01.2.3
expect_refused tag v1.2.3-01
expect_refused tag v1.2.3-rc..1
expect_refused tag v1.2.3+build
expect_refused branch not-a-sha

if ((failures > 0)); then
  echo "$failures check(s) failed"
  exit 1
fi
echo "All checks passed"
