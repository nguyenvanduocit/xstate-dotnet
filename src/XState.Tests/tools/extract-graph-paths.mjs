import ts from '../../../data/library-source/xstate-test-tools/node_modules/typescript/lib/typescript.js';
import {literal} from './translate-data-tests.mjs';
import {readFileSync,writeFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../..');
export function readSnapshots(source) {
 const ast=ts.createSourceFile('snapshots.js',source,ts.ScriptTarget.Latest,true);
 if(ast.parseDiagnostics.length) throw Error('Invalid snapshot module');
 const result={};
 for(const statement of ast.statements){
  if(!ts.isExpressionStatement(statement)||!ts.isBinaryExpression(statement.expression)||statement.expression.operatorToken.kind!==ts.SyntaxKind.EqualsToken)throw Error('Unsupported snapshot statement');
  const {left,right}=statement.expression;
  if(!ts.isElementAccessExpression(left)||!ts.isIdentifier(left.expression)||left.expression.text!=='exports'||!ts.isStringLiteralLike(left.argumentExpression)||!ts.isNoSubstitutionTemplateLiteral(right))throw Error('Unsupported snapshot assignment');
  const parsed=ts.createSourceFile('snapshot.ts','const value = '+right.text+';',ts.ScriptTarget.Latest,true);
  if(parsed.parseDiagnostics.length||parsed.statements.length!==1||!ts.isVariableStatement(parsed.statements[0]))throw Error('Invalid literal snapshot');
  const declarations=parsed.statements[0].declarationList.declarations;
  if(declarations.length!==1||!declarations[0].initializer)throw Error('Invalid snapshot declaration');
  const key=left.argumentExpression.text;
  if(Object.hasOwn(result,key))throw Error('Duplicate snapshot key');
  result[key]=literal(declarations[0].initializer);
 }
 return result;
}
function generate(){
 const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
 const source='packages/core/src/graph/test/__snapshots__/graph.test.ts.snap';
 const bytes=readFileSync(resolve(root,pin.sourceDirectory,source));
 const snapshots=Object.fromEntries(Object.entries(readSnapshots(bytes.toString('utf8'))).filter(([key])=>!key.includes('toDirectedGraph')));
 const report={commit:pin.commit,source,sourceSha256:createHash('sha256').update(bytes).digest('hex'),snapshots};
 writeFileSync(resolve(root,'src/XState.Tests/fixtures/upstream-graph-paths.json'),JSON.stringify(report,null,2)+'\n');
 console.log(JSON.stringify({snapshots:Object.keys(snapshots).length}));
}
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url))generate();
