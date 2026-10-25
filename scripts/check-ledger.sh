#!/usr/bin/env bash
# Fails when a plan marked complete still has an unfilled verification-ledger cell.
# Plans that are not complete are skipped: they are allowed to carry TODO rows.
set -euo pipefail

cd "$(dirname "$0")/.."

status=0
for plan in .kilo/plans/*.md; do
  [[ -f "$plan" ]] || continue

  # Only enforce plans whose status line declares them complete.
  if ! grep -qiE '^\*\*Status:\*\*.{0,40}\bcomplete\b' "$plan"; then
    continue
  fi

  ledger=$(awk '/^## Verification ledger/{flag=1; next} /^## /{flag=0} flag' "$plan")
  if [[ -z "$ledger" ]]; then
    echo "FAIL: $plan is complete but has no '## Verification ledger' section" >&2
    status=1
    continue
  fi

  if grep -qE '\| *(TODO|not run|) *\|' <<<"$ledger"; then
    echo "FAIL: $plan is complete but has an unfilled ledger row:" >&2
    grep -nE '\| *(TODO|not run|) *\|' <<<"$ledger" >&2 || true
    status=1
  fi
done

if [[ "$status" == "0" ]]; then
  echo "ledger: ok (no complete plan has unfilled rows)"
fi
exit "$status"
