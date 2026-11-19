import { mountSuspended } from '@nuxt/test-utils/runtime';
import { flushPromises } from '@vue/test-utils';
import { afterEach, beforeEach, expect, onTestFinished, test, vi } from 'vitest';
import SourceWorkspace from '../../app/components/SourceWorkspace.vue';
import { clearPrivateAccess } from '../../app/lib/session';
import type { SourceConfigurationResponse } from '../../app/api/models';

const requests = vi.hoisted(() => ({
    capabilities: vi.fn(), root: vi.fn(), sources: vi.fn(), create: vi.fn(), saved: vi.fn(), save: vi.fn(),
    replace: vi.fn(), display: vi.fn(), recent: vi.fn(), authentication: vi.fn(), phase: vi.fn(),
}));
vi.mock('../../app/lib/api', async importOriginal => ({
    ...await importOriginal<typeof import('../../app/lib/api')>(),
    api: { management: {
        sourceCapabilities: { get: requests.capabilities, byProfile: (profile: string) => ({ caRoot: { get: (options: unknown) => requests.root(profile, options) } }) },
        sources: { get: requests.sources, post: requests.create, byId: (id: string) => ({
            display: { get: () => requests.display(id) }, get: (options: unknown) => requests.saved(id, options),
            put: (body: unknown) => requests.save(id, body), regenerateSecret: { post: (body: unknown) => requests.replace(id, body) },
        }) },
        captures: { get: requests.recent },
    } },
    readSourceAuthenticationObservations: requests.authentication, readSourceActivity: requests.phase,
}));
const profiles = ['ModernFtps', 'LegacyFtps', 'PlainFtpLan'].map(profile => ({
    profile, enabled: true, hostname: 'ingest.example.test', controlPort: 2121, caRootAvailable: true,
}));
const passwordPolicy = { minimumLength: 12, maximumLength: 128, generatedLength: 20 };
const setup = { profiles, passwordPolicy };
const camera = (id: string, name = id.toUpperCase(), configurationRevision = 1): SourceConfigurationResponse =>
    ({ id, name, configurationRevision, curationMode: 'Manual', connectionProfile: 'ModernFtps', username: 'camera_' + id });
beforeEach(() => {
    vi.resetAllMocks();
    requests.capabilities.mockResolvedValue(setup);
    requests.display.mockResolvedValue(undefined);
    requests.recent.mockResolvedValue({ items: [] });
    requests.authentication.mockResolvedValue({ coverageComplete: true, items: [] });
    requests.phase.mockResolvedValue({ epoch: 'one', coverageComplete: true, items: [] });
});
afterEach(() => vi.restoreAllMocks());

async function mountSource(sources: SourceConfigurationResponse[] = []) {
    const container = document.body.appendChild(document.createElement('div'));
    const view = await mountSuspended(SourceWorkspace, {
        route: '', attachTo: container, props: { sources, connected: true, recoverySequence: 0, motionEnabled: true },
        global: { stubs: { CollectionPicker: true, AuthorizedPreview: true, UIcon: true } },
    });
    onTestFinished(() => { view.unmount(); container.remove(); });
    await flushPromises();
    const button = (label: string) => {
        const control = view.findAll('button').find(button => button.text().trim() === label);
        if (!control) throw new Error('Missing button: ' + label);
        return control;
    };
    const click = async (label: string) => { await button(label).trigger('click'); await flushPromises(); };
    const name = () => view.get<HTMLInputElement>('input[maxlength="200"]');
    const profile = (value: string) => view.get<HTMLInputElement>(`input[name="source-profile"][value="${value}"]`);
    const choose = async (id: number) => { await view.findAll('.source-summary')[id]!.trigger('click'); await flushPromises(); };
    const submit = async () => { await view.get('form').trigger('submit'); await flushPromises(); };
    const custom = async (password: string) => {
        await view.get('#source-password-mode').setValue('Custom');
        await view.get('#source-custom-password').setValue(password);
    };
    const replace = async () => { await click('Replace credentials...'); await click('Confirm replacement'); };
    return { view, button, click, name, profile, choose, submit, custom, replace };
}

test('cached Source labels cannot replace editor values or a newer authoritative revision', async () => {
    requests.display.mockResolvedValue({ id: 'a', name: 'Cached label', configurationRevision: 1, curationMode: 'Immediate' });
    const { view, choose, name } = await mountSource([camera('a')]);
    await choose(0);
    expect(view.get('.source-summary').attributes('aria-label')).toContain('Cached label');
    expect(name().element.value).toBe('A');
    expect(view.get<HTMLSelectElement>('#source-panel-0 select').element.value).toBe('Manual');
    await view.setProps({ sources: [camera('a', 'New authority', 2)] });
    await flushPromises();
    expect(view.get('.source-summary').attributes('aria-label')).toContain('New authority');
    expect(name().element.value).toBe('New authority');
});

test('Sources outside the bounded initial history read offer an explicit load action', async () => {
    const sources = Array.from({ length: 31 }, (_, i) => camera(String(i), 'Source ' + i));
    const { view } = await mountSource(sources);
    expect(requests.recent).toHaveBeenCalledTimes(30);
    await view.get('[aria-label="Load recent Captures for Source 30"]').trigger('click');
    await flushPromises();
    expect(requests.recent).toHaveBeenCalledTimes(31);
    expect(requests.recent.mock.lastCall?.[0].queryParameters.sourceId).toBe('30');
    expect(view.get('#source-status-30').text()).toContain('No recent Captures.');
});

test('Source switching and New Source retain drafts until explicit discard', async () => {
    for (const target of ['B', 'New Source']) {
        const { view, choose, name, click } = await mountSource([camera('a'), camera('b')]);
        await choose(0);
        await name().setValue('Unsaved A');
        const change = () => target === 'B' ? choose(1) : click('New Source');
        await change();
        expect(view.get('dialog[aria-labelledby="source-discard-title"]').attributes('open')).toBeDefined();
        expect(name().element.value).toBe('Unsaved A');
        await click('Keep editing');
        expect(view.emitted('dirty-change')?.at(-1)).toEqual([true]);
        await change();
        await click('Discard changes');
        expect(name().element.value).toBe(target === 'B' ? 'B' : '');
        expect(view.emitted('dirty-change')?.at(-1)).toEqual([false]);
    }
});

test('Source stream read failures preserve last-known photos and recover on Retry', async () => {
    requests.recent.mockImplementation(async ({ queryParameters }) => ({ items: queryParameters.sourceId === 'a' ? [{ id: 'photo', displayName: 'SYNTHETIC.JPG' }] : [] }));
    const { view } = await mountSource([camera('a'), camera('b')]);
    expect(view.findAll('authorized-preview-stub')).toHaveLength(1);
    requests.recent.mockRejectedValue(new Error('unavailable'));
    await view.setProps({ recoverySequence: 1 });
    await flushPromises();
    expect(view.get('#source-status-a').text()).toContain('Last known photographs');
    expect(view.get('#source-status-b').text()).toContain('Recent Captures unavailable');
    expect(view.findAll('authorized-preview-stub')).toHaveLength(1);
    const pending = Promise.withResolvers<unknown>();
    requests.recent.mockReturnValueOnce(pending.promise);
    await view.get('[aria-label="Retry recent Captures for A"]').trigger('click');
    expect(view.get('#source-status-a').text()).toContain('Updating recent Captures');
    pending.resolve({ items: [] });
    await flushPromises();
    expect(view.get('#source-status-a').text()).toContain('No recent Captures');
    expect(view.findAll('authorized-preview-stub')).toHaveLength(0);
});

test('authentication snapshot races retain newer persisted outcomes and distinguish no record from unavailable history', async () => {
    const first = Promise.withResolvers<unknown>();
    requests.authentication.mockReturnValueOnce(first.promise);
    const { view } = await mountSource([camera('a'), camera('b')]);
    await view.setProps({ authActivity: { sourceId: 'a', outcome: 'Rejected', version: 4, observedAt: '2026-10-13T12:00:02Z' } });
    first.resolve({ coverageComplete: true, items: [{ sourceId: 'a', outcome: 'Succeeded', version: 3, observedAt: '2026-10-13T12:00:01Z' }] });
    await flushPromises();
    expect(view.get('#auth-a').text()).toContain('was rejected');
    expect(view.get('#auth-b').text()).toContain('No saved authentication outcome');
    requests.authentication.mockResolvedValueOnce({ coverageComplete: false, items: [] });
    await view.setProps({ recoverySequence: 1 });
    await flushPromises();
    expect(view.get('#auth-a').text()).toMatch(/was rejected.*coverage is incomplete/);
    requests.authentication.mockRejectedValueOnce(new Error('unavailable'));
    await view.setProps({ recoverySequence: 2 });
    await flushPromises();
    expect(view.get('#auth-a').text()).toContain('may be old');
    expect(view.get('#auth-b').text()).toContain('unavailable');
});

test('a missing older transfer mapping recovers through a bounded explicit identity snapshot', async () => {
    const { view } = await mountSource([camera('a')]);
    const prior = { transferId: 'old', sourceId: 'a', phase: 'Receiving' as const, observedAt: new Date().toISOString(), accepted: false, previewReady: false, version: 1 };
    await view.setProps({ phaseActivity: prior });
    requests.phase.mockClear().mockImplementation(async (_ids, _signal, transfers) => ({
        epoch: 'one', coverageComplete: !transfers,
        items: transfers ? [{ ...prior, captureId: 'known', assetId: 'asset', accepted: true, phase: 'AcceptedAwaitingGrouping', version: 2 }] : [],
    }));
    await view.setProps({ recoverySequence: 1 });
    await flushPromises();
    expect(requests.phase).toHaveBeenCalledTimes(2);
    expect(requests.phase.mock.calls[1]?.[2]).toEqual(['old']);
    expect(view.get('#source-status-a').text()).toContain('Partial activity coverage');
    expect(view.get('.source-counters').text()).toMatch(/1Received/);
});

test('Source profile security choices announce consequences and summary exposes the assigned profile', async () => {
    const { view, profile } = await mountSource([camera('a', 'Shared upload')]);
    expect(view.get('.source-summary').attributes('aria-label')).toMatch(/Shared upload.*Camera \/ FTP upload.*FTPS.*recommended/);
    for (const [value, consequence] of [['PlainFtpLan', /credentials and photographs travel unencrypted/i], ['LegacyFtps', /lacks forward secrecy.*server-key compromise/s]] as const) {
        expect(view.get('#' + profile(value).attributes('aria-describedby')).text()).toMatch(consequence);
    }
});

test('Source settings tabs retain profile warnings and support keyboard navigation', async () => {
    const { view, click, profile } = await mountSource();
    await click('Security');
    await profile('LegacyFtps').setValue();
    expect(view.get('#source-legacy-warning').isVisible()).toBe(true);
    await view.get('#source-tab-0').trigger('keydown', { key: 'ArrowRight' });
    expect(view.get('#source-tab-1').attributes('aria-selected')).toBe('true');
    expect(profile('LegacyFtps').element.checked).toBe(true);
    await view.get('#source-tab-1').trigger('keydown', { key: 'End' });
    expect(view.get('#source-tab-2').attributes('aria-selected')).toBe('true');
    await profile('PlainFtpLan').setValue();
    expect(view.get('#source-plain-warning').isVisible()).toBe(true);
    expect(view.get('#source-plain-warning').text()).toMatch(/Credentials and photographs travel unencrypted.*trusted local network/s);
});

test('Source profile withdrawal preserves saved choice and dirty draft while preventing a new assignment', async () => {
    requests.capabilities.mockResolvedValue({ ...setup, profiles: profiles.map(p => ({ ...p, enabled: p.profile === 'ModernFtps' })) });
    const { view, choose, name, profile, click } = await mountSource([{ ...camera('a'), connectionProfile: 'LegacyFtps' }]);
    await choose(0);
    await name().setValue('Retained');
    await click('Security');
    expect(profile('LegacyFtps').element.checked).toBe(true);
    expect(profile('PlainFtpLan').element.disabled).toBe(true);
    expect(view.get<HTMLButtonElement>('button[type="submit"]').element.disabled).toBe(false);
    await profile('ModernFtps').setValue();
    expect(profile('ModernFtps').element.checked).toBe(true);
    expect(name().element.value).toBe('Retained');
    expect(view.get<HTMLButtonElement>('button[type="submit"]').element.disabled).toBe(false);
});

test('Source save sends explicit profile and retains intent after a conflict or uncertain outcome', async () => {
    for (const status of [409, 503]) {
        requests.save.mockRejectedValueOnce({ responseStatusCode: status });
        const { view, choose, name, profile, click, submit } = await mountSource([camera('a', 'Shared upload', 7)]);
        await choose(0);
        await name().setValue('Retained');
        await click('Security');
        await profile('PlainFtpLan').setValue();
        await submit();
        const [id, body] = requests.save.mock.lastCall!;
        expect(id).toBe('a');
        expect(body).toMatchObject({ sourceType: 'CameraFtpUpload', expectedRevision: 7, connectionProfile: 'PlainFtpLan' });
        expect(body).not.toHaveProperty('allowLegacyFtps');
        expect(name().element.value).toBe('Retained');
        expect(profile('PlainFtpLan').element.checked).toBe(true);
        expect(view.emitted('dirty-change')?.at(-1)).toEqual([true]);
        expect(view.get('p[tabindex="-1"]').text()).toMatch(status === 409 ? /conflict/ : /uncertain/);
        expect(view.get<HTMLButtonElement>('button[type="submit"]').element.disabled).toBe(true);
    }
});

test('Source username browser pattern accepts hyphens and underscores and rejects paths or spaces', async () => {
    const { view } = await mountSource();
    const input = view.get<HTMLInputElement>('#source-username').element;
    const valid = new RegExp('^(?:' + input.pattern + ')$', 'v');
    for (const value of ['a', 'Camera-7', 'camera_one', '7_camera-one', 'a'.repeat(64)]) expect(valid.test(value)).toBe(true);
    for (const value of ['', '-camera', '_camera', 'camera/root', 'camera name', 'a'.repeat(65)]) expect(valid.test(value)).toBe(false);
});

test('Source creation discloses its password only after the saved read for the current editor', async () => {
    for (const change of [false, true]) {
        const saved = Promise.withResolvers<unknown>();
        requests.create.mockResolvedValue({ id: 'created', secret: 'SYNTHETIC_ONE_TIME_VALUE' });
        requests.saved.mockReturnValueOnce(saved.promise);
        const { view, name, submit, choose, click } = await mountSource([camera('other')]);
        await name().setValue('New camera');
        await submit();
        const [id, options] = requests.saved.mock.lastCall!;
        expect(id).toBe('created');
        expect(view.find('output.generated-password').exists()).toBe(false);
        if (change) {
            await choose(0);
            await click('Discard changes');
            await name().setValue('Unrelated draft');
        }
        expect(options.options[0].signal.aborted).toBe(change);
        saved.resolve(camera('created'));
        await flushPromises();
        if (change) {
            expect(view.find('output.generated-password').exists()).toBe(false);
            expect(name().element.value).toBe('Unrelated draft');
            expect(view.text()).not.toContain('Source created.');
        } else {
            expect(view.get('output.generated-password').text()).toBe('SYNTHETIC_ONE_TIME_VALUE');
            expect(view.text()).toContain('Source created. Copy its password now.');
        }
    }
});

test('Source profile defaults require confirmed modern capability and never downgrade after failure', async () => {
    const pending = Promise.withResolvers<unknown>();
    requests.capabilities.mockReturnValueOnce(pending.promise).mockResolvedValueOnce(setup);
    const { view, click, profile } = await mountSource();
    expect(profile('ModernFtps').element.checked).toBe(false);
    expect(view.get<HTMLButtonElement>('button[type="submit"]').element.disabled).toBe(true);
    pending.reject(new Error('unavailable'));
    await flushPromises();
    expect(view.text()).toContain('Connection profiles unavailable');
    expect(profile('PlainFtpLan').element.checked).toBe(false);
    await click('Retry connection profiles');
    expect(profile('ModernFtps').element.checked).toBe(true);
    requests.capabilities.mockResolvedValueOnce({ profiles: profiles.filter(p => p.profile === 'PlainFtpLan'), passwordPolicy });
    const plain = await mountSource();
    expect(plain.profile('PlainFtpLan').element.checked).toBe(false);
    expect(plain.view.get<HTMLButtonElement>('button[type="submit"]').element.disabled).toBe(true);
});

test('Source status reconciliation retains deployment capabilities and an in-flight initial read', async () => {
    for (const initiallyReady of [true, false]) {
        const pending = Promise.withResolvers<unknown>();
        requests.capabilities.mockReturnValueOnce(pending.promise);
        const { view, profile } = await mountSource();
        if (initiallyReady) { pending.resolve(setup); await flushPromises(); }
        await view.setProps({ sources: [camera('a'), camera('b')], recoverySequence: 1 });
        if (!initiallyReady) pending.resolve(setup);
        await flushPromises();
        expect(view.text()).toContain('ingest.example.test');
        expect(profile('ModernFtps').element.checked).toBe(true);
        await view.setProps({ sources: [] });
        await flushPromises();
        expect(view.get<HTMLButtonElement>('button[type="submit"]').element.disabled).toBe(false);
    }
    expect(requests.capabilities).toHaveBeenCalledTimes(2);
});

test('Source setup retains loaded profiles through recovery checks until an explicit refresh', async () => {
    requests.capabilities.mockRejectedValueOnce(new Error('unavailable'));
    const { view, name, click, profile } = await mountSource();
    await click('Retry connection profiles');
    await name().setValue('Retained setup draft');
    for (const recoverySequence of [1, 2]) {
        await view.setProps({ recoverySequence });
        await flushPromises();
    }
    expect(requests.capabilities).toHaveBeenCalledTimes(2);
    expect(view.text()).toContain('ingest.example.test');
    expect(profile('ModernFtps').element.checked).toBe(true);
    expect(name().element.value).toBe('Retained setup draft');
});

test('Source CA download uses the selected endpoint and ignores a late result after owner change', async () => {
    const { view, click, choose } = await mountSource([camera('a')]);
    const pending = Promise.withResolvers<unknown>();
    const downloaded: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { downloaded.push(this.download); });
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:synthetic-ca');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    requests.root.mockReturnValueOnce(pending.promise).mockResolvedValueOnce(new Uint8Array([65, 66]).buffer);
    await click('Security');
    await click('Download CA root');
    const signal: AbortSignal = requests.root.mock.lastCall?.[1].options[0].signal;
    await choose(0);
    expect(signal.aborted).toBe(true);
    pending.resolve(new Uint8Array([65, 66]).buffer);
    await flushPromises();
    expect(downloaded).toEqual([]);
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
        await view.findAll('button').find(b => b.text().trim() === 'Download CA root')!.trigger('click');
        await flushPromises();
        expect(requests.root.mock.calls.map(([profile]) => profile)).toEqual(['ModernFtps', 'ModernFtps']);
        expect(downloaded).toEqual(['shutterspool-ModernFtps-ca-root.pem']);
        await vi.advanceTimersByTimeAsync(1000);
        expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:synthetic-ca');
    } finally { vi.useRealTimers(); }
});

test('Source CA failures give setup guidance without bypass or substituting a root', async () => {
    requests.root.mockRejectedValueOnce({ responseStatusCode: 503 });
    const { view, click, profile } = await mountSource();
    await click('Security');
    await profile('LegacyFtps').setValue();
    await click('Download CA root');
    expect(view.text()).toMatch(/public root configured for this endpoint/);
    expect(profile('LegacyFtps').element.checked).toBe(true);
    expect(requests.root.mock.calls.map(([profile]) => profile)).toEqual(['LegacyFtps']);
});

test('Source save failure brings recovery into view only for the current draft owner', async () => {
    const placement = vi.spyOn(HTMLElement.prototype, 'scrollIntoView');
    for (const change of [false, true]) {
        placement.mockClear();
        const pending = Promise.withResolvers<unknown>();
        requests.save.mockReturnValueOnce(pending.promise);
        const { view, choose, submit } = await mountSource([camera('a'), camera('b')]);
        await choose(0);
        await submit();
        if (change) await choose(1);
        pending.reject({ responseStatusCode: 500 });
        await flushPromises();
        if (change) {
            expect(placement).not.toHaveBeenCalled();
            expect(view.find('p[tabindex="-1"]').exists()).toBe(false);
        } else {
            const notice = view.get('p[tabindex="-1"]');
            expect(notice.text()).toContain('Reload saved settings');
            expect(document.activeElement).toBe(notice.element);
            expect(placement).toHaveBeenCalledWith({ block: 'center' });
        }
    }
});

test('Source custom credentials submit exact bytes independently of generated username and clear after rejection', async () => {
    requests.create.mockRejectedValueOnce({ responseStatusCode: 400, errors: { additionalData: {
        Password: ['Password does not meet deployment policy.', 'Choose another password.'], PasswordMode: 1, Username: { details: 'Unexpected' },
    } } });
    const { view, click, custom, name, submit } = await mountSource();
    await name().setValue('Shared camera');
    await click('Connection');
    await custom('Exact_CASE!123');
    await submit();
    expect(requests.create.mock.lastCall?.[0]).toMatchObject({ passwordMode: 'Custom', username: undefined, password: 'Exact_CASE!123' });
    expect(view.get<HTMLInputElement>('#source-custom-password').element.value).toBe('');
    expect(view.get('#source-password-error').text()).toBe('Password does not meet deployment policy. Choose another password.');
    expect(view.get('#source-username-error').text()).toBe('Check this credential field.');
    expect(view.findAll('.credential-error').map(e => e.text())).toContain('1');
    expect(view.get('#source-tab-1').attributes('aria-selected')).toBe('true');
    expect(name().element.value).toBe('Shared camera');
    expect(view.emitted('dirty-change')?.at(-1)).toEqual([true]);
    expect(view.find('.source-password-disclosure').exists()).toBe(false);
});

test('Source replacement keeps non-secret settings draft and revision while generated mode excludes custom password', async () => {
    requests.replace.mockResolvedValueOnce({ current: { ...camera('a', 'Saved', 5), username: 'new_user' }, secret: 'SYNTHETIC_ONCE' });
    requests.save.mockResolvedValueOnce(camera('a', 'Unsaved name', 6));
    const { view, choose, name, click, custom, replace, submit } = await mountSource([camera('a', 'Saved', 4), camera('b')]);
    await choose(0);
    await name().setValue('Unsaved name');
    await click('Connection');
    await click('Replace credentials');
    await view.get('#source-username').setValue('new_user');
    await custom('STALE_SYNTHETIC!');
    await view.get('#source-password-mode').setValue('Generated');
    await replace();
    expect(requests.replace.mock.lastCall).toEqual(['a', { expectedRevision: 4, username: 'new_user', passwordMode: 'Generated', password: undefined }]);
    expect(view.get('output.generated-password').text()).toBe('SYNTHETIC_ONCE');
    expect(name().element.value).toBe('Unsaved name');
    expect(view.emitted('dirty-change')?.at(-1)).toEqual([true]);
    await submit();
    expect(requests.save.mock.lastCall?.[1].expectedRevision).toBe(5);
    await choose(1);
    expect(view.find('.source-password-disclosure').exists()).toBe(false);
});

