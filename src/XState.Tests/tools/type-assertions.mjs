// Dedicated type-test files also contain positive-only checks; transpiling them
// successfully is not evidence that the compiler accepted their contracts.
export function requiresCompilerEvidence(file, source, suites = []) {
  return /(?:^|[./])(?:types|typeHelpers)\.test\.[cm]?[jt]sx?$/.test(file.replaceAll('\\', '/')) ||
    suites.some(title => /\btype safety\b/i.test(title)) ||
    /@ts-expect-error|expectTypeOf|\bsatisfies\b/.test(source);
}
