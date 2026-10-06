import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { loadContracts, runContract, validateEvidence, resultsPath, root } from './compiler-contracts.mjs';

mkdirSync(dirname(resultsPath), { recursive: true });
// Invalidate previous success before setup/compilation, including setup failures.
writeFileSync(resultsPath, JSON.stringify({ schema: 'xstate-csharp-compiler/1', cases: [], coveredDeclarations: [] }));
const context = loadContracts();
const cases = context.cases.map((test, index) => runContract(context, test, resolve(root, 'tmp/xstate-parity/compiler', String(index))));
const report = { schema: 'xstate-csharp-compiler/1', fingerprints: context.fingerprints, cases,
  coveredDeclarations: cases.filter(test => test.passed).map(test => test.id) };
writeFileSync(resultsPath, JSON.stringify(report, null, 2) + '\n');
validateEvidence(context, report);
console.log(`C# compiler assertion groups: ${report.coveredDeclarations.length}/${cases.length} pass. Full type parity is NOT implied.`);
