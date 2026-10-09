import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { transformSync } from '../../../Grayjay.Desktop.Web/node_modules/esbuild/lib/main.js';

const source = readFileSync(new URL('../../../Grayjay.Desktop.Web/src/components/player/LinuxCdm/Session.ts', import.meta.url), 'utf8');
const code = transformSync(source, { loader: 'ts', format: 'cjs', target: 'es2022' }).code;
const deferred = () => { let resolve; const promise = new Promise(yes => resolve = yes); return { promise, resolve }; };
function fixture(drm = {}) {
    const boot = deferred(), license = deferred(), calls = [], errors = [];
    let listener;
    const bridge = {
        call: async (method, token, data) => {
            calls.push({ method, token, data });
            if (method === 'start') await boot.promise;
            if (method === 'session') return { session: 'license-session' };
            if (method === 'fragment') return { data: 'AQI=', samples: 1 };
        },
        listen: (_, handler) => { listener = handler; return () => listener = undefined; },
        toBase64: data => Buffer.from(data).toString('base64'),
        fromBase64: data => new Uint8Array(Buffer.from(data, 'base64'))
    };
    const context = { module: { exports: {} }, require: name => name === './bridge' ? bridge : {
        assertSupportedEncryption() {}, initializationData: () => [new Uint8Array([9])],
        clearEncryptionMetadata: data => new Uint8Array(data)
    }, crypto: { randomUUID: () => 'playback-token' }, AbortController, DOMException, Uint8Array, DataView,
        setTimeout, clearTimeout, fetch: () => license.promise, Error };
    vm.runInNewContext(code, context);
    const session = new context.module.exports.PlaybackSession({ licenseUrl: '/license', ...drm }, error => errors.push(error));
    return { session, boot, license, calls, errors, emit: event => listener?.(event), context };
}
const flush = () => new Promise(yes => setImmediate(yes));
test('initialization can load during startup but media waits for usable license keys', async () => {
    const f = fixture();
    assert.deepEqual(Array.from(await f.session.initialize(new Uint8Array([1]))), [1]);
    const decrypted = f.session.decrypt(new Uint8Array([1]), new Uint8Array([2]));
    await flush();
    assert.deepEqual(f.calls.map(x => x.method), ['start']);
    f.boot.resolve(); await flush();
    assert.deepEqual(f.calls.map(x => x.method), ['start', 'session']);
    await f.emit({ event: 'keys', session: 'license-session', statuses: [0] });
    assert.deepEqual(Array.from(await decrypted), [1, 2]);
    assert.deepEqual(f.calls.map(x => x.method), ['start', 'session', 'fragment']);
    f.session.close();
});
test('closing during guest startup prevents late license and fragment requests', async () => {
    const f = fixture();
    await f.session.initialize(new Uint8Array([1]));
    const decrypted = f.session.decrypt(new Uint8Array([1]), new Uint8Array([2]));
    const rejected = assert.rejects(decrypted, { name: 'AbortError' });
    f.session.close(); f.boot.resolve();
    await rejected; await flush();
    assert.deepEqual(f.calls.map(x => x.method), ['start', 'close']);
    assert.deepEqual(f.errors, []);
});
test('service certificate is supplied when starting the guest rather than inherited', async () => {
    const f = fixture({ serviceCertificate: 'Y2VydA==' });
    f.boot.resolve(); await flush();
    assert.equal(f.calls[0].method, 'start');
    assert.equal(f.calls[0].data, 'Y2VydA==');
    f.session.close();
});
