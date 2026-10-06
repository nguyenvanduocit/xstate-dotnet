import { requiresCompilerEvidence } from './type-assertions.mjs';
import ts from '../../../data/library-source/xstate-test-tools/node_modules/typescript/lib/typescript.js';
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { resolve, relative, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const sourceRoot = resolve(root, pin.sourceDirectory);
const core = resolve(sourceRoot, pin.package);
const files = [];
function walk(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (['node_modules', 'dist'].includes(entry.name)) continue;
    const path = resolve(dir, entry.name);
    if (entry.isDirectory()) walk(path);
    else if (/\.(test|spec)\.[cm]?[jt]sx?$/.test(entry.name)) files.push(path);
  }
}
walk(core);
const tests = [];
const sources = [];
for (const file of files.sort()) {
  const text = readFileSync(file, 'utf8');
  const path = relative(sourceRoot, file).replaceAll('\\', '/');
  sources.push({ path, sha256: createHash('sha256').update(text).digest('hex') });
  const ast = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
  function visit(node, suites = [], inheritedMode = 'run') {
    if (ts.isCallExpression(node)) {
      const callee = node.expression.getText(ast);
      const isSuite = /^(describe|suite)(\.|$)/.test(callee);
      const isTest = /^(it|test)(\.|$)/.test(callee);
      const callback = node.arguments.find(a => ts.isArrowFunction(a) || ts.isFunctionExpression(a));
      const titleNode = node.arguments[0];
      if ((isTest || isSuite) && titleNode && (callback || /\.(todo|skip)/.test(callee))) {
        const literal = ts.isStringLiteralLike(titleNode);
        const title = literal ? titleNode.text : titleNode.getText(ast);
        const line = ast.getLineAndCharacterOfPosition(node.getStart(ast)).line + 1;
        const mode = /\.skip\b/.test(callee) ? 'skip' : /\.todo\b/.test(callee) ? 'todo' : /\.only\b/.test(callee) ? 'only' : inheritedMode;
        if (isTest) tests.push({ id: `${path}:${line}`, file: path, line, suites, title, declaration: callee, mode, dynamic: !literal || /\.each\b/.test(callee), typeAssertions: requiresCompilerEvidence(path, node.getText(ast), suites), portStatus: 'pending' });
        if (isSuite && callback) { visit(callback.body, [...suites, title], mode); return; }
        if (isTest) return;
      }
    }
    ts.forEachChild(node, child => visit(child, suites, inheritedMode));
  }
  visit(ast);
}
const output = { version: pin.version, commit: pin.commit, kind: 'static-declarations-not-expanded-runtime-cases', sourceFiles: sources, counts: { files: files.length, declarations: tests.length, dynamicDeclarations: tests.filter(t => t.dynamic).length, declarationsWithTypeAssertions: tests.filter(t => t.typeAssertions).length }, tests };
writeFileSync(resolve(here, '../upstream-inventory.json'), JSON.stringify(output, null, 2) + '\n');
console.log(JSON.stringify(output.counts));
