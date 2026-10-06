import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,assign,fromCallback}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const {default:Ajv}=await import(pathToFileURL(resolve(root,'data/library-source/xstate-test-tools/node_modules/ajv/dist/ajv.js')).href);
const validate=new Ajv().compile(JSON.parse(readFileSync(resolve(root,pin.sourceDirectory,'packages/core/src/machine.schema.json'),'utf8')));
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-json-definition.json'),'utf8'));
it('serializes the original json.test.ts machine and validates with its schema',()=>{
 const machine=createMachine({initial:'foo',version:'1.0.0',context:{number:0,string:'hello'},invoke:[{id:'invokeId',src:'invokeSrc'}],states:{
 testActions:{invoke:[{id:'invokeId',src:'invokeSrc'}],entry:['stringActionType',{type:'objectActionType'},{type:'objectActionTypeWithExec',exec:()=>true,other:'any'},function actionFunction(){return true;},assign({number:10,string:'test',evalNumber:()=>42}),assign(ctx=>({...ctx}))],on:{TO_FOO:{target:['foo','bar'],guard:({context})=>!!context.string}},after:{1000:'bar'}},
 foo:{},bar:{},testHistory:{type:'history',history:'deep'},testFinal:{type:'final',output:{something:'else'}},testParallel:{type:'parallel',states:{one:{initial:'inactive',states:{inactive:{}}},two:{initial:'inactive',states:{inactive:{}}}}}},output:{result:42}});
 const json=JSON.parse(JSON.stringify(machine.definition));expect(validate(json)).toBe(true);expect(validate.errors).toBeNull();expect(json).toEqual(native.machine);expect(validate(native.machine)).toBe(native.valid);
});
it('rejects the original invalid machine',()=>{expect(validate({id:'something',key:'something',type:'invalid type',states:{}})).toBe(false);expect(validate.errors).not.toBeNull();expect(native.invalid).toBe(false);});
it('retains live action properties and ignores exec while resolving the registry implementation',()=>{
 const calls=[];let ignoredExec=0;const properties={type:'first',params:null,other:'any',exec:()=>ignoredExec++};
 const machine=createMachine({context:7,entry:[properties],on:{GO:{actions:[properties]}}},{actions:{first:(_,params)=>calls.push(`first:${params===undefined?'False':'True'}:${params??'null'}`),second:(_,params)=>calls.push(`second:${params===undefined?'False':'True'}:${params??'null'}`)}});
 const definition=machine.definition.entry[0];expect(definition).toBe(properties);const actor=createActor(machine).start();try{
 properties.type='second';properties.params=({context,event})=>context+event.type.length;expect(definition.type).toBe('second');expect(definition.params).toBe(properties.params);actor.send({type:'GO'});
 expect(JSON.parse(JSON.stringify(machine))).toEqual(native.actionPropertiesJson);delete properties.params;expect(Object.hasOwn(definition,'params')).toBe(false);actor.send({type:'GO'});expect(calls).toEqual(native.actionPropertiesCalls);expect(ignoredExec).toBe(0);expect(ignoredExec).toBe(native.ignoredExec);
 }finally{actor.stop();}
});

it('agrees on nested schema references and additional properties',()=>{
 const results={};for(const name of ['missingId','nestedOrder','historyKind','actionType','extraProperty']){const node=structuredClone(native.machine);switch(name){case 'missingId':delete node.id;break;case 'nestedOrder':node.states.foo.order='wrong';break;case 'historyKind':node.states.testHistory.history='wrong';break;case 'actionType':delete node.states.testActions.entry[2].type;break;case 'extraProperty':node.extra={nested:42};break;}results[name]=validate(node);expect(results[name]).toBe(name==='extraProperty');}expect(results).toEqual(native.schemaControls);
});

it('round trips invoke transitions without duplication with all original snapshot fields',()=>{
 const machine=createMachine({initial:'active',states:{active:{id:'active',invoke:{src:'someSrc',onDone:'foo',onError:'bar'},on:{EVENT:'foo'}},foo:{},bar:{}}});
 const json=JSON.stringify(machine),revived=createMachine(JSON.parse(json));const transitions=[...revived.states.active.transitions.values()].flat();
 expect(transitions).toHaveLength(3);expect([...revived.getStateNodeById('active').transitions.values()].flat()).toHaveLength(3);
 expect(transitions.map(t=>({eventType:t.eventType,reenter:t.reenter,source:'#'+t.source.id,target:t.target.map(node=>'#'+node.id),actions:t.actions,guard:t.guard??null}))).toEqual(native.roundTripTransitions);
 expect(transitions.every(t=>typeof t.toJSON==='function')).toBe(true);expect(JSON.parse(JSON.stringify(transitions))).toEqual(native.roundTripTransitionJson);expect(JSON.parse(json)).toEqual(native.roundTripJson);
 const actor=createActor(revived);let error;actor.subscribe({error:value=>error=value});actor.start();try{expect(actor.getSnapshot().status).toBe('error');expect(error.message).toEqual(native.roundTripInitialError);}finally{actor.stop();}
});
it('parses executable config, literal invoke input and output, and preserves null action failure',()=>{
 const config=JSON.parse('{"id":"parsed","initial":"a","invoke":{"src":"reader","input":{"count":42}},"states":{"a":{"on":{"GO":{"target":"b","actions":{"type":"record","params":null}}}},"b":{"type":"final","output":7}},"output":null}');
 let input,actions=0;const machine=createMachine(config,{actors:{reader:fromCallback(args=>{input=args.input;})},actions:{record:(_,params)=>{expect(params).toBe(null);actions++;}}});const actor=createActor(machine).start();try{expect(input).toBe(config.invoke.input);actor.send({type:'GO'});expect(actor.getSnapshot().status).toBe('done');expect(actor.getSnapshot().output).toBe(null);expect(actions).toBe(1);expect(JSON.parse(JSON.stringify(machine))).toEqual(native.parsedConfigJson);expect({input,actions,status:actor.getSnapshot().status,output:actor.getSnapshot().output}).toEqual(native.parsedBehavior);}finally{actor.stop();}
 const broken=createMachine(JSON.parse('{"entry":[null]}'));expect(broken.definition.entry).toEqual([null]);expect(JSON.parse(JSON.stringify(broken))).toEqual(native.nullActionJson);const failed=createActor(broken);let error;failed.subscribe({error:value=>error=value});failed.start();try{expect(failed.getSnapshot().status).toBe('error');expect(error.message).toEqual(native.nullActionError);}finally{failed.stop();}
 const nullInput=createMachine(JSON.parse('{"invoke":{"src":"reader","input":null}}'));expect(JSON.parse(JSON.stringify(nullInput))).toEqual(native.nullInputJson);
});
