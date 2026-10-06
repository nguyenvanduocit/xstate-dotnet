import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const upstream = part => pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src',part)).href;
const {createMachine,createActor,fromTransition,fromCallback,assign} = await import(upstream('index.ts'));
const {getAdjacencyMap,getShortestPaths,serializeSnapshot} = await import(upstream('graph/index.ts'));
const native = JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-graph-traversal.json'),'utf8'));
const cycle = () => createMachine({initial:'a',states:{a:{on:{toB:'b'}},b:{on:{toC:'c'}},c:{on:{toA:'a'}}}});
const paths = rows => rows.map(p => ({state:JSON.stringify(p.state.value),weight:p.weight,steps:p.steps.map(s => ({state:JSON.stringify(s.state.value),event:s.event.type}))}));
it('matches native adjacency keys, event order and target snapshots', () => {
 const map=getAdjacencyMap(cycle(),{});
 expect(native.adjacency).toEqual(Object.entries(map).map(([key,value]) => ({key,state:JSON.stringify(value.state.value),transitions:Object.entries(value.transitions).map(([key,t])=>({key,event:t.event.type,state:JSON.stringify(t.state.value)}))})));
});
it('matches native shortest paths on a cycle',()=>expect(native.shortest).toEqual(paths(getShortestPaths(cycle()))));
it('matches native shortest paths with event and previous-state serialization',()=>{
 expect(native.previous).toEqual(paths(getShortestPaths(cycle(),{serializeState:(state,event,previous)=>`${JSON.stringify(state.value)} via ${event?.type}${previous?' via '+JSON.stringify(previous.value):''}`})));
});
it('matches native generated delay paths without running timers',()=>{
 const machine=createMachine({initial:'a',states:{a:{after:{1000:'b'}},b:{}}});
 expect(native.delayed).toEqual(paths(getShortestPaths(machine)));
});
it('uses an unstarted empty actor as graph self and ignores defer emit stopChild',()=>{
 let deferred=0,emitted=0;const seen=[]; const unrelated=createActor(fromTransition(s=>s,0)).start();
 const logic=fromTransition((context,event,scope)=>{
  seen.push(scope.self); expect(scope.self.getSnapshot()).toEqual({status:'active',output:undefined,error:undefined,context:undefined});
  expect(scope.id).toBe(''); expect(scope.sessionId).toMatch(/^[0-9a-v]*$/);expect(scope.sessionId).not.toBe(scope.self.sessionId);expect(scope.system).toBe(scope.self.system);
  expect(scope.logger).toBe(console.log);
  const before=scope.self.getSnapshot(); const sub=scope.self.on('EMIT',()=>emitted++);
  scope.emit({type:'EMIT'});scope.defer(()=>deferred++);scope.stopChild(unrelated);scope.self.send({type:'QUEUED'});expect(scope.self.getSnapshot()).toBe(before);sub.unsubscribe();
  return context+1;
 },0);
 const map=getAdjacencyMap(logic,{events:[{type:'INC'}],stopWhen:s=>s.context===2});
 expect(Object.keys(map)).toHaveLength(3);expect(deferred).toBe(0);expect(emitted).toBe(0);expect(seen[0]).toBe(seen[1]);unrelated.stop();
});
it('initializes machine defaults twice while keeping assigns and discarding effects/invoke starts',()=>{
 let initialized=0,effects=0,starts=0;const selves=[];
 const machine=createMachine({context:({self})=>{initialized++;selves.push(self);expect(self.getSnapshot().context).toBeUndefined();return {count:0};},entry:[assign({count:({context})=>context.count+1}),()=>effects++],invoke:{src:fromCallback(()=>{starts++;}),id:'worker'}});
 const result=getShortestPaths(machine);expect(initialized).toBe(2);expect(effects).toBe(0);expect(starts).toBe(0);expect(result).toHaveLength(1);expect(result[0].state.context.count).toBe(1);expect(selves[0]).not.toBe(selves[1]);expect(result[0].state.children.worker._parent).toBe(selves[0]);
});
it('counts duplicate queued states against the exact traversal limit',()=>{
 const logic=fromTransition(s=>s,0);
 expect(()=>getAdjacencyMap(logic,{limit:-1})).toThrow('Traversal limit exceeded');
 expect(()=>getAdjacencyMap(logic,{events:[{type:'SAME'}],limit:0})).toThrow('Traversal limit exceeded');
 expect(Object.keys(getAdjacencyMap(logic,{events:[{type:'SAME'}],limit:1}))).toHaveLength(1);
});
it('queues colliding events separately and keeps the last edge',()=>{
 let calls=0;const logic=fromTransition((_,event)=>{calls++;return event.payload;},0);
 const map=getAdjacencyMap(logic,{events:[{type:'A',payload:1},{type:'B',payload:2},{type:'DROP',payload:3}],filterEvents:(_,e)=>e.type!=='DROP',stopWhen:s=>s.context!==0,serializeEvent:()=> 'same'});
 expect(calls).toBe(2);expect(Object.keys(map)).toHaveLength(3);expect(Object.values(map)[0].transitions.same.state.context).toBe(2);
});
it('enumerates numeric state/event keys and permits explicit null stopWhen',()=>{
 const logic=fromTransition((_,e)=>e.payload,0);const map=getAdjacencyMap(logic,{events:[{type:'2',payload:2},{type:'1',payload:1}],serializeState:s=>String(s.context),serializeEvent:e=>e.type,stopWhen:s=>s.context!==0});
 expect(Object.keys(map)).toEqual(['0','1','2']);expect(Object.keys(map['0'].transitions)).toEqual(['1','2']);
 const machine=createMachine({initial:'a',states:{a:{on:{NEXT:'b'}},b:{}}});
 expect(Object.keys(getAdjacencyMap(machine,{toState:s=>s.matches('a'),stopWhen:null}))).toHaveLength(2);
});
it('omits empty context from machine serialization',()=>{
 expect(serializeSnapshot(createMachine({initial:'a',states:{a:{}}}).resolveState({value:'a'}))).toBe('{"value":"a"}');
 expect(serializeSnapshot(createMachine({context:{count:0}}).resolveState({value:{},context:{count:0}}))).toBe('{"value":{},"context":{"count":0}}');
});
it('actor reference observes reducer stop snapshots and exposes emitted events',()=>{
 const seen=[];let complete=0;const actor=createActor(fromTransition((context,event)=>event.type==='INC'?context+1:context,0));
 actor.subscribe({next:s=>seen.push(s.context),complete:()=>complete++});actor.start();actor.send({type:'INC'});actor.stop();
 expect(seen).toEqual([0,1,1]);expect(complete).toBe(1);
 let emitted=0;const callback=createActor(fromCallback(({emit})=>{emit({type:'READY'});}));callback.on('READY',()=>emitted++);callback.start();callback.stop();expect(emitted).toBe(1);
});
