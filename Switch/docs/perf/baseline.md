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

- `perf: sort sprite depths with a plain float sort instead of List<float>.Sort` — not yet measured
  on the emulator.

## Ryujinx (THROTTLE=40, cgroup v2 thread quotas)

World and sound hashes equal the x64 ones, so one reference serves both targets.

| scene | build | upd ms | drw ms | sum mean / p95 | over 16.7 ms | GL draws per frame | gen0 | MB |
|---|---|---|---|---|---|---|---|---|
| control | first three optimizations | 3.68 | 10.75 | 14.43 / 22.83 | 168 | 6.1 | 3 | 168 |
| effects | first three optimizations | 14.92 | 25.43 | 40.36 / 57.39 | 1780 | 42.9 | 13 | 420 |

The baseline build (test code without the optimizations) has not been run on this target yet, and the
custom maps have not been scanned.

Profile of `effects` (main thread, 28201 samples, leaf functions):

- about 15% in system calls made by the GPU driver and the present path (`nvIoctl`, binder); the
  emulator services these on the host, so their weight on the console is unknown;
- about 10% in `List<float>.Sort()` of sprite depths running through shared generic code (gsharedvt
  wrappers, `GenericArraySortHelper<T_GSHAREDVT>`);
- about 10% in the collision grid (`Dictionary<Vec2, ...>.FindValue`, `GetThings` iterators,
  `CollisionRectAllDan`);
- the rest is spread thin; no other single entry is above 2%.
