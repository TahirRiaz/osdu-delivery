#!/usr/bin/env bash
# Frees room on a GitHub-hosted Ubuntu runner before CI builds. A clean Release build of OsduDelivery.sln writes about
# 9 GB of output (each test and host project carries every dependency it runs with), beside the packages it restores
# and, in the image job, every image layer; together they fill the runner's disk, and a runner whose disk fills dies
# without its log. The toolchains removed here come with the runner image and are never used by this repository's CI:
# the Android SDK, the Haskell toolchain, CodeQL's bundle, Swift and Boost. Each is removed only where it exists, and
# the free space is printed before and after, so a run's log says how much room the build had.
set -euo pipefail

free() { df --output=avail -BG / | tail -n 1 | tr -d ' G'; }

before=$(free)
for path in /usr/local/lib/android /opt/ghc /usr/local/.ghcup /opt/hostedtoolcache/CodeQL /usr/share/swift /usr/local/share/boost; do
  if [ -e "$path" ]; then
    sudo rm -rf "$path"
    echo "removed $path"
  fi
done
echo "free on /: ${before} GB before, $(free) GB after"
