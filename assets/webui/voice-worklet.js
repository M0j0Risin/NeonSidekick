// NeonSidekick's Web UI, the Chat tab's microphone (2026-11-13, the webui-voice plan): the context's rate in, 16 kHz mono 16-bit PCM out,
// in 250 ms frames. One file, two homes: the AudioWorklet module (registerProcessor) and a plain script on the chat page, whose
// ScriptProcessor fallback (no audioWorklet, or a module that failed to load) uses the same resampler. The context runs at the device's
// rate, never asked for 16 kHz (iOS Safari's bugs with a non-default rate); a Bluetooth headset can bring it to 16 kHz or below, so the
// step is a general linear one, up or down. The graph low-passes at ~7 kHz before it, so nothing above 8 kHz folds into the band.
'use strict';

(function () {
  const OutRate = 16000;
  const FrameSamples = 4000;   // 250 ms at 16 kHz: 8,000 bytes a post

  // A linear resampler that keeps its fractional place across blocks: index -1 is the last block's last sample.
  class Resampler {
    constructor(inRate, emit) {
      this.step = inRate / OutRate;
      this.t = 0;
      this.prev = 0;
      this.frame = new Int16Array(FrameSamples);
      this.n = 0;
      this.emit = emit;
    }

    push(x) {
      const len = x ? x.length : 0;
      if (!len) return;   // no frames yet: nothing posted
      let t = this.t;
      while (t < len - 1) {
        const i = Math.floor(t);
        const frac = t - i;
        const a = i < 0 ? this.prev : x[i];
        const b = x[i + 1];
        const v = a + (b - a) * frac;
        this.frame[this.n++] = v >= 1 ? 32767 : v <= -1 ? -32768 : Math.round(v * 32767);
        if (this.n === FrameSamples) {
          this.emit(this.frame.buffer);
          this.frame = new Int16Array(FrameSamples);
          this.n = 0;
        }
        t += this.step;
      }
      this.t = t - len;
      this.prev = x[len - 1];
    }

    // What's left past the last whole frame (a listen ending): posted short, or nothing.
    flush() {
      if (!this.n) return;
      this.emit(this.frame.slice(0, this.n).buffer);
      this.frame = new Int16Array(FrameSamples);
      this.n = 0;
    }
  }

  if (typeof registerProcessor === 'function') {
    // The worklet: the first channel of the first input (the node mixes down to one), each frame transferred to the page.
    class NeonVoice extends AudioWorkletProcessor {
      constructor() {
        super();
        this.resampler = new Resampler(sampleRate, buffer => this.port.postMessage(buffer, [buffer]));
        this.port.onmessage = ev => { if (ev.data === 'flush') this.resampler.flush(); };
      }

      process(inputs) {
        const input = inputs[0];
        if (input && input[0]) this.resampler.push(input[0]);
        return true;
      }
    }

    registerProcessor('neon-voice', NeonVoice);
  } else {
    globalThis.NeonResampler = Resampler;
  }
})();
