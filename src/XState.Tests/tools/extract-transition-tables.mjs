import ts from '../../../data/library-source/xstate-test-tools/node_modules/typescript/lib/typescript.js';
import {literal,validateConfig} from './translate-data-tests.mjs';
import {readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {createHash} from 'node:crypto';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../..');
const callName=node=>ts.isCallExpression(node)&&ts.isIdentifier(node.expression)?node.expression.text:null;
function value(node,allowUndefined=false){
 if(allowUndefined&&ts.isIdentifier(node)&&node.text==='undefined')return undefined;
 if(ts.isObjectLiteralExpression(node)){
  const result=Object.create(null);
  for(const property of node.properties){
   if(!ts.isPropertyAssignment(property)||ts.isComputedPropertyName(property.name))throw Error('Nonliteral table property');
   const name=property.name.text;
   if(Object.hasOwn(result,name))throw Error('Duplicate property '+name);
   result[name]=value(property.initializer,allowUndefined);
  }
  return result;
 }
 if(ts.isArrayLiteralExpression(node))return node.elements.map(item=>value(item,allowUndefined));
 return literal(node);
}
function stateValue(value){
 if(typeof value==='string')return;
 if(!value||typeof value!=='object'||Array.isArray(value))throw Error('Invalid expected state value');
 for(const child of Object.values(value))stateValue(child);
}
function canonicalCode(expression){
 function shape(node){
  const children=[];ts.forEachChild(node,child=>{children.push(shape(child));});
  return [node.kind,typeof node.text==='string'?node.text:null,
   ts.isVariableDeclarationList(node)?node.flags&ts.NodeFlags.BlockScoped:0,
   typeof node.operator==='number'?node.operator:null,children];
 }
 return JSON.stringify(shape(expression));
}
// Recognize the whole nested loop and testcase body. Extra statements/assertions
// invalidate the translation instead of silently disappearing from the C# case.
function transitionLoop(expression){
 const isCall=(node,name)=>ts.isCallExpression(node)&&node.expression.getText()===name;
 const identifier=node=>node&&ts.isIdentifier(node)?node.text:null;
 const callback=node=>node&&ts.isArrowFunction(node)&&node.parameters.length===1&&ts.isBlock(node.body)?node:null;
 if(!ts.isCallExpression(expression)||!ts.isPropertyAccessExpression(expression.expression)||expression.expression.name.text!=='forEach')return null;
 const keys=expression.expression.expression;
 if(!isCall(keys,'Object.keys')||keys.arguments.length!==1)return null;
 const expected=identifier(keys.arguments[0]);const outer=callback(expression.arguments[0]);
 if(!expected||!outer||outer.body.statements.length!==1)return null;
 const from=identifier(outer.parameters[0].name),innerStatement=outer.body.statements[0];
 if(!from||!ts.isExpressionStatement(innerStatement)||!ts.isCallExpression(innerStatement.expression))return null;
 const inner=callback(innerStatement.expression.arguments[0]);
 if(!inner||inner.body.statements.length!==2)return null;
 const event=identifier(inner.parameters[0].name),toStatement=inner.body.statements[0],itStatement=inner.body.statements[1];
 if(!event||!ts.isVariableStatement(toStatement)||toStatement.declarationList.declarations.length!==1||!ts.isExpressionStatement(itStatement)||!ts.isCallExpression(itStatement.expression))return null;
 const to=identifier(toStatement.declarationList.declarations[0].name),body=itStatement.expression.arguments[1];
 if(!to||!body||!ts.isArrowFunction(body)||!ts.isBlock(body.body)||body.body.statements.length!==2)return null;
 const resultStatement=body.body.statements[0];
 if(!ts.isVariableStatement(resultStatement)||resultStatement.declarationList.declarations.length!==1)return null;
 const declaration=resultStatement.declarationList.declarations[0],result=identifier(declaration.name),next=declaration.initializer;
 if(!result||!next||!isCall(next,'testMultiTransition'))return null;
 const machine=identifier(next.arguments[0]);if(!machine)return null;
 const title='`should go from ${'+from+'} to ${JSON.stringify('+to+')} on ${'+event+'}`';
 const canonical=`Object.keys(${expected}).forEach((${from})=>{Object.keys(${expected}[${from}]).forEach((${event})=>{const ${to}=${expected}[${from}][${event}];it(${title},()=>{const ${result}=testMultiTransition(${machine},${from},${event});expect(${result}.value).toEqual(${to});});});})`;
 const pattern=ts.createSourceFile('pattern.ts',canonical,ts.ScriptTarget.Latest,true).statements[0].expression;
 if(canonicalCode(expression)!==canonicalCode(pattern))return null;
 return {args:[next.arguments[0],keys.arguments[0]]};
}
export function extractTables(text,source){
 const ast=ts.createSourceFile(source,text,ts.ScriptTarget.Latest,true);
 const tables=[],rejected=[];
 function scope(statements,inherited,suites){
  const bindings=new Map(inherited);const candidates=[];let unsafe=null;
  for(const statement of statements){
   if(ts.isImportDeclaration(statement)||ts.isInterfaceDeclaration(statement)||ts.isTypeAliasDeclaration(statement)||ts.isEmptyStatement(statement))continue;
   if(ts.isVariableStatement(statement)){
    for(const declaration of statement.declarationList.declarations){
     if(!ts.isIdentifier(declaration.name)) {unsafe='Destructured declaration';continue;}
     const name=declaration.name.text;
     if(['createMachine','testAll','testMultiTransition','stateIn','undefined','Object','JSON'].includes(name))unsafe='Shadows a required table primitive: '+name;
     try{
      if(!(statement.declarationList.flags&ts.NodeFlags.Const)||!declaration.initializer)throw Error('Mutable or uninitialized binding '+name);
      const initializer=declaration.initializer;
      if(callName(initializer)==='createMachine'){
       if(initializer.arguments.length!==1)throw Error('Machine implementations are unsupported');
       const config=value(initializer.arguments[0]);validateConfig(config);
       bindings.set(name,{kind:'machine',config,expression:initializer.getText(ast)});
      }else bindings.set(name,{kind:'value',value:value(initializer,true),expression:initializer.getText(ast)});
     }catch(error){bindings.set(name,{error:error.message});}
    }
    continue;
   }
   if(ts.isExpressionStatement(statement)){
    const expression=statement.expression;const name=callName(expression);
    if(name==='describe'){
     const [title,callback]=expression.arguments;
     if(!title||!ts.isStringLiteralLike(title)||!callback||!ts.isArrowFunction(callback)||!ts.isBlock(callback.body)){unsafe='Unsupported describe';continue;}
     candidates.push({describe:callback.body.statements,bindings:new Map(bindings),suites:[...suites,title.text]});continue;
    }
    // These callbacks are registered independently and are not table declarations.
    if(name==='it'||name==='test')continue;
    const loop=transitionLoop(expression);
    if(!loop&&ts.isCallExpression(expression)&&expression.getText(ast).startsWith('Object.keys(')&&expression.getText(ast).includes('testMultiTransition('))
     rejected.push({source,line:ast.getLineAndCharacterOfPosition(expression.getStart(ast)).line+1,reason:'Unsupported transition table loop'});
    if(name==='testAll'||loop){
     const line=ast.getLineAndCharacterOfPosition(expression.getStart(ast)).line+1;
     try{
      const args=loop?.args??expression.arguments;
      if(args.length!==2||!args.every(ts.isIdentifier))throw Error('testAll requires two bindings');
      const [machineName,expectedName]=args.map(arg=>arg.text);
      const machine=bindings.get(machineName),expected=bindings.get(expectedName);
      if(machine?.error)throw Error(machine.error);
      if(expected?.error)throw Error(expected.error);
      if(machine?.kind!=='machine'||expected?.kind!=='value')throw Error('Unresolved table bindings');
      const table=expected.value;
      if(!table||typeof table!=='object'||Array.isArray(table))throw Error('Invalid table');
      const tests=[];
      for(const [from,events] of Object.entries(table)){
       if(!events||typeof events!=='object'||Array.isArray(events))throw Error('Invalid event table');
       if(from.startsWith('{'))stateValue(JSON.parse(from));
       for(const [eventTypes,to] of Object.entries(events)){
        if(to!==undefined)stateValue(to);
        else if(loop)throw Error('Undefined equality requires a distinct assertion model');
        const title=`should go from ${from} to ${JSON.stringify(to)} on ${eventTypes}`;
        tests.push({id:source+'::'+[...suites,title].join(' > '),from,eventTypes,events:eventTypes.split(/,\s?/),kind:loop?'equals':to===undefined?'unchanged':typeof to==='string'?'matches':'equals',...(to===undefined?{}:{expected:to})});
       }
      }
      if(!tests.length)throw Error('Empty table');
      candidates.push({table:{source,line,key:source+':'+line,suites,mode:loop?'equals':'helper',config:machine.config,expression:machine.expression,expectedExpression:expected.expression,tests}});
     }catch(error){rejected.push({source,line,reason:error.message});}
     continue;
    }
   }
   unsafe='Unsupported statement: '+statement.getText(ast).slice(0,100);
  }
  for(const candidate of candidates){
   if(unsafe){
    if(candidate.table)rejected.push({source,line:candidate.table.line,reason:unsafe});
    else scope(candidate.describe,new Map([...candidate.bindings].map(([name])=>[name,{error:unsafe}])),candidate.suites);
   }else if(candidate.table)tables.push(candidate.table);
   else scope(candidate.describe,candidate.bindings,candidate.suites);
  }
 }
 scope(ast.statements,new Map(),[]);
 return {tables,rejected};
}
function generate(){
 const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
 const inventory=JSON.parse(readFileSync(resolve(root,'src/XState.Tests/upstream-inventory.json'),'utf8'));
 const tables=[],rejected=[],sources=[];
 for(const file of inventory.sourceFiles){
  const text=readFileSync(resolve(root,pin.sourceDirectory,file.path),'utf8');
  if(!/\b(?:testAll|testMultiTransition)\s*\(/.test(text)||file.path.endsWith('/utils.ts'))continue;
  const result=extractTables(text,file.path);tables.push(...result.tables);rejected.push(...result.rejected);
  sources.push({source:file.path,sha256:createHash('sha256').update(text).digest('hex')});
 }
 const ids=tables.flatMap(table=>table.tests.map(test=>test.id));
 if(new Set(ids).size!==ids.length)throw Error('Duplicate generated test ID');
 const fixture={commit:pin.commit,sources,tables:tables.map(({expression,expectedExpression,...table})=>table),rejected};
 writeFileSync(resolve(root,'src/XState.Tests/fixtures/upstream-transition-tables.json'),JSON.stringify(fixture,null,2)+'\n');
 const imports=pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href;
 const module=`import {createMachine,stateIn} from ${JSON.stringify(imports)};\nexport const machines={\n`+tables.map(table=>JSON.stringify(table.key)+':'+table.expression).join(',\n')+'\n};\nexport const expectedTables={\n'+tables.map(table=>JSON.stringify(table.key)+':'+table.expectedExpression).join(',\n')+'\n};\n';
 mkdirSync(resolve(root,'tmp/xstate-parity'),{recursive:true});writeFileSync(resolve(root,'tmp/xstate-parity/transition-table-machines.mts'),module);
 console.log(JSON.stringify({tables:tables.length,tests:ids.length,rejected}));
}
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url))generate();
