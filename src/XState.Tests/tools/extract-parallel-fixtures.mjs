import ts from '../../../data/library-source/xstate-test-tools/node_modules/typescript/lib/typescript.js';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
function literal(node) {
  if (ts.isStringLiteralLike(node)) return node.text;
  if (ts.isNumericLiteral(node)) return Number(node.text);
  if (node.kind === ts.SyntaxKind.TrueKeyword) return true;
  if (node.kind === ts.SyntaxKind.FalseKeyword) return false;
  if (node.kind === ts.SyntaxKind.NullKeyword) return null;
  if (ts.isArrayLiteralExpression(node)) return node.elements.map(literal);
  if (ts.isObjectLiteralExpression(node)) {
    const entries = node.properties.map(property => {
      if (!ts.isPropertyAssignment(property) || ts.isComputedPropertyName(property.name)) throw Error('Nonliteral property');
      return [property.name.text, literal(property.initializer)];
    });
    return Object.fromEntries(entries);
  }
  if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'raise' && node.arguments.length === 1) {
    const event = literal(node.arguments[0]);
    if (!event || typeof event.type !== 'string') throw Error('raise requires a literal event type');
    return { __raiseEvent: event };
  }
  throw Error('Unsupported expression: ' + node.getText().slice(0, 90));
}
function keys(value, allowed, role) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw Error('Invalid ' + role);
  for (const key of Object.keys(value)) if (!allowed.includes(key)) throw Error('Unsupported ' + role + ' property: ' + key);
}
function actions(value) {
  if (value === undefined) return;
  if (Array.isArray(value)) { value.forEach(actions); return; }
  if (typeof value === 'string') return;
  keys(value, ['__raiseEvent'], 'action');
  if (!value.__raiseEvent || typeof value.__raiseEvent.type !== 'string') throw Error('Unsupported action');
}
function transition(value) {
  if (typeof value === 'string') return;
  if (Array.isArray(value)) { value.forEach(transition); return; }
  keys(value, ['target', 'reenter', 'actions'], 'transition');
  actions(value.actions);
}
function validate(config) {
  keys(config, ['id','initial','states','on','always','onDone','type','history','target','tags','meta','entry','exit'], 'state');
  if (config.initial !== undefined && typeof config.initial !== 'string') throw Error('Unsupported initial transition');
  actions(config.entry); actions(config.exit);
  for (const child of Object.values(config.states ?? {})) validate(child);
  for (const value of Object.values(config.on ?? {})) transition(value);
  if (config.always !== undefined) transition(config.always);
  if (config.onDone !== undefined) transition(config.onDone);
}
export function extractFixtures(text, source) {
  const ast = ts.createSourceFile(source, text, ts.ScriptTarget.Latest, true);
  const fixtures = {}; const expressions = {}; const rejected = [];
  function visit(node) {
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer && ts.isCallExpression(node.initializer) &&
        ts.isIdentifier(node.initializer.expression) && node.initializer.expression.text === 'createMachine') {
      const line = ast.getLineAndCharacterOfPosition(node.getStart(ast)).line + 1;
      const key = `${node.name.text}@${line}`;
      try {
        if (node.initializer.arguments.length !== 1) throw Error('Machine implementations are not literal fixtures');
        const config = literal(node.initializer.arguments[0]); validate(config);
        fixtures[key] = { line, config }; expressions[key] = node.initializer.getText(ast);
      } catch (error) { rejected.push({ key, reason: error.message }); }
    }
    ts.forEachChild(node, visit);
  }
  visit(ast); return { fixtures, expressions, rejected };
}
function generate() {
  const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
  const source = 'packages/core/test/parallel.test.ts';
  const text = readFileSync(resolve(root, pin.sourceDirectory, source), 'utf8');
  const { fixtures, expressions, rejected } = extractFixtures(text, source);
  const report = { commit: pin.commit, source, sourceSha256: createHash('sha256').update(text).digest('hex'), fixtures, rejected };
  writeFileSync(resolve(root, 'src/XState.Tests/fixtures/upstream-parallel.json'), JSON.stringify(report, null, 2) + '\n');
  // Original initializer expressions, not the JSON translation, run against the pinned JS source.
  const importPath = pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href;
  const module = `import { createMachine, raise } from ${JSON.stringify(importPath)};\nexport const machines = {\n` +
    Object.entries(expressions).map(([key, expression]) => `${JSON.stringify(key)}: ${expression}`).join(',\n') + '\n};\n';
  mkdirSync(resolve(root, 'tmp/xstate-parity'), { recursive: true });
  writeFileSync(resolve(root, 'tmp/xstate-parity/parallel-machines.mts'), module);
  console.log(JSON.stringify({ fixtures: Object.keys(fixtures), rejected: rejected.length }));
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) generate();