test('Source uncertain creation blocks blind replay and retains draft until a successful reread', async () => {
    requests.create.mockRejectedValueOnce({ responseStatusCode: 503 });
    requests.sources.mockRejectedValueOnce({ responseStatusCode: 503 }).mockResolvedValueOnce({ items: [] });
    const { view, click, name, submit, custom } = await mountSource();
    await name().setValue('Retained');
    await click('Connection');
    await view.get('#source-username').setValue('chosen_user');
    await custom('SYNTHETIC_PRIVATE');
    await submit();
    expect(view.get<HTMLInputElement>('#source-custom-password').element.value).toBe('');
    await submit();
    await click('Reload saved settings');
    expect(view.get<HTMLButtonElement>('button[type="submit"]').element.disabled).toBe(true);
    await click('Reload saved settings');
    expect(requests.sources).toHaveBeenCalledTimes(2);
    expect(view.get<HTMLButtonElement>('button[type="submit"]').element.disabled).toBe(false);
    expect(view.get<HTMLInputElement>('#source-username').element.value).toBe('chosen_user');
    expect(name().element.value).toBe('Retained');
    expect(view.emitted('dirty-change')?.at(-1)).toEqual([true]);
    expect(requests.create).toHaveBeenCalledTimes(1);
});

test('Source replacement confirmation precedes mutation and access loss clears private inputs and fences disclosures', async () => {
    const pending = Promise.withResolvers<unknown>();
    requests.replace.mockReturnValueOnce(pending.promise);
    const { view, choose, click, custom } = await mountSource([camera('a')]);
    await choose(0);
    await click('Connection');
    await click('Replace credentials');
    await custom('SYNTHETIC_PRIVATE');
    await click('Replace credentials...');
    expect(view.get('dialog[aria-labelledby="replace-source-credentials-title"]').attributes('open')).toBeDefined();
    expect(requests.replace).not.toHaveBeenCalled();
    await click('Confirm replacement');
    expect(requests.replace).toHaveBeenCalledTimes(1);
    clearPrivateAccess();
    pending.resolve({ current: camera('a', 'A', 2), secret: 'SYNTHETIC_LATE' });
    await flushPromises();
    expect(view.find('#source-custom-password').exists()).toBe(false);
    expect(view.find('.source-password-disclosure').exists()).toBe(false);
    expect(view.get('.source-summary').attributes('aria-pressed')).toBe('false');
});

test('Source duplicate usernames from generated conflict extensions remain field errors for creation and replacement', async () => {
    for (const action of ['create', 'replace']) {
        for (const [extensions, title, message] of [
            [{ errors: { username: ['Choose another username.'] } }, 'Conflict', 'Choose another username.'],
            [{ field: 'username', errorCode: 'SOURCE_USERNAME_CONFLICT' }, 'Assigned.', 'Assigned.'],
            [{ field: 'username', errorCode: 'SOURCE_USERNAME_CONFLICT' }, { details: 'unexpected' }, 'This username is already in use.'],
        ] as const) {
            const endpoint = action === 'create' ? requests.create : requests.replace;
            endpoint.mockRejectedValueOnce({ responseStatusCode: 409, additionalData: extensions, title });
            const { view, choose, name, click, custom, replace, submit } = await mountSource([camera('a')]);
            if (action === 'replace') await choose(0);
            await name().setValue('Retained camera draft');
            await click('Connection');
            if (action === 'replace') await click('Replace credentials');
            await view.get('#source-username').setValue('duplicate_name');
            await custom('SYNTHETIC_PRIVATE');
            if (action === 'replace') await replace(); else await submit();
            expect(view.get('#source-username-error').text()).toBe(message);
            expect(view.get<HTMLInputElement>('#source-username').element.value).toBe('duplicate_name');
            expect(view.get<HTMLInputElement>('#source-custom-password').element.value).toBe('');
            expect(name().element.value).toBe('Retained camera draft');
            expect(view.find('.source-password-disclosure').exists()).toBe(false);
            expect(view.text()).not.toContain('Reload saved settings');
            expect(view.emitted('dirty-change')?.at(-1)).toEqual([true]);
            expect(view.get('#source-tab-1').attributes('aria-selected')).toBe('true');
        }
    }
    expect(requests.create).toHaveBeenCalledTimes(3);
    expect(requests.replace).toHaveBeenCalledTimes(3);
    expect(requests.saved).not.toHaveBeenCalled();
    expect(requests.sources).not.toHaveBeenCalled();
});

