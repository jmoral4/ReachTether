#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

SOLUTION_DIR="${REPO_ROOT}/dotNet"
SOLUTION_PATH="${SOLUTION_DIR}/ReachTether.slnx"
PROJECT_PATH="${REPO_ROOT}/dotNet/ReachTether.Robot/ReachTether.Robot.csproj"
OUTPUT_DIR="${REPO_ROOT}/out/reachrobot"

REMOTE_USER="${REMOTE_USER:-pollen}"
REMOTE_HOST="${REMOTE_HOST:-reachy-mini.local}"
REMOTE_DIR="${REMOTE_DIR:-/home/pollen/reachrobot/}"

usage() {
    cat <<EOF
Usage: $(basename "$0") [--publish-only] [--scp-only]

Environment overrides:
  REMOTE_USER  SSH username        (default: pollen)
  REMOTE_HOST  SSH host            (default: reachy-mini.local)
  REMOTE_DIR   Remote deploy path  (default: /home/pollen/reachrobot/)

Examples:
  $(basename "$0")
  REMOTE_HOST=192.168.1.50 $(basename "$0")
  $(basename "$0") --publish-only
EOF
}

run_publish=true
run_scp=true

while [[ $# -gt 0 ]]; do
    case "$1" in
        --publish-only)
            run_scp=false
            shift
            ;;
        --scp-only)
            run_publish=false
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 1
            ;;
    esac
done

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet is required but was not found on PATH." >&2
    exit 1
fi

if ! command -v scp >/dev/null 2>&1; then
    echo "scp is required but was not found on PATH." >&2
    exit 1
fi

if [[ "${run_publish}" == true ]]; then
    echo "Building solution from ${SOLUTION_DIR}"
    (
        cd "${SOLUTION_DIR}"
        dotnet build "${SOLUTION_PATH}" -c Release
    )

    echo "Publishing ReachTether.Robot to ${OUTPUT_DIR}"
    dotnet publish "${PROJECT_PATH}" \
        -c Release \
        -r linux-arm64 \
        --self-contained false \
        -o "${OUTPUT_DIR}"
fi

if [[ "${run_scp}" == true ]]; then
    if [[ ! -d "${OUTPUT_DIR}" ]]; then
        echo "Publish output was not found at ${OUTPUT_DIR}" >&2
        echo "Run without --scp-only, or publish first." >&2
        exit 1
    fi

    remote_target="${REMOTE_USER}@${REMOTE_HOST}:${REMOTE_DIR}"
    echo "Copying ${OUTPUT_DIR} to ${remote_target}"
    scp -r "${OUTPUT_DIR}/." "${remote_target}"
fi
