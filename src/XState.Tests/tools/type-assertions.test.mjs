import assert from 'node:assert/strict';
import {test} from 'node:test';
import {requiresCompilerEvidence} from './type-assertions.mjs';
for(const file of ['packages/core/test/types.test.ts','packages/core/src/graph/types.test.ts','packages/core/test/setup.types.test.ts','packages/core/test/spawn.types.test.ts','packages/core/test/typeHelpers.test.ts']){
 test(`requires positive compiler evidence for ${file}`,()=>assert.equal(requiresCompilerEvidence(file,'it("positive", () => createMachine({}));'),true));
}
test('recognizes negative and explicit type assertions in ordinary runtime suites',()=>{
 for(const source of ['// @ts-expect-error\nactor.send({type:"OTHER"});','expectTypeOf(value).toEqualTypeOf<string>();','const value = 1 satisfies number;'])assert.equal(requiresCompilerEvidence('packages/core/test/runtime.test.ts',source),true);
});
test('does not classify an ordinary runtime test or a similarly named file as a dedicated type test',()=>{
 for(const file of ['packages/core/test/runtime.test.ts','packages/core/test/eventtypes.test.ts','packages/core/test/types/runtime.test.ts'])assert.equal(requiresCompilerEvidence(file,'expect(actor.getSnapshot().value).toBe("a");'),false);
});
test('recognizes dedicated files with Windows separators',()=>assert.equal(requiresCompilerEvidence('packages\\core\\test\\setup.types.test.ts',''),true));

test('requires positive compiler evidence in explicit type safety suites within runtime files',()=>assert.equal(requiresCompilerEvidence('packages/core/test/mapState.test.ts','mapState(snapshot, mapper);',['mapState','type safety']),true));
