# Engineering Rules

## Operating principles

- Verify, do not guess. Never invent paths, APIs, package names, or behavior. Read code and run it where the environment permits. State unknowns explicitly.
- Be decisive. Ask only when the answer materially changes the work or the decision is hard to reverse.
- Think before coding. Consider empty/null values, boundaries, invalid input, concurrency, failures, and timeouts.
- Prefer the simplest correct solution. YAGNI applies.

## Engineering standard

- Organize code by feature/domain.
- Use small focused modules.
- Optimize for maintainability: clear names, simple control flow, sparse comments.
- Apply SOLID, DRY, YAGNI, and KISS pragmatically.
- Match repository tooling and conventions.
- Add no dependency without a clear need.
- Prefer solutions which are easy to explain, test, and modify.

## Implementation rules

- Validate inputs and handle failures explicitly.
- Isolate side effects. Prefer deterministic logic at the core.
- Document public APIs, non-obvious behavior, and important constraints concisely.
- Keep patches small and scoped.
- Do not refactor unrelated code.
- Stop once a change passes verification.

## Testing

- Test observable behavior, not implementation details.
- Use unit tests for deterministic logic and integration tests for real boundaries.
- Add regression tests for bug fixes.
- Avoid trivial, tautological, and brittle tests.
