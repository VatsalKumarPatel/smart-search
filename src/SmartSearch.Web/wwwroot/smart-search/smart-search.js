/*
 * Smart Search - client behaviour (no build step, no framework).
 *
 * Talks to the server only through data-ss-* attributes, so designers can
 * change markup and classes freely as long as those attributes stay.
 *
 *   [data-ss-root]            wrapper; data-ss-endpoint, data-ss-click-url, data-ss-hub, data-ss-worklet
 *   [data-ss-form]            search form
 *   [data-ss-input]           text box (voice fills it too)
 *   [data-ss-mic]             mic button
 *   [data-ss-status]          short status line ("Listening...", "Searching...")
 *   [data-ss-output]          results partial is injected here
 *   template[data-ss-loading] / template[data-ss-error]
 *   inside results: [data-ss-layout], [data-ss-pick-client], [data-ss-followup],
 *                   [data-ss-page], [data-ss-link], [data-ss-sortable], [data-ss-item]
 *
 * Voice needs @microsoft/signalr loaded on the page (window.signalR) and a hub with:
 *   server: StartTranscription(IAsyncEnumerable<string> base64Pcm16kChunks)
 *   client: "transcript"(text: string, isFinal: bool), "transcriptError"(message: string)
 */
(function () {
    'use strict';

    function SmartSearch(root) {
        this.root = root;
        this.form = root.querySelector('[data-ss-form]');
        this.input = root.querySelector('[data-ss-input]');
        this.micBtn = root.querySelector('[data-ss-mic]');
        this.status = root.querySelector('[data-ss-status]');
        this.output = root.querySelector('[data-ss-output]');
        this.endpoint = root.dataset.ssEndpoint;
        this.clickUrl = root.dataset.ssClickUrl;
        this.queryId = null;
        this.source = 'Text';
        this.abort = null;
        this.voice = null;

        this.form.addEventListener('submit', this.onSubmit.bind(this));
        this.output.addEventListener('click', this.onResultsClick.bind(this));
        this.input.addEventListener('input', () => { this.source = 'Text'; });
        this.input.addEventListener('keydown', (e) => { if (e.key === 'Escape') this.clear(); });
        this.setupMic();
    }

    /* ------------------------------------------------------------ search */

    SmartSearch.prototype.onSubmit = function (e) {
        e.preventDefault();
        var q = this.input.value.trim();
        if (!q) { this.setStatus('Type a question or use the mic.'); this.input.focus(); return; }
        if (this.voice) this.voice.stop();
        this.load({ q: q, source: this.source });
    };

    /** Re-runs are keyed by queryId so the server reuses the stored intent (no LLM call). */
    SmartSearch.prototype.rerun = function (params) {
        if (!this.queryId) return;
        params.queryId = this.queryId;
        this.load(params);
    };

    SmartSearch.prototype.load = function (params) {
        var self = this;
        if (this.abort) this.abort.abort();
        this.abort = new AbortController();

        var url = new URL(this.endpoint, window.location.origin);
        Object.keys(params).forEach(function (k) {
            if (params[k] !== undefined && params[k] !== null && params[k] !== '') url.searchParams.set(k, params[k]);
        });

        this.setStatus('Searching...');
        this.output.setAttribute('aria-busy', 'true');
        this.showTemplate('[data-ss-loading]');

        fetch(url.toString(), {
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest', 'Accept': 'text/html' },
            signal: this.abort.signal
        })
            .then(function (res) {
                if (res.status === 401) { window.location.reload(); throw new Error('Session expired'); }
                if (!res.ok) throw new Error('HTTP ' + res.status);
                return res.text();
            })
            .then(function (html) {
                self.output.innerHTML = html;   // server-rendered Razor, values HTML-encoded by Razor
                var results = self.output.querySelector('[data-ss-results]');
                self.queryId = results ? results.dataset.ssQueryId : null;
                self.setStatus('');
            })
            .catch(function (err) {
                if (err.name === 'AbortError') return;
                self.showTemplate('[data-ss-error]');
                self.setStatus('');
            })
            .finally(function () { self.output.removeAttribute('aria-busy'); });
    };

    SmartSearch.prototype.clear = function () {
        this.input.value = '';
        this.output.innerHTML = '';
        this.queryId = null;
        this.setStatus('');
    };

    SmartSearch.prototype.onResultsClick = function (e) {
        var t = e.target.closest('[data-ss-layout],[data-ss-pick-client],[data-ss-followup],[data-ss-page],[data-ss-link],[data-ss-sortable]');
        if (!t || !this.output.contains(t)) return;

        if (t.hasAttribute('data-ss-layout')) {
            e.preventDefault();
            this.track({ clickType: 'Layout', linkKey: t.dataset.ssLayout });
            this.rerun({ layout: t.dataset.ssLayout });
        } else if (t.hasAttribute('data-ss-pick-client')) {
            e.preventDefault();
            this.track({ clickType: 'Suggestion', clientId: t.dataset.ssPickClient, entityType: 'Client', entityId: t.dataset.ssPickClient });
            this.rerun({ clientId: t.dataset.ssPickClient });
        } else if (t.hasAttribute('data-ss-followup')) {
            e.preventDefault();
            this.track({ clickType: 'FollowUp' });
            this.input.value = t.dataset.ssFollowup;
            this.source = 'Text';
            this.load({ q: t.dataset.ssFollowup, source: 'Text' });
        } else if (t.hasAttribute('data-ss-page')) {
            e.preventDefault();
            if (!t.disabled) this.rerun({ page: t.dataset.ssPage });
        } else if (t.hasAttribute('data-ss-link')) {
            // normal navigation to the existing EHR page; only record the click
            var item = t.closest('[data-ss-item]');
            this.track({
                clickType: 'Link',
                linkKey: t.dataset.ssLink,
                entityType: item && item.dataset.entityType,
                entityId: item && item.dataset.entityId,
                clientId: item && item.dataset.clientId,
                rank: item && item.dataset.rank
            });
        } else if (t.hasAttribute('data-ss-sortable')) {
            this.sortTable(t);
        }
    };

    /* ------------------------------------------------------------ list sorting (current page) */

    SmartSearch.prototype.sortTable = function (th) {
        var table = th.closest('table');
        var tbody = table.tBodies[0];
        var index = th.cellIndex;
        var dir = th.getAttribute('aria-sort') === 'ascending' ? 'descending' : 'ascending';
        table.querySelectorAll('thead th[aria-sort]').forEach(function (h) { h.setAttribute('aria-sort', 'none'); });
        th.setAttribute('aria-sort', dir);

        var rows = Array.prototype.slice.call(tbody.rows);
        rows.sort(function (a, b) {
            var x = (a.cells[index].dataset.sortValue || a.cells[index].textContent).trim();
            var y = (b.cells[index].dataset.sortValue || b.cells[index].textContent).trim();
            var r = x.localeCompare(y, undefined, { numeric: true, sensitivity: 'base' });
            return dir === 'ascending' ? r : -r;
        });
        rows.forEach(function (r) { tbody.appendChild(r); });
    };

    /* ------------------------------------------------------------ click tracking */

    SmartSearch.prototype.track = function (data) {
        if (!this.clickUrl || !this.queryId || !navigator.sendBeacon) return;
        var fd = new FormData();
        fd.append('queryId', this.queryId);
        Object.keys(data).forEach(function (k) { if (data[k] != null) fd.append(k, data[k]); });
        var token = this.root.querySelector('input[name="__RequestVerificationToken"]');
        if (token) fd.append('__RequestVerificationToken', token.value);
        navigator.sendBeacon(this.clickUrl, fd);
    };

    /* ------------------------------------------------------------ helpers */

    SmartSearch.prototype.showTemplate = function (selector) {
        var tpl = this.root.querySelector('template' + selector);
        this.output.innerHTML = '';
        if (tpl) this.output.appendChild(tpl.content.cloneNode(true));
    };

    SmartSearch.prototype.setStatus = function (text) {
        if (this.status) this.status.textContent = text || '';
    };

    /* ------------------------------------------------------------ voice */

    SmartSearch.prototype.setupMic = function () {
        if (!this.micBtn) return;
        var demo = this.root.hasAttribute('data-ss-mic-demo');
        var hub = this.root.dataset.ssHub;
        var supported = demo || (hub && window.signalR && navigator.mediaDevices && window.AudioWorkletNode);
        if (!supported) { this.micBtn.hidden = true; return; }

        var self = this;
        this.micBtn.addEventListener('click', function () {
            if (self.voice) { self.voice.stop(); return; }
            self.voice = demo ? new DemoVoice(self) : new HubVoice(self, hub, self.root.dataset.ssWorklet);
            self.voice.start();
        });
    };

    SmartSearch.prototype.onVoiceState = function (listening) {
        this.micBtn.setAttribute('aria-pressed', listening ? 'true' : 'false');
        this.micBtn.setAttribute('aria-label', listening ? 'Stop voice search' : 'Search by voice');
        this.setStatus(listening ? 'Listening... speak your question, then press the mic again or Enter.' : '');
        if (!listening) { this.voice = null; this.input.focus(); }
    };

    /** Shows the transcript in the box. Never auto-submits: names are often misheard. */
    SmartSearch.prototype.onTranscript = function (text, isFinal) {
        this.input.value = text;
        this.source = 'Voice';
        if (isFinal) this.setStatus('Check the text, then press Enter to search.');
    };

    /* Real voice: mic -> AudioWorklet (16 kHz PCM16) -> SignalR stream -> Amazon Transcribe */
    function HubVoice(owner, hubUrl, workletUrl) {
        this.owner = owner; this.hubUrl = hubUrl; this.workletUrl = workletUrl;
        this.finalText = '';
    }

    HubVoice.prototype.start = function () {
        var self = this, o = this.owner;
        o.onVoiceState(true);

        this.connection = new window.signalR.HubConnectionBuilder().withUrl(this.hubUrl).build();
        this.connection.on('transcript', function (text, isFinal) {
            if (isFinal) { self.finalText = (self.finalText + ' ' + text).trim(); o.onTranscript(self.finalText, true); }
            else { o.onTranscript((self.finalText + ' ' + text).trim(), false); }
        });
        this.connection.on('transcriptError', function (msg) { o.setStatus(msg || 'Voice search failed.'); self.stop(); });

        navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } })
            .then(function (stream) {
                self.stream = stream;
                return self.connection.start();
            })
            .then(function () {
                self.ctx = new AudioContext();
                return self.ctx.audioWorklet.addModule(self.workletUrl);
            })
            .then(function () {
                self.subject = new window.signalR.Subject();
                self.connection.send('StartTranscription', self.subject);

                var src = self.ctx.createMediaStreamSource(self.stream);
                self.node = new AudioWorkletNode(self.ctx, 'pcm16-worklet');
                self.node.port.onmessage = function (e) { self.subject.next(toBase64(e.data)); };
                src.connect(self.node);

                self.timer = setTimeout(function () { self.stop(); }, 15000);   // safety cap
            })
            .catch(function (err) {
                o.setStatus(err && err.name === 'NotAllowedError'
                    ? 'Microphone permission was denied.'
                    : 'Voice search is unavailable right now.');
                self.stop();
            });
    };

    HubVoice.prototype.stop = function () {
        clearTimeout(this.timer);
        try { if (this.subject) this.subject.complete(); } catch (e) { /* already closed */ }
        if (this.node) this.node.disconnect();
        if (this.ctx) this.ctx.close();
        if (this.stream) this.stream.getTracks().forEach(function (t) { t.stop(); });
        var conn = this.connection;
        if (conn) setTimeout(function () { conn.stop(); }, 1500);   // let the final transcript arrive
        this.owner.onVoiceState(false);
    };

    function toBase64(buffer) {
        var bytes = new Uint8Array(buffer), bin = '';
        for (var i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
        return btoa(bin);
    }

    /* Preview only: types a phrase word by word to show the voice UX */
    function DemoVoice(owner) { this.owner = owner; }
    DemoVoice.prototype.start = function () {
        var self = this, words = 'Get me latest visit for vatsal'.split(' '), i = 0;
        this.owner.onVoiceState(true);
        this.timer = setInterval(function () {
            i++;
            self.owner.onTranscript(words.slice(0, i).join(' '), i === words.length);
            if (i === words.length) self.stop(true);
        }, 350);
    };
    DemoVoice.prototype.stop = function (keepStatus) {
        clearInterval(this.timer);
        this.owner.onVoiceState(false);
        if (keepStatus) this.owner.setStatus('Check the text, then press Enter to search.');
    };

    /* ------------------------------------------------------------ boot */

    function init() {
        document.querySelectorAll('[data-ss-root]').forEach(function (root) {
            if (!root.__smartSearch) root.__smartSearch = new SmartSearch(root);
        });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
    window.SmartSearch = { init: init };
})();
