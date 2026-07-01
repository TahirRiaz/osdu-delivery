# DeltaForge - Project Instructions

## Writing Style

**THE EM DASH CHARACTER (Unicode U+2014) IS ABSOLUTELY FORBIDDEN. NEVER USE IT. NOT ONCE. NOT ANYWHERE.**

This means: zero occurrences in prose, headings, descriptions, code comments, doc comments, string literals, Markdown, HTML, YAML, TOML, SQL comments, shell script comments, JSON string values, or any other text context. There are no exceptions. If you find yourself reaching for this character, stop and rewrite the sentence using a comma, colon, semicolon, parentheses, or a new sentence instead.

## No Time Estimates

**Never give time estimates for tasks.** No "this will take a few hours", no "1-2 days", no "should be done in a week", no "expect roughly N hours", no sprint-sized framings, no "small / medium / large effort" gradings that imply duration. Do not include them in plans, summaries, status updates, PR descriptions, design docs, or any other output.

This applies whether the unit is minutes, hours, days, weeks, sprints, or vague qualitative bands ("quick", "longer effort", "non-trivial amount of time"). The user is not asking how long something will take and is not interested in your guess. Your estimates are not calibrated to their environment, their priorities, or the rest of their workload, so the number is noise.

**What to do instead:** describe scope (what gets touched, how many call sites, what the risky steps are, what depends on what). Scope is useful; duration is not. If the user explicitly asks for a time estimate, you may provide one; otherwise, omit it entirely.

## Single Code Path Principle

**Never create multiple execution pathways for the same feature.** Before implementing something, verify whether an existing path already handles it. Extend or modify the existing path rather than creating a parallel one. This applies at all layers: backend execution, API routes, GUI commands, and frontend data flows.

## No TODOs, No Stubs, Production-Grade Only

**It is forbidden to leave TODOs, FIXMEs, placeholders, stubs, fake return values, or "will implement later" markers in any code you write.** Every function, branch, and module you touch must be fully implemented and production-grade before you hand it back. There is no such thing as a "first pass" that is allowed to be incomplete.

**This rule covers, without exception:**

- No `todo!()`, `unimplemented!()`, `unreachable!()` (unless the branch is genuinely unreachable and you can prove it from the type system, not from "it should not happen"), or `panic!("TODO ...")` in Rust.
- No `// TODO`, `// FIXME`, `// XXX`, `// HACK`, or `// stub` comments anywhere in code, doc comments, SQL, shell, TOML, YAML, JSON, HTML, CSS, or Markdown.
- No functions that return a hardcoded default, empty `Vec`, `None`, `null`, `0`, `""`, or fabricated success status as a placeholder for real logic.
- No `throw new Error("not implemented")`, `NotImplementedException`, `raise NotImplementedError`, or equivalent in any language.
- No half-wired control flow: every `match`/`switch`/`if` arm must do the real work, not log "would handle X here" and fall through.
- No commented-out code left in as a "future hook." If it is not running, delete it.
- No silent error swallowing as a stand-in for real handling. Every `Result`/`Option`/`try` boundary must do the right thing for both the success and failure path: propagate, recover, or surface to the user with context. "I will harden this later" is not an option.

**What "production-grade" means at the point you write the code:**

- Edge cases are handled: empty inputs, oversized inputs, unicode, concurrent access where the function can be hit concurrently, partial failure of downstream calls, IO/network/permission errors, malformed data, version skew across protocol boundaries.
- Resource lifetimes are explicit: files closed, transactions committed or rolled back, locks released on every exit path including panic/error, async tasks awaited or deliberately detached with a documented reason.
- Inputs are validated at trust boundaries (user input, external API responses, file contents, FFI), and trusted internal calls are not over-validated (see the global "no defensive checks for impossible scenarios" rule).
- Errors carry enough context to debug from a log line alone: which operation, which inputs (sanitised), which underlying cause.
- The code compiles, the types are tight (no stray `any`/`unknown`/`Box<dyn Any>` to dodge a real signature), and any new public surface has the same level of polish as the surrounding code.