/*
 * AudioWorklet: converts mic audio (usually 48 kHz float) to 16 kHz mono PCM16,
 * which is what Amazon Transcribe streaming expects, and posts ~100 ms chunks.
 * Simple decimation is fine for speech; add a low-pass filter if quality suffers.
 */
class Pcm16Worklet extends AudioWorkletProcessor {
    constructor() {
        super();
        this.ratio = sampleRate / 16000;
        this.pos = 0;
        this.chunk = new Int16Array(1600);   // 100 ms at 16 kHz
        this.filled = 0;
    }

    process(inputs) {
        const channel = inputs[0] && inputs[0][0];
        if (!channel) return true;

        for (; this.pos < channel.length; this.pos += this.ratio) {
            const s = Math.max(-1, Math.min(1, channel[Math.floor(this.pos)]));
            this.chunk[this.filled++] = s < 0 ? s * 0x8000 : s * 0x7fff;

            if (this.filled === this.chunk.length) {
                const out = this.chunk.slice(0);
                this.port.postMessage(out.buffer, [out.buffer]);
                this.filled = 0;
            }
        }
        this.pos -= channel.length;
        return true;
    }
}

registerProcessor('pcm16-worklet', Pcm16Worklet);
