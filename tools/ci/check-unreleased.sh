#!/usr/bin/env bash
# Is there code on this branch that no release carries yet, and for how long?
#
#   tools/ci/check-unreleased.sh              # report; fail once the oldest such change is past the window
#   tools/ci/check-unreleased.sh --report     # report only, never fail
#   STALE_HOURS=48 tools/ci/check-unreleased.sh
#
# Commits waiting for a release are normal, so this does not fail on the first
# one. It fails when shipped code has sat unreleased for longer than the window
# (24 hours by default): by then people downloading the latest release are
# running code that is already fixed on main. A release clears it.
#
# Only shipped paths count. A docs or CI commit does not need a release.

set -uo pipefail

STALE_HOURS="${STALE_HOURS:-24}"
REPORT_ONLY=0
[[ "${1:-}" == "--report" ]] && REPORT_ONLY=1

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${repo_root}"

# What a release ships: the program, the example configs and the two service
# scripts in the Windows zip, and the build settings every build reads.
SHIPPED=(src examples scripts/install-service.ps1 scripts/uninstall-service.ps1 Directory.Build.props)
paths=()
for p in "${SHIPPED[@]}"; do
    [[ -e ${p} ]] && paths+=("${p}")
done
if [[ ${#paths[@]} -eq 0 ]]; then
    echo "UNRELEASED CHECK: none of the shipped paths exist here, cannot judge" >&2
    exit 2
fi

# A shallow clone cannot see the tags behind HEAD, so finding no tag there means
# "cannot tell", never "nothing released".
if [[ $(git rev-parse --is-shallow-repository 2>/dev/null) == "true" ]]; then
    echo "UNRELEASED CHECK: this is a shallow clone, so the release tags behind HEAD cannot be seen." >&2
    echo "Check out the full history (actions/checkout fetch-depth: 0, or git fetch --unshallow)." >&2
    exit 2
fi

latest_tag=$(git describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || echo "")
if [[ -z ${latest_tag} ]]; then
    echo "UNRELEASED CHECK: no release tags found, cannot judge" >&2
    exit 2
fi

count=$(git rev-list --count "${latest_tag}..HEAD" -- "${paths[@]}")

if [[ ${count} -eq 0 ]]; then
    echo "UNRELEASED CHECK PASS: every shipped change is in ${latest_tag}."
    exit 0
fi

# The oldest unreleased change has waited longest.
oldest=$(git rev-list "${latest_tag}..HEAD" -- "${paths[@]}" | tail -1)
oldest_epoch=$(git log -1 --format=%ct "${oldest}")
hours=$(( ($(date +%s) - oldest_epoch) / 3600 ))

echo "UNRELEASED: ${count} shipped commit(s) after ${latest_tag}, the oldest ${hours}h ago:"
git log --oneline --no-decorate "${latest_tag}..HEAD" -- "${paths[@]}" | head -10 | sed 's/^/    /'
(( count > 10 )) && echo "    ... and $(( count - 10 )) more"

if (( REPORT_ONLY )); then
    exit 0
fi

if (( hours >= STALE_HOURS )); then
    echo "UNRELEASED CHECK FAIL: shipped code has waited ${hours}h for a release (limit ${STALE_HOURS}h). Cut a release." >&2
    exit 1
fi

echo "Within the ${STALE_HOURS}h window, so not failing yet."
exit 0