test('three Source dots and thumbnail structure remain fixed across phase/loading changes and hint bursts', async () => {
    requests.recent.mockResolvedValue({ items: [{ id: 'photo', displayName: 'SYNTHETIC.JPG' }] });
    const { view, choose } = await mountSource([camera('a')]);
    await choose(0);
    const frame = view.get('.recent-stream-frame').element;
    const thumbnail = view.get('authorized-preview-stub').element;
    const dots = [...view.element.querySelectorAll('.flow-dot')];
    const counters = [...view.element.querySelectorAll('.source-counters > span')];
    expect(dots).toHaveLength(3);
    expect(counters).toHaveLength(3);
    const status = view.get('.source-status-control');
    expect(status.attributes('popovertarget')).toBe(view.get('.source-status-popover').attributes('id'));
    expect(view.get('.source-status-popover').attributes('popover')).toBe('auto');
    expect(status.element.parentElement).toBe(view.get('button.source-summary').element.parentElement);
    expect(view.get('.source-row').classes()).toContain('selected');
    expect(view.get('.source-summary').attributes('aria-pressed')).toBe('true');
    const recent = Promise.withResolvers<unknown>();
    requests.recent.mockReturnValueOnce(recent.promise);
    const hint = { sourceId: 'a', transferId: 'one', observedAt: new Date().toISOString(), accepted: true, previewReady: false };
    await view.setProps({ activity: { captureId: 'photo', kind: 'captureChanged', sequence: 1 } });
    for (const [version, phase] of (['Receiving', 'AcceptedAwaitingGrouping', 'PreparingPreview', 'Ready', 'Stale', 'FailedUnavailable'] as const).entries()) {
        await view.setProps({ phaseActivity: { ...hint, phase, version: version + 1, accepted: !['Receiving', 'Stale'].includes(phase), previewReady: phase === 'Ready' } });
        expect(view.get('.recent-stream-frame').element).toBe(frame);
        expect(view.get('authorized-preview-stub').element).toBe(thumbnail);
        expect([...view.element.querySelectorAll('.flow-dot')].every((dot, i) => dot === dots[i])).toBe(true);
        expect([...view.element.querySelectorAll('.source-counters > span')].every((node, i) => node === counters[i])).toBe(true);
        const row = view.get('.source-row').element.cloneNode(true) as HTMLElement;
        row.querySelector('[popover]')!.remove();
        expect(row.textContent).not.toMatch(/Loading|observation|Transfer|Preparing|Stale/);
    }
    for (let i = 0; i < 6; i++) {
        await view.setProps({ phaseActivity: { ...hint, transferId: 'burst-' + i, phase: 'Receiving', accepted: false, version: 1 } });
    }
    expect(view.findAll('.transfer-observations li')).toHaveLength(6);
    expect(view.get('#source-status-a').text()).toContain('Showing 6 of 7');
    expect(view.get('.source-activity').classes()).toContain('running');
    await view.setProps({ motionEnabled: false });
    expect(view.get('.source-activity').classes()).not.toContain('running');
    await view.setProps({ motionEnabled: true, connected: false });
    expect(view.get('.source-activity').classes()).not.toContain('running');
    recent.resolve({ items: [] });
    await flushPromises();
});

test('Source custom password checks show policy boundaries and block invalid credential writes before confirmation', async () => {
    for (const existing of [false, true]) {
        const { view, click, choose, custom, submit } = await mountSource([camera('a')]);
        if (existing) await choose(0);
        await click('Connection');
        if (existing) await click('Replace credentials');
        for (const value of ['', 'A'.repeat(11), 'A'.repeat(129), 'Camera password12', 'CameräPassword12']) {
            await custom(value);
            if (existing) {
                await click('Replace credentials...');
                view.get('dialog[aria-labelledby="replace-source-credentials-title"] .danger').element.dispatchEvent(new Event('click', { bubbles: true }));
                await flushPromises();
            } else await submit();
            expect(requests.create).not.toHaveBeenCalled();
            expect(requests.replace).not.toHaveBeenCalled();
            expect(view.get('dialog[aria-labelledby="replace-source-credentials-title"]').attributes('open')).toBeUndefined();
            expect(view.get<HTMLInputElement>('#source-custom-password').element.value).toBe(value);
            expect(view.get('.password-status').text()).toBe('');
        }
        for (const value of ['A'.repeat(12), 'CameraPassword!12', 'A'.repeat(128)]) {
            await custom(value);
            expect(view.get('.password-status').text()).toContain('Password meets requirements');
        }
        expect(view.get('#source-custom-password').attributes('maxlength')).toBeUndefined();
    }
    requests.capabilities.mockResolvedValueOnce({ profiles, passwordPolicy: { ...passwordPolicy, minimumLength: 1 } });
    const one = await mountSource();
    await one.custom('!');
    expect(one.view.get('.password-status').text()).toContain('Password meets requirements');
});

