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

World and sound hashes equal the x64 ones, so one reference serves both targets. The first run after
a new build is discarded (cold translation cache: update about 5x slower); the numbers are second runs.

`work` is update + draw without the wait for vsync inside the first clear of the frame; the 2x target
is `work` mean and p95 at most half of the baseline.

| scene | build | upd ms | drw ms | work mean / p95 | GL ms draw/upload/swap/clear | gen0 | MB |
|---|---|---|---|---|---|---|---|
| control | baseline (`b1e90f15`) | 3.63 | 11.13 | 6.88 / 13.74 | 0.38/0.12/1.64/7.88 | 6 | 305 |
| control | `bcdb6e7a` (6 perf commits) | 3.77 | 11.48 | 6.71 / 12.44 | 0.40/0.11/1.23/8.54 | 0 | 37 |
| effects | baseline (`b1e90f15`) | 14.83 | 25.23 | 37.00 / 49.20 | 1.42/2.87/2.41/3.05 | 33 | 1047 |
| effects | `bcdb6e7a` (6 perf commits) | 14.54 | 21.22 | 32.79 / 43.35 | 1.43/2.80/2.43/2.97 | 2 | 70 |

So far: effects x0.89, control x0.98. Allocations are down 15x, but garbage was not where the frame
time goes. The custom maps have not been scanned yet.

Profile of `effects` (main thread, 28201 samples, leaf functions):

- about 15% in system calls made by the GPU driver and the present path (`nvIoctl`, binder); the
  emulator services these on the host, so their weight on the console is unknown;
- about 10% in `List<float>.Sort()` of sprite depths running through shared generic code (gsharedvt
  wrappers, `GenericArraySortHelper<T_GSHAREDVT>`);
- about 10% in the collision grid (`Dictionary<Vec2, ...>.FindValue`, `GetThings` iterators,
  `CollisionRectAllDan`);
- the rest is spread thin; no other single entry is above 2%.
