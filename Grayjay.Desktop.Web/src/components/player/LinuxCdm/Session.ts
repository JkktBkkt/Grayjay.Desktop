import type { ISourceDrm } from '../../../backend/DetailsBackend';
import { call, fromBase64, listen, toBase64 } from './bridge';
import { assertSupportedEncryption, clearEncryptionMetadata, initializationData } from './mp4';

function deferred() {
    let resolve!: () => void, reject!: (error: unknown) => void;
    const promise = new Promise<void>((yes, no) => { resolve = yes; reject = no; });
    void promise.catch(() => {});
    return { promise, resolve, reject };
}
export class PlaybackSession {
    readonly token = crypto.randomUUID();
    private readonly cancel = new AbortController();
    private readonly keys = new Map<string, ReturnType<typeof deferred>>();
    private readonly requests = new Map<string, Promise<void>>();
    private readonly licenses = new Map<string, Promise<void>>();
    private readonly started: Promise<void>;
    private readonly unlisten: () => void;
    private failure?: Error;
    private closed = false;
    constructor(private readonly drm: ISourceDrm, private readonly onError: (error: string) => void) {
        this.unlisten = listen(this.token, event => this.event(event));
        this.started = this.start();
        void this.started.catch(error => this.fail(error));
    }
    private async start() {
        let certificate = this.drm.serviceCertificate;
        if (!certificate && this.drm.certificateUrl) {
            const response = await fetch(new URL(this.drm.certificateUrl, location.href), { signal: this.cancel.signal });
            if (!response.ok) throw new Error('Playback certificate HTTP ' + response.status);
            certificate = toBase64(new Uint8Array(await response.arrayBuffer()));
        }
        this.cancel.signal.throwIfAborted();
        await call('start', this.token, certificate ?? '');
        this.cancel.signal.throwIfAborted();
    }
    private key(id: string) {
        let value = this.keys.get(id);
        if (!value) { value = deferred(); this.keys.set(id, value); }
        return value;
    }
    private fail(error: unknown) {
        if (this.closed || this.failure) return;
        this.failure = error instanceof Error ? error : new Error(String(error));
        for (const key of this.keys.values()) key.reject(this.failure);
        this.onError(this.failure.message);
    }
    private async event(event: any) {
        if (this.closed) return;
        if (event.event === 'fatal') { this.fail(new Error(event.message)); return; }
        if (event.event === 'keys') {
            if (event.statuses.includes(0)) this.key(event.session).resolve();
            else if (event.statuses.some((status: number) => status !== 5)) this.fail(new Error('The playback license did not provide a usable key.'));
        } else if (event.event === 'message') {
            const previous = this.licenses.get(event.session) ?? Promise.resolve();
            const task = previous.then(async () => {
                const response = await fetch(new URL(this.drm.licenseUrl, location.href), {
                    method: 'POST', headers: { 'Content-Type': 'application/octet-stream' },
                    body: fromBase64(event.data), signal: this.cancel.signal
                });
                if (!response.ok) throw new Error('Playback license HTTP ' + response.status);
                await call('update', this.token, toBase64(new Uint8Array(await response.arrayBuffer())), event.session);
            });
            this.licenses.set(event.session, task);
            void task.catch(error => this.fail(error));
        } else if (event.event === 'closed') this.key(event.session).reject(new Error('The playback license closed.'));
    }
    requestKey(data: Uint8Array) {
        void this.ensureKey(data).catch(error => this.fail(error));
    }
    async ensureKey(data: Uint8Array) {
        const encoded = toBase64(data);
        let request = this.requests.get(encoded);
        if (!request) {
            request = (async () => {
                await this.started; this.check();
                const created = await call<{ session: string }>('session', this.token, encoded);
                this.check();
                let timeout: ReturnType<typeof setTimeout> | undefined;
                try {
                    await Promise.race([this.key(created.session).promise, new Promise<never>((_, no) => {
                        timeout = setTimeout(() => no(new Error('The playback license timed out.')), 30000);
                    })]);
                    this.check();
                } finally { clearTimeout(timeout); }
            })();
            this.requests.set(encoded, request);
        }
        await request;
    }
    async initialize(data: Uint8Array) {
        assertSupportedEncryption(data);
        for (const pssh of initializationData(data)) this.requestKey(pssh);
        this.check();
        return clearEncryptionMetadata(data);
    }
    async decrypt(init: Uint8Array, fragment: Uint8Array) {
        assertSupportedEncryption(fragment);
        await this.started;
        await Promise.all(this.requests.values()); this.check();
        const wire = new Uint8Array(4 + init.length + fragment.length);
        new DataView(wire.buffer).setUint32(0, init.length); wire.set(init, 4); wire.set(fragment, 4 + init.length);
        const result = await call<{ data: string; samples: number }>('fragment', this.token, toBase64(wire));
        this.check(); return clearEncryptionMetadata(fromBase64(result.data));
    }
    private check() { this.cancel.signal.throwIfAborted(); if (this.failure) throw this.failure; }
    close() {
        if (this.closed) return;
        this.closed = true; this.cancel.abort(); this.unlisten();
        for (const key of this.keys.values()) key.reject(new DOMException('Playback closed', 'AbortError'));
        void call('close', this.token).catch(() => {});
    }
}
