#!/usr/bin/env bash
# Fails when sqlflow/ is not exactly the SQLFlow commit it was vendored from.
#
# sqlflow/ is a squashed git subtree. Every `git subtree add` or `git subtree pull --squash` records a squash commit
# whose tree is the vendored SQLFlow content and whose message names the upstream commit (git-subtree-split). This
# check compares that tree with sqlflow/ as committed at HEAD and with the working tree, so an edit to vendored code is
# caught whether or not it has been committed. Changes to SQLFlow are made in the SQLFlow repository and pulled in.
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
cd "$root"

squash="$(git log --format=%H --grep="^Squashed 'sqlflow/' " -n 1)"
if [ -z "$squash" ]; then
  echo "check-vendored-sqlflow: no squashed subtree commit for sqlflow/ was found; add SQLFlow with 'git subtree add --prefix=sqlflow --squash'." >&2
  exit 1
fi

upstream="$(git log -1 --format=%B "$squash" | sed -n 's/^git-subtree-split: //p' | head -n 1)"
expected="$(git rev-parse "$squash^{tree}")"

if ! actual="$(git rev-parse --verify --quiet "HEAD:sqlflow")"; then
  echo "check-vendored-sqlflow: HEAD has no sqlflow/ directory." >&2
  exit 1
fi

if [ "$actual" != "$expected" ]; then
  echo "check-vendored-sqlflow: sqlflow/ at HEAD differs from SQLFlow ${upstream:-(unknown commit)} as vendored in ${squash}." >&2
  echo "Files that differ:" >&2
  git diff --stat "$squash" "HEAD:sqlflow" -- >&2 || git diff --name-status "$expected" "$actual" >&2
  echo "Make the change in the SQLFlow repository and run: git subtree pull --prefix=sqlflow --squash <sqlflow-repo> main" >&2
  exit 1
fi

if [ -n "$(git status --porcelain -- sqlflow)" ]; then
  echo "check-vendored-sqlflow: sqlflow/ has uncommitted changes:" >&2
  git status --short -- sqlflow >&2
  echo "Vendored SQLFlow is never edited here; revert them and make the change in the SQLFlow repository." >&2
  exit 1
fi

echo "check-vendored-sqlflow: sqlflow/ is SQLFlow ${upstream:-(unknown commit)}, unmodified."
