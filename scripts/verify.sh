#!/usr/bin/env bash
# Canonical quality gate. Run before claiming any item done, and in CI.
#
#   scripts/verify.sh          build + test + format
#   scripts/verify.sh --uat    also run the lake backfill UAT on a scratch lake
#
# UAT requires a populated source SQLite DB (default: output/econdata.db, override with
# ECONDATA_SQLITE_PATH). If it has no observations the UAT is reported as not run, not green.
set -euo pipefail

cd "$(dirname "$0")/.."

DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  DOTNET="$HOME/.dotnet/dotnet"
fi
UAT=0
[[ "${1:-}" == "--uat" ]] && UAT=1

echo "==> build"
"$DOTNET" build EconData.slnx

echo "==> test"
"$DOTNET" test EconData.slnx

echo "==> format"
"$DOTNET" format EconData.slnx --verify-no-changes

if [[ "$UAT" == "1" ]]; then
  echo "==> uat: lake backfill"
  source_db="${ECONDATA_SQLITE_PATH:-output/econdata.db}"
  if [[ ! -f "$source_db" ]]; then
    echo "not run: source SQLite DB not found at $source_db" >&2
    exit 1
  fi
  observations=$(python3 - "$source_db" <<'PY'
import sqlite3, sys
try:
    with sqlite3.connect(sys.argv[1]) as connection:
        print(connection.execute("SELECT COUNT(*) FROM dbe_observations").fetchone()[0])
except Exception:
    print(0)
PY
)
  if [[ "$observations" == "0" ]]; then
    echo "not run: $source_db has no observations (nothing to backfill); populate it first" >&2
    exit 1
  fi

  work="${ECONDATA_UAT_DIR:-/tmp/econdata-verify}"
  rm -rf "$work"
  export ECONDATA_LAKE_DIR="$work/lake"
  export ECONDATA_SQLITE_PATH="$source_db"

  "$DOTNET" run --project src/EconData.Cli -- lake init

  before=$("$DOTNET" run --project src/EconData.Cli -- lake status | grep dbe_observations)
  "$DOTNET" run --project src/EconData.Cli -- lake import-from-sqlite --dry-run
  after=$("$DOTNET" run --project src/EconData.Cli -- lake status | grep dbe_observations)
  if [[ "$before" != "$after" ]]; then
    echo "FAIL: --dry-run mutated gold ($before -> $after)" >&2
    exit 1
  fi
  echo "ok: --dry-run left gold unchanged ($after)"

  "$DOTNET" run --project src/EconData.Cli -- lake import-from-sqlite --write-silver
  "$DOTNET" run --project src/EconData.Cli -- lake build-gold
fi

echo "verify: ok"
