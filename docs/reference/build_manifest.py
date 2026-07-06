"""Regenerate docs/reference/manifest.json from the frontmatter of every page in this tree.

Run after adding, removing, or renaming a page under docs/reference/, or after editing any
page's frontmatter (id, title, type, summary, keywords, yamlPath, cliCommand, related,
sourceRefs). Usage: python docs/reference/build_manifest.py
"""

import glob
import json
import os
import re
import sys

import yaml

DOCS_ROOT = os.path.dirname(os.path.abspath(__file__))
DOC_TYPES = {"cli-command", "flow-reference", "source-type", "concept", "guide"}
FM_RE = re.compile(r"^---\s*\n(.*?\n)---\s*\n", re.DOTALL)


def find_pages():
    paths = []
    for p in glob.glob(os.path.join(DOCS_ROOT, "**", "*.md"), recursive=True):
        rel = os.path.relpath(p, DOCS_ROOT).replace(os.sep, "/")
        paths.append((p, rel))
    return sorted(paths, key=lambda t: t[1])


def parse_frontmatter(path):
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    m = FM_RE.match(text)
    if not m:
        return None, text
    try:
        fm = yaml.safe_load(m.group(1)) or {}
    except yaml.YAMLError as e:
        return {"__parse_error__": str(e)}, text
    return fm, text


def derive_summary(body_text):
    after_fm = FM_RE.sub("", body_text, count=1)
    lines = [l.strip() for l in after_fm.splitlines()]
    for l in lines:
        if not l or l.startswith("#"):
            continue
        return (l[:157] + "...") if len(l) > 160 else l
    return ""


def main():
    pages = find_pages()
    docs = []
    issues = []
    seen_ids = {}

    parsed = []
    for abspath, rel in pages:
        fm, text = parse_frontmatter(abspath)
        if fm is None:
            issues.append(f"{rel}: no frontmatter block found; skipped from manifest")
            continue
        if "__parse_error__" in fm:
            issues.append(f"{rel}: frontmatter YAML parse error: {fm['__parse_error__']}; skipped from manifest")
            continue
        parsed.append((rel, fm, text))

    all_ids = set()
    for rel, fm, _ in parsed:
        doc_id = fm.get("id")
        if not doc_id:
            issues.append(f"{rel}: missing 'id' in frontmatter; skipped from manifest")
            continue
        if doc_id in seen_ids:
            issues.append(f"{rel}: duplicate id '{doc_id}' (also used by {seen_ids[doc_id]}); kept first occurrence only")
            continue
        seen_ids[doc_id] = rel
        all_ids.add(doc_id)

    for rel, fm, text in parsed:
        doc_id = fm.get("id")
        if not doc_id or seen_ids.get(doc_id) != rel:
            continue

        title = fm.get("title")
        doc_type = fm.get("type")
        summary = fm.get("summary")
        keywords = fm.get("keywords") or []

        if not title:
            issues.append(f"{rel}: missing 'title'; used filename as fallback")
            title = os.path.splitext(os.path.basename(rel))[0]
        if doc_type not in DOC_TYPES:
            issues.append(f"{rel}: missing or invalid 'type' ({doc_type!r}); left as-is, needs manual fix")
        if not summary:
            derived = derive_summary(text)
            issues.append(f"{rel}: missing 'summary'; derived one from body text")
            summary = derived
        elif len(summary) > 160:
            issues.append(f"{rel}: 'summary' exceeds 160 chars; truncated for manifest only (source file left untouched)")
            summary = summary[:157] + "..."
        if not keywords:
            issues.append(f"{rel}: missing/empty 'keywords'")

        related = fm.get("related") or []
        pruned_related = [r for r in related if r in all_ids]
        dropped = [r for r in related if r not in all_ids]
        if dropped:
            issues.append(f"{rel}: pruned {len(dropped)} dangling related id(s) from manifest entry: {dropped}")

        entry = {
            "id": doc_id,
            "path": rel,
            "title": title,
            "type": doc_type,
            "summary": summary,
            "keywords": keywords,
        }
        if fm.get("yamlPath"):
            entry["yamlPath"] = fm["yamlPath"]
        if fm.get("cliCommand"):
            entry["cliCommand"] = fm["cliCommand"]
        entry["related"] = pruned_related
        entry["sourceRefs"] = fm.get("sourceRefs") or []

        docs.append(entry)

    docs.sort(key=lambda d: d["id"])

    manifest = {
        "version": 1,
        "product": "SQLFlow V3 (DeltaForge)",
        "docs": docs,
    }

    out_path = os.path.join(DOCS_ROOT, "manifest.json")
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"pages_found={len(pages)}")
    print(f"docs_indexed={len(docs)}")
    print(f"issues_count={len(issues)}")
    for i in issues:
        print(f"ISSUE: {i}")
    print(f"written={out_path}")


if __name__ == "__main__":
    sys.exit(main())
