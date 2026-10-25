import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { parse } from '@vue/compiler-sfc';
import { computed, reactive } from 'vue';

function aperture(view = 'Gallery') {
    const props = reactive({ view, motionEnabled: true, hidden: false, continuous: false });
    const context = vm.createContext({ exports: {}, computed, defineProps: () => props });
    const source = parse(readFileSync(new URL('../app/components/ApertureBackground.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    return { props, read: expression => vm.runInContext(expression, context) };
}
test('only the focus ring progresses slightly clockwise and returns without accumulating rotation', () => {
    const lens = aperture('Sources');
    assert.equal(lens.read('ringRotation.value'), 0);
    let previous = 0;
    for (const view of ['Curation', 'Gallery', 'Delivery']) {
        lens.props.view = view;
        const next = lens.read('ringRotation.value');
        assert.ok(next > previous && next - previous <= 1.5);
        previous = next;
    }
    for (const view of ['Sources', 'Delivery', 'Gallery', 'Gallery']) lens.props.view = view;
    assert.equal(lens.read('ringRotation.value'), 3);
});

test('reduced motion keeps the same ring position across every tab', () => {
    const lens = aperture('Delivery');
    lens.props.motionEnabled = false;
    const position = lens.read('ringRotation.value');
    for (const view of ['Sources', 'Curation', 'Gallery', 'Delivery']) {
        lens.props.view = view;
        assert.equal(lens.read('ringRotation.value'), position);
        assert.equal(lens.read('duration.value'), '0ms');
    }
});

test('ring motion takes at least five seconds and fullscreen suppresses its transition', () => {
    const lens = aperture();
    assert.ok(parseInt(lens.read('duration.value')) >= 5000);
    lens.props.hidden = true;
    assert.equal(lens.read('duration.value'), '0ms');
});

test('sign-in ring pauses when motion stops or the background is hidden and resumes without changing workspace angles', () => {
    const lens = aperture('Sources');
    lens.props.continuous = true;
    assert.equal(lens.read('ringAnimationState.value'), 'running');
    lens.props.motionEnabled = false;
    assert.equal(lens.read('ringAnimationState.value'), 'paused');
    lens.props.motionEnabled = true;
    lens.props.hidden = true;
    assert.equal(lens.read('ringAnimationState.value'), 'paused');
    lens.props.hidden = false;
    assert.equal(lens.read('ringAnimationState.value'), 'running');
    assert.equal(lens.read('ringRotation.value'), 0);
});
