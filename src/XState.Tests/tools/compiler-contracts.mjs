import { readFileSync, readdirSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { resolve, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { isDeepStrictEqual } from 'node:util';

export const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const casesDirectory = resolve(root, 'src/XState.Tests/compiler-cases');
export const resultsPath = resolve(root, 'tmp/xstate-parity/compile-results.json');
export const hashFile = path => createHash('sha256').update(readFileSync(path)).digest('hex');
const json = path => JSON.parse(readFileSync(path, 'utf8'));
function command(args) {
  const result = spawnSync('dotnet', args, { encoding: 'utf8', timeout: 30000, windowsHide: true });
  if (result.error || result.status !== 0) throw Error(`dotnet ${args.join(' ')} failed: ${result.error ?? result.stderr}`);
  return result.stdout.trim();
}
export function loadContracts() {
  const pin = json(resolve(root, 'src/XState/upstream.json'));
  const inventory = json(resolve(root, 'src/XState.Tests/upstream-inventory.json'));
  const manifestPath = resolve(casesDirectory, 'manifest.json');
  const cases = json(manifestPath);
  const ids = new Set();
  for (const test of cases) {
    if (ids.has(test.id) || !inventory.tests.some(t => t.id === test.id && t.typeAssertions)) throw Error(`Invalid compiler mapping: ${test.id}`);
    ids.add(test.id);
    if (!test.mapping || !test.positive || !Array.isArray(test.expectedDiagnostics)) throw Error(`Incomplete compiler mapping: ${test.id}`);
    if (Boolean(test.negative) !== (test.expectedDiagnostics.length > 0)) throw Error(`Negative fixture/diagnostics mismatch: ${test.id}`);
    for (const fixture of [test.positive, test.negative].filter(Boolean)) {
      if (basename(fixture) !== fixture || !fixture.endsWith('.cs.txt')) throw Error(`Invalid fixture path: ${fixture}`);
    }
    for (const diagnostic of test.expectedDiagnostics) {
      if (!/^CS\d{4}$/.test(diagnostic.id) || !Number.isInteger(diagnostic.line) || diagnostic.line < 1) throw Error(`Invalid expected diagnostic: ${test.id}`);
    }
  }
  const sdkVersion = command(['--version']);
  const installed = command(['--list-sdks']).split(/\r?\n/).map(line => /^(\S+) \[(.+)\]$/.exec(line));
  const sdk = installed.find(match => match?.[1] === sdkVersion);
  if (!sdk) throw Error(`Cannot resolve active SDK ${sdkVersion}`);
  const compiler = resolve(sdk[2], sdkVersion, 'Roslyn/bincore/csc.dll');
  const packRoot = resolve(sdk[2], '../packs/Microsoft.NETCore.App.Ref');
  const packVersion = readdirSync(packRoot).filter(v => /^8\.\d+\.\d+$/.test(v)).sort((a, b) => a.localeCompare(b, undefined, { numeric: true })).at(-1);
  if (!packVersion) throw Error('Missing net8.0 reference pack');
  const refsDirectory = resolve(packRoot, packVersion, 'ref/net8.0');
  const references = readdirSync(refsDirectory).filter(f => f.endsWith('.dll')).sort().map(f => resolve(refsDirectory, f));
  const library = resolve(root, 'tmp/csharp-lint/XState.Tests/XState.Tests/XState.dll');
  const fingerprints = {
    upstreamCommit: pin.commit, inventorySha256: hashFile(resolve(root, 'src/XState.Tests/upstream-inventory.json')),
    manifestSha256: hashFile(manifestPath), librarySha256: hashFile(library),
    sdkVersion, compilerSha256: hashFile(compiler), referencePackVersion: packVersion,
    referencePackSha256: createHash('sha256').update(references.map(file => `${basename(file)}:${hashFile(file)}`).join('\n')).digest('hex'),
    harnessSha256: hashFile(fileURLToPath(import.meta.url)),
    runnerSha256: hashFile(resolve(root, 'src/XState.Tests/tools/Compile-Contracts.mjs')),
    fixtures: Object.fromEntries([...new Set(cases.flatMap(t => [t.positive, t.negative].filter(Boolean)))].sort().map(file => [file, hashFile(resolve(casesDirectory, file))]))
  };
  return { cases, fingerprints, compiler, references, library };
}
// Every case is compiled separately: an unrelated compile error cannot satisfy a negative assertion.
export function compileSource(context, source, directory, name) {
  mkdirSync(directory, { recursive: true });
  const sourcePath = resolve(directory, name + '.cs');
  const sarifPath = resolve(directory, name + '.sarif');
  const assemblyPath = resolve(directory, name + '.dll');
  writeFileSync(sourcePath, source);
  rmSync(sarifPath, { force: true }); rmSync(assemblyPath, { force: true });
  const quote = value => {
    if (/[\r\n"]/.test(value)) throw Error('Invalid compiler path');
    return `"${value}"`;
  };
  const options = ['/nologo', '/target:library', '/langversion:12', '/nullable:enable', '/warnaserror+', '/deterministic+', '/optimize+',
    ...context.references.map(path => '/reference:' + quote(path)), '/reference:' + quote(context.library),
    '/out:' + quote(assemblyPath), '/errorlog:' + quote(sarifPath) + ',version=2.1', quote(sourcePath)];
  const responsePath = resolve(directory, name + '.rsp'); writeFileSync(responsePath, options.join('\n'));
  const result = spawnSync('dotnet', ['exec', context.compiler, '@' + responsePath], { encoding: 'utf8', timeout: 30000, windowsHide: true });
  if (result.error || result.signal || result.status === null) throw Error(`Compiler process failed: ${result.error ?? result.signal}`);
  const sarif = json(sarifPath);
  const diagnostics = sarif.runs.flatMap(run => run.results ?? []).map(d => ({
    id: d.ruleId, line: d.locations?.[0]?.physicalLocation?.region?.startLine ?? null, level: d.level
  })).sort((a, b) => a.line - b.line || a.id.localeCompare(b.id));
  return { exitCode: result.status, diagnostics, output: result.stdout + result.stderr };
}
export function outcomePasses(test, outcome) {
  const expected = test.expectedDiagnostics.map(d => ({ ...d, level: 'error' })).sort((a, b) => a.line - b.line || a.id.localeCompare(b.id));
  return outcome.positive?.exitCode === 0 && isDeepStrictEqual(outcome.positive.diagnostics, []) &&
    (test.negative ? outcome.negative?.exitCode === 1 && isDeepStrictEqual(outcome.negative.diagnostics, expected) : outcome.negative === null);
}
export function runContract(context, test, directory) {
  const positive = compileSource(context, readFileSync(resolve(casesDirectory, test.positive), 'utf8'), directory, 'positive');
  const negative = test.negative ? compileSource(context, readFileSync(resolve(casesDirectory, test.negative), 'utf8'), directory, 'negative') : null;
  const result = { id: test.id, positive, negative };
  return { ...result, passed: outcomePasses(test, result) };
}
export function validateEvidence(context, report) {
  if (report.schema !== 'xstate-csharp-compiler/1' || !isDeepStrictEqual(report.fingerprints, context.fingerprints)) throw Error('Compiler evidence is stale or has incompatible provenance; rerun Compile-Contracts.mjs.');
  if (!Array.isArray(report.cases) || report.cases.length !== context.cases.length) throw Error('Compiler evidence is missing cases.');
  const ids = new Set();
  for (const test of context.cases) {
    const matches = report.cases.filter(result => result.id === test.id);
    if (matches.length !== 1 || !outcomePasses(test, matches[0]) || matches[0].passed !== true) throw Error(`Compiler assertion failed: ${test.id}`);
    ids.add(test.id);
  }
  if (!isDeepStrictEqual(report.coveredDeclarations, [...ids])) throw Error('Compiler coverage does not match successful case evidence.');
  return ids;
}
