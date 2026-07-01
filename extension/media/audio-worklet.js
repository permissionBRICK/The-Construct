"use strict";
// AudioWorklet processor for Construct mic passthrough.
//
// Converts the mic's native float32 mono stream (whatever the hardware sample rate
// is — commonly 44.1/48 kHz) to the recorder contract: raw PCM, signed 16-bit
// little-endian, MONO, resampled to 16 kHz. It posts ArrayBuffers of S16LE bytes
// back to the main thread (audio-capture.js), which relays them to the extension.
//
// WHY a worklet — it runs on the realtime audio thread, so downsampling happens
// without main-thread jank and without buffering the whole stream. We do a simple
// linear-interpolation resample (good enough for speech; Claude's own recorder path
// is not high-fidelity either) with a fractional read position carried across
// render quanta so there are no discontinuities at block boundaries.

const TARGET_RATE = 16000;

class ConstructDownsampler extends AudioWorkletProcessor {
  constructor() {
    super();
    // Fractional source-sample position we've consumed up to, carried across the
    // 128-frame render quanta so resampling is continuous (no clicks at boundaries).
    this._pos = 0;
    // Ratio of source frames per output frame (e.g. 48000/16000 = 3).
    this._ratio = sampleRate / TARGET_RATE; // `sampleRate` is a global in worklet scope
    // Tail of the previous block so linear interpolation can look back one sample
    // across the boundary.
    this._prev = 0;
    this._active = true;
    this.port.onmessage = (e) => {
      if (e && e.data === "stop") this._active = false;
    };
  }

  process(inputs) {
    if (!this._active) return false; // returning false ends the processor
    const input = inputs[0];
    if (!input || input.length === 0 || !input[0]) return true; // no mic frames yet
    const chan = input[0]; // mono — first channel only (getUserMedia opened with channelCount:1)
    const n = chan.length; // 128 frames per quantum
    if (n === 0) return true;

    // Produce output frames whose (fractional) source position falls within this
    // block. `this._pos` is relative to the start of the current block.
    const out = [];
    let pos = this._pos;
    while (pos < n) {
      const i = Math.floor(pos);
      const frac = pos - i;
      // Sample i-1 is either in this block or the carried-over previous tail.
      const a = i <= 0 ? this._prev : chan[i - 1];
      const b = chan[i] !== undefined ? chan[i] : a;
      let s = a + (b - a) * frac; // linear interpolation
      // Clamp to [-1,1] then scale to signed 16-bit.
      if (s > 1) s = 1; else if (s < -1) s = -1;
      out.push(s < 0 ? s * 0x8000 : s * 0x7fff);
      pos += this._ratio;
    }
    // Carry the fractional remainder into the next block and remember the last input
    // sample for the look-back.
    this._pos = pos - n;
    this._prev = chan[n - 1];

    if (out.length > 0) {
      const buf = new ArrayBuffer(out.length * 2);
      const view = new DataView(buf);
      for (let k = 0; k < out.length; k++) {
        // little-endian signed 16-bit
        view.setInt16(k * 2, out[k] | 0, true);
      }
      // Transfer the buffer (zero-copy) to the main thread.
      this.port.postMessage(buf, [buf]);
    }
    return true;
  }
}

registerProcessor("construct-downsampler", ConstructDownsampler);
