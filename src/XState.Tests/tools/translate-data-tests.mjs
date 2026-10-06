import ts from '../../../data/library-source/xstate-test-tools/node_modules/typescript/lib/typescript.js';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const inventory = JSON.parse(readFileSync(resolve(here, '../upstream-inventory.json'), 'utf8'));
const output = { commit: pin.commit, tests: [], rejected: [] };
export function literal(n) {
  if (ts.isStringLiteralLike(n)) return n.text;
  if (ts.isNumericLiteral(n)) return Number(n.text);
  if (n.kind === ts.SyntaxKind.TrueKeyword) return true;
  if (n.kind === ts.SyntaxKind.FalseKeyword) return false;
  if (n.kind === ts.SyntaxKind.NullKeyword) return null;
  if (ts.isArrayLiteralExpression(n)) return n.elements.map(literal);
  if (ts.isObjectLiteralExpression(n)) {
    const result = {};
    for (const p of n.properties) {
      if (!ts.isPropertyAssignment(p) || ts.isComputedPropertyName(p.name)) throw Error('nonliteral property');
      result[p.name.text] = literal(p.initializer);
    }
    return result;
  }
  if (ts.isCallExpression(n) && ts.isIdentifier(n.expression) && n.expression.text === 'stateIn') {
    if (n.arguments.length !== 1) throw Error('stateIn arity');
    const value = literal(n.arguments[0]); validateStateValue(value);
    return { __stateIn: value };
  }
  if (ts.isArrowFunction(n) && n.parameters.length === 0 && [ts.SyntaxKind.TrueKeyword, ts.SyntaxKind.FalseKeyword].includes(n.body.kind)) return { __constantGuard: literal(n.body) };
  throw Error('nonliteral expression ' + n.getText().slice(0, 90));
}
const stateKeys = new Set(['id', 'initial', 'states', 'on', 'always', 'onDone', 'type', 'history', 'target', 'tags', 'meta']);
function validateStateValue(value) {
  if (typeof value === 'string') return;
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw Error('nonliteral state value');
  Object.values(value).forEach(validateStateValue);
}
export function validateConfig(value) {
  for (const key of Object.keys(value)) if (!stateKeys.has(key)) throw Error('unsupported config ' + key);
  if (value.initial !== undefined && typeof value.initial !== 'string') throw Error('nonstring initial');
  for (const child of Object.values(value.states ?? {})) validateConfig(child);
  function transition(t) {
    if (typeof t === 'string') return;
    if (Array.isArray(t)) { t.forEach(transition); return; }
    if (!t || typeof t !== 'object') throw Error('nonobject transition');
    for (const key of Object.keys(t)) if (!['target', 'guard', 'reenter'].includes(key)) throw Error('unsupported transition ' + key);
    if (t.guard !== undefined) {
      const guard = t.guard;
      if (!guard || typeof guard !== 'object' || Object.keys(guard).length !== 1) throw Error('unsupported guard');
      if (Object.hasOwn(guard, '__constantGuard')) {
        if (typeof guard.__constantGuard !== 'boolean') throw Error('nonboolean constant guard');
      } else if (Object.hasOwn(guard, '__stateIn')) validateStateValue(guard.__stateIn);
      else throw Error('unsupported guard');
    }
  }
  Object.values(value.on ?? {}).forEach(transition);
  if (value.always !== undefined) transition(value.always);
  if (value.onDone !== undefined) transition(value.onDone);
}
function generate() {
for (const source of inventory.sourceFiles) {
  const text = readFileSync(resolve(root, pin.sourceDirectory, source.path), 'utf8');
  const ast = ts.createSourceFile(source.path, text, ts.ScriptTarget.Latest, true);
  function visit(n, suites = []) {
    if (ts.isCallExpression(n) && ts.isIdentifier(n.expression) && ['describe', 'it', 'test'].includes(n.expression.text)) {
      const [name, callback] = n.arguments;
      if (!ts.isStringLiteralLike(name) || !callback || !(ts.isArrowFunction(callback) || ts.isFunctionExpression(callback)) || !ts.isBlock(callback.body)) return;
      if (n.expression.text === 'describe') { ts.forEachChild(callback.body, child => visit(child, [...suites, name.text])); return; }
      const id = source.path + '::' + [...suites, name.text].join(' > ');
      const variables = new Set();
      let assertions = 0;
      try {
        function expression(e) {
          if (ts.isIdentifier(e)) {
            if (!variables.has(e.text)) throw Error('external variable ' + e.text);
            return { op: 'variable', name: e.text };
          }
          if (ts.isCallExpression(e)) {
            if (ts.isIdentifier(e.expression) && e.expression.text === 'getInitialSnapshot') {
              if (e.arguments.length < 1 || e.arguments.length > 2 || (e.arguments[1] && e.arguments[1].getText(ast) !== 'undefined')) throw Error('getInitialSnapshot input');
              return { op: 'initialSnapshot', machine: expression(e.arguments[0]) };
            }
            if (ts.isIdentifier(e.expression) && e.expression.text === 'getNextSnapshot') {
              if (e.arguments.length !== 3) throw Error('getNextSnapshot arity');
              return { op: 'nextSnapshot', machine: expression(e.arguments[0]), snapshot: expression(e.arguments[1]), event: expression(e.arguments[2]) };
            }            if (ts.isIdentifier(e.expression) && e.expression.text === 'trackEntries') {
              if (e.arguments.length !== 1) throw Error('trackEntries arity');
              return { op: 'track', machine: expression(e.arguments[0]) };
            }
            if (ts.isIdentifier(e.expression) && variables.has(e.expression.text)) {
              if (e.arguments.length) throw Error('nonzero function arity');
              return { op: 'invoke', function: expression(e.expression) };
            }            if (ts.isIdentifier(e.expression) && e.expression.text === 'createMachine') {
              if (e.arguments.length !== 1) throw Error('machine implementations');
              const config = literal(e.arguments[0]); validateConfig(config);
              return { op: 'machine', config };
            }
            if (ts.isIdentifier(e.expression) && e.expression.text === 'createActor') {
              if (e.arguments.length !== 1) throw Error('actor options');
              return { op: 'actor', machine: expression(e.arguments[0]) };
            }
            if (ts.isPropertyAccessExpression(e.expression)) {
              const method = e.expression.name.text;
              if (!['start', 'send', 'stop', 'getSnapshot', 'matches', 'hasTag', 'can', 'resolveState'].includes(method)) throw Error('unsupported method ' + method);
              return { op: 'call', method, receiver: expression(e.expression.expression), args: e.arguments.map(expression) };
            }
            throw Error('unsupported call ' + e.expression.getText(ast));
          }
          if (ts.isPropertyAccessExpression(e)) {
            if (!['value', 'status', 'context', 'output'].includes(e.name.text)) throw Error('unsupported property ' + e.name.text);
            return { op: 'property', receiver: expression(e.expression), name: e.name.text };
          }
          return { op: 'literal', value: literal(e) };
        }
        const steps = [];
        for (const statement of callback.body.statements) {
          if (ts.isVariableStatement(statement)) {
            for (const declaration of statement.declarationList.declarations) {
              if (!ts.isIdentifier(declaration.name) || !declaration.initializer) throw Error('unsupported declaration');
              const value = expression(declaration.initializer);
              variables.add(declaration.name.text);
              steps.push({ op: 'declare', name: declaration.name.text, value });
            }
          } else if (ts.isExpressionStatement(statement)) {
            const e = statement.expression;
            if (ts.isCallExpression(e) && ts.isPropertyAccessExpression(e.expression) && ts.isCallExpression(e.expression.expression) && e.expression.expression.expression.getText(ast) === 'expect') {
              const matcher = e.expression.name.text;
              if (!['toEqual', 'toBe', 'toBeTruthy', 'toBeFalsy', 'toStrictEqual'].includes(matcher)) throw Error('unsupported assertion ' + matcher);
              if (e.expression.expression.arguments.length !== 1) throw Error('expect arity');
              const unary = ['toBeTruthy', 'toBeFalsy'].includes(matcher);
              if (e.arguments.length !== (unary ? 0 : 1)) throw Error('matcher arity');
              steps.push({ op: 'assert', matcher, actual: expression(e.expression.expression.arguments[0]), expected: unary ? { op: 'literal', value: matcher === 'toBeTruthy' } : expression(e.arguments[0]) });
              assertions++;
            } else steps.push({ op: 'evaluate', value: expression(e) });
          } else throw Error('unsupported statement ' + ts.SyntaxKind[statement.kind]);
        }
        if (!assertions) throw Error('no assertions');
        output.tests.push({ id, file: source.path, line: ast.getLineAndCharacterOfPosition(n.getStart(ast)).line + 1, assertions, steps });
      } catch (err) { output.rejected.push({ id, reason: err.message }); }
      return;
    }
    ts.forEachChild(n, child => visit(child, suites));
  }
  visit(ast);
}
writeFileSync(resolve(here, '../data-tests.json'), JSON.stringify(output, null, 2) + '\n');
console.log(JSON.stringify({ translated: output.tests.length, rejected: output.rejected.length, assertions: output.tests.reduce((n,t) => n+t.assertions,0) }));

}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) generate();