test('Source committed creation with lost response rereads the created Source before deliberate password replacement', async () => {
    const committed = camera('created', 'Camera draft', 1);
    const events: string[] = [];
    requests.create.mockImplementation(async () => { events.push('create'); throw { responseStatusCode: 503 }; });
    requests.sources.mockImplementation(async () => { events.push('read:1'); return { items: [committed] }; });
    requests.replace.mockImplementation(async (_id, body) => {
        events.push('replace:' + body.expectedRevision);
        return { current: { ...committed, configurationRevision: 2 }, secret: 'SYNTHETIC_NEW_DISCLOSURE' };
    });
    const { view, click, choose, name, submit, replace } = await mountSource();
    await name().setValue('Camera draft');
    await click('Connection');
    await view.get('#source-username').setValue('chosen_user');
    await submit();
    await submit();
    expect(events).toEqual(['create']);
    expect(view.find('.source-password-disclosure').exists()).toBe(false);
    await click('Reload saved settings');
    expect(events).toEqual(['create', 'read:1']);
    expect(name().element.value).toBe('Camera draft');
    expect(view.get<HTMLInputElement>('#source-username').element.value).toBe('chosen_user');
    expect(view.find('.source-password-disclosure').exists()).toBe(false);
    expect(view.text()).toContain('cannot be recovered');
    await view.setProps({ sources: [committed] });
    await choose(0);
    await click('Discard changes');
    await click('Replace credentials');
    await replace();
    expect(events).toEqual(['create', 'read:1', 'replace:1']);
    expect(view.get('output.generated-password').text()).toBe('SYNTHETIC_NEW_DISCLOSURE');
});

test('Source committed replacement with lost response rereads the advanced revision before a deliberate new replacement', async () => {
    let committed = camera('a', 'Saved camera', 4);
    const events: string[] = [];
    requests.saved.mockImplementation(async () => { events.push('read:' + committed.configurationRevision); return { ...committed }; });
    requests.replace.mockImplementation(async (_id, body) => {
        events.push('replace:' + body.expectedRevision);
        expect(body.expectedRevision).toBe(committed.configurationRevision);
        committed = { ...committed, configurationRevision: body.expectedRevision + 1, username: body.username };
        if (committed.configurationRevision === 5) throw { responseStatusCode: 503 };
        return { current: { ...committed }, secret: null };
    });
    const { view, click, choose, name, custom, replace } = await mountSource([{ ...committed }]);
    await choose(0);
    await name().setValue('Retained settings draft');
    await click('Connection');
    await click('Replace credentials');
    await view.get('#source-username').setValue('new_user');
    await custom('SYNTHETIC_CUSTOM_ONE');
    await replace();
    expect(committed).toMatchObject({ configurationRevision: 5, username: 'new_user' });
    expect(view.get<HTMLInputElement>('#source-custom-password').element.value).toBe('');
    expect(view.find('.source-password-disclosure').exists()).toBe(false);
    const retryPassword = view.get<HTMLInputElement>('#source-custom-password').element;
    retryPassword.value = 'SYNTHETIC_PENDING_RETRY';
    retryPassword.dispatchEvent(new Event('input', { bubbles: true }));
    await flushPromises();
    const confirm = view.get<HTMLButtonElement>('dialog[aria-labelledby="replace-source-credentials-title"] .danger');
    expect(confirm.element.disabled).toBe(true);
    confirm.element.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    expect(events).toEqual(['replace:4']);
    await click('Reload saved settings');
    expect(events).toEqual(['replace:4', 'read:5']);
    expect(name().element.value).toBe('Retained settings draft');
    expect(view.get<HTMLInputElement>('#source-username').element.value).toBe('new_user');
    expect(view.find('.source-password-disclosure').exists()).toBe(false);
    await custom('SYNTHETIC_CUSTOM_TWO');
    await replace();
    expect(events).toEqual(['replace:4', 'read:5', 'replace:5']);
    expect(committed.configurationRevision).toBe(6);
    expect(view.find('.source-password-disclosure').exists()).toBe(false);
    expect(name().element.value).toBe('Retained settings draft');
});
