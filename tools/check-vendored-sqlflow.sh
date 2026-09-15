#!/usr/bin/env bash
# Guards the vendored SQLFlow in sqlflow/.
#
# sqlflow/ is a squashed git subtree of SQLFlow. This project changes it only to add generic extension points, each in a
# commit of its own that touches nothing outside sqlflow/, and never with OSDU code. The check:
#   - names the SQLFlow commit sqlflow/ was vendored from and lists every file changed here since then;
#   - fails when a commit since the vendoring changes sqlflow/ together with any other path;
#   - fails when a line added to sqlflow/ since the vendoring (committed or not) mentions OSDU or the delivery module.
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
cd "$root"

squash="$(git log --format=%H --grep="^Squashed 'sqlflow/' " -n 1)"
if [ -z "$squash" ]; then
  echo "check-vendored-sqlflow: no squashed subtree commit for sqlflow/ was found; add SQLFlow with 'git subtree add --prefix=sqlflow --squash'." >&2
  exit 1
fi

upstream="$(git log -1 --format=%B "$squash" | sed -n 's/^git-subtree-split: //p' | head -n 1)"
vendored="$(git rev-parse "$squash^{tree}")"
if ! current="$(git rev-parse --verify --quiet "HEAD:sqlflow")"; then
  echo "check-vendored-sqlflow: HEAD has no sqlflow/ directory." >&2
  exit 1
fi

failed=0

# A commit that changes sqlflow/ must change nothing else.
while read -r commit; do
  [ -z "$commit" ] && continue
  outside="$(git diff-tree --no-commit-id --name-only -r "$commit" | grep -v '^sqlflow/' || true)"
  if [ -n "$outside" ]; then
    echo "check-vendored-sqlflow: commit $(git log -1 --format='%h %s' "$commit") changes sqlflow/ together with other paths:" >&2
    printf '%s\n' "$outside" | sed 's/^/  /' >&2
    failed=1
  fi
done < <(git rev-list --no-merges "$squash"..HEAD -- sqlflow)

# No OSDU code in sqlflow/: added lines, committed since the vendoring or still in the working tree.
added="$( { git diff -U0 "$vendored" "$current"; git diff -U0 HEAD -- sqlflow; } | grep '^+' | grep -v '^+++' || true)"
mentions="$(printf '%s\n' "$added" | grep -inE 'osdu|SqlFlow\.Delivery' || true)"
if [ -n "$mentions" ]; then
  echo "check-vendored-sqlflow: lines added to sqlflow/ mention OSDU or the delivery module; sqlflow/ holds generic extension points only:" >&2
  printf '%s\n' "$mentions" | head -n 20 | sed 's/^/  /' >&2
  failed=1
fi

echo "check-vendored-sqlflow: sqlflow/ is SQLFlow ${upstream:-(unknown commit)} plus this project's extension points."
changed="$(git diff --name-status "$vendored" "$current")"
if [ -n "$changed" ]; then
  echo "Files changed in sqlflow/ since it was vendored:"
  printf '%s\n' "$changed" | sed 's/^/  /'
else
  echo "No files changed in sqlflow/ since it was vendored."
fi

uncommitted="$(git status --porcelain -- sqlflow)"
if [ -n "$uncommitted" ]; then
  echo "Uncommitted changes in sqlflow/:"
  printf '%s\n' "$uncommitted" | sed 's/^/  /'
fi

exit "$failed"
