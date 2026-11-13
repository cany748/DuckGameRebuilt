# -perftest results

Base commit `13f2a2a7`. Every optimization below reproduces the baseline `VERIFY` lines (world, sound
and picture hashes) on x64.

## x64 (CoreCLR, llvmpipe) — behaviour check and allocation profile only

Frame times on this target are dominated by software GL and do not transfer to the console.

| scene | build | upd ms | drw ms | gen0 GCs | allocated MB (1800 frames) |
|---|---|---|---|---|---|
| control | baseline | 0.23 | 1.09 | 14 | 177 |
| control | optimized | 0.24 | 1.15 | 4 | 42 |
| effects | baseline | 0.64 | 1.45 | 56 | 673 |
| effects | optimized | 0.66 | 1.41 | 8 | 90 |

Optimizations so far:

- `perf: reuse sprite batch items and depth lists between frames` — 394 MB of `MTSpriteBatchItem` per
  effects run gone.
- `perf: clip lines against boxes without temporary arrays` — 116 MB of `float[]` per effects run gone.
- `perf: skip rebuilding a thing's collision cells when they did not change`.

## Ryujinx (THROTTLE=40) — pending

Not measured yet: the Switch build needs Docker, and the Docker Desktop engine did not answer during
this session. Heavy custom maps (`-perfscan`), the baseline and the 2x criterion are decided on this
target.
