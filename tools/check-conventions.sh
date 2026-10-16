#!/usr/bin/env bash
# Checks what a change brings against two of the repository's rules (CLAUDE.md):
#   - no line it adds outside sqlflow/ carries an em dash (U+2014);
#   - no commit it brings is attributed to an AI assistant: no Co-Authored-By trailer naming Claude or Anthropic, no
#     "Generated with Claude" line, no Anthropic noreply address, and no author or committer of that name or address.
# sqlflow/ is SQLFlow's text, vendored as it is and guarded by tools/check-vendored-sqlflow.sh. Commits Dependabot
# writes are not read for attribution: their messages quote upstream release notes, which this project does not write.
#
# Usage: tools/check-conventions.sh <base> <head>
#   The commits checked are the ones <head> has and <base> does not (base..head); the lines checked are the ones added
#   from their merge base to <head> (base...head). CI passes a pull request's base and head, or a push's previous and
#   new tip. Before opening a pull request: tools/check-conventions.sh origin/main HEAD
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: tools/check-conventions.sh <base> <head>" >&2
  exit 2
fi

root="$(git rev-parse --show-toplevel)"
cd "$root"

if ! base="$(git rev-parse --verify --quiet "$1^{commit}")"; then
  echo "check-conventions: '$1' names no commit in this clone; fetch it, or the full history, first." >&2
  exit 2
fi
if ! head="$(git rev-parse --verify --quiet "$2^{commit}")"; then
  echo "check-conventions: '$2' names no commit in this clone; fetch it, or the full history, first." >&2
  exit 2
fi
if ! git merge-base "$base" "$head" > /dev/null; then
  echo "check-conventions: $1 and $2 share no history, so there is no change between them to check." >&2
  exit 2
fi

annotate=false
if [ "${GITHUB_ACTIONS:-}" = true ]; then
  annotate=true
fi

failed=0

# Added lines with an em dash, as path:line, from a zero-context diff. A header's "+++" line is read only between a
# "diff --git" line and the first hunk, so an added line that itself starts with "++" is never taken for a path.
emdash=$'\xe2\x80\x94'
findings="$(
  git -c core.quotePath=false diff --no-color --no-ext-diff --no-prefix -U0 --diff-filter=d "$base...$head" \
    -- . ':(exclude)sqlflow' |
  LC_ALL=C awk -v mark="$emdash" '
    /^diff --git / { header = 1; next }
    header && /^\+\+\+ / { path = substr($0, 5); next }
    /^@@ / {
      header = 0
      split($3, start, ",")
      line = substr(start[1], 2) + 0
      next
    }
    header { next }
    /^\+/ {
      if (index($0, mark) > 0) {
        printf "%s\t%d\t%s\n", path, line, substr($0, 2, 200)
      }
      line++
    }
  '
)"
if [ -n "$findings" ]; then
  echo "check-conventions: added lines carry an em dash (U+2014), which the repository allows nowhere; rewrite each" \
       "with a comma, a colon, a semicolon, parentheses or a new sentence:" >&2
  while IFS=$'\t' read -r path line text; do
    echo "  $path:$line: $text" >&2
    if [ "$annotate" = true ]; then
      echo "::error file=$path,line=$line,title=Em dash::This line carries an em dash (U+2014); rewrite it without one."
    fi
  done <<< "$findings"
  failed=1
fi

# Commits attributed to an AI assistant, by their message or by who wrote them.
attribution='^[[:space:]]*co-authored-by:.*(claude|anthropic)|generated with[^[:alnum:]]*claude|noreply@anthropic\.com'
dependabot='49699333+dependabot[bot]@users.noreply.github.com'
while read -r commit; do
  [ -z "$commit" ] && continue
  author_email="$(git log -1 --format=%ae "$commit")"
  if [ "$author_email" = "$dependabot" ]; then
    continue
  fi
  subject="$(git log -1 --format='%h %s' "$commit")"
  hits="$(git log -1 --format=%B "$commit" | grep -inE "$attribution" || true)"
  identity="$(git log -1 --format='%an <%ae>%n%cn <%ce>' "$commit" | grep -iE '^claude <|anthropic\.com>$' || true)"
  if [ -n "$hits" ] || [ -n "$identity" ]; then
    echo "check-conventions: commit $subject is attributed to an AI assistant; every commit is the person's own," \
         "with no such trailer, line or identity:" >&2
    if [ -n "$hits" ]; then
      printf '%s\n' "$hits" | sed -E 's/^([0-9]+):/  message line \1: /' >&2
    fi
    if [ -n "$identity" ]; then
      printf '%s\n' "$identity" | sed 's/^/  author or committer: /' >&2
    fi
    if [ "$annotate" = true ]; then
      echo "::error title=AI attribution::Commit $subject is attributed to an AI assistant; reword it without the trailer, line or identity."
    fi
    failed=1
  fi
done < <(git rev-list "$base..$head")

commits="$(git rev-list --count "$base..$head")"
if [ "$failed" -eq 0 ]; then
  echo "check-conventions: $commits commit(s) from ${base:0:12} to ${head:0:12}: no em dash added outside sqlflow/, no AI attribution."
fi
exit "$failed"
