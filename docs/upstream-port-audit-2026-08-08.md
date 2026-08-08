# Upstream port audit — firmware branch

Date: 2026-08-08
Target: Touche PX5, branch `codex/firmware-support`
Baseline before this batch: `b6d4c92de7fb7843a1f57cdb368672e30a4e2da0`

## Sources and method

- SharpEmu official: <https://github.com/sharpemu/sharpemu>, snapshot
  `62c3852556401883b31abf1623b3b48572b65e0b`.
- Kyty official: <https://github.com/InoriRus/Kyty>, snapshot
  `4733b7e1c91b10554a52007903d74dc76c39a230`.
- Local symbol catalog: `scripts/ps5_names.txt`.
- Auxiliary NID evidence: `F:/Downloads/nids.csv`.

The Touche branch and SharpEmu official have diverged substantially: 150 local
commits versus 63 upstream commits since merge-base
`b4cc5f88ca1d01e7a37249438b5fb6093816b96e`. A synthetic whole-tree merge
conflicts in AGC, Vulkan, audio, GUI and other customized areas. Therefore,
changes are ported individually and tested; a wholesale merge is rejected.

## Ported in this batch

| Source | Change | Touche adaptation | Risk control |
|---|---|---|---|
| SharpEmu `c086e32` | Release pooled AGC evaluator arrays when no GPU dispatch consumes them | Preserved Touche's CPU-evaluation path | Existing AGC suite |
| SharpEmu `26bda04` | Guard overflow in Vulkan guest-buffer ranges | Falls back to a transient buffer or skips an invalid padded range | Existing Vulkan tests/build |
| SharpEmu `9e10d7c` | `sceKernelIsTrinityMode`, `sceKernelGetOpenPsId`, `sceNpTrophy2GetTrophyInfoArray` | OpenPsId output is explicitly zeroed; trophy call fails closed | New identity/trophy tests |
| SharpEmu `544f588` (partial) | `sceKernelClearVirtualRangeName` | Uses Touche's mapped-region/name state and return convention | Build/export validation |
| SharpEmu `b572738` | Correct `scePadGetTriggerEffectState` NID ownership | Removed the incorrect UserService alias; writes only the observed 8-byte state | Guard-byte and invalid-handle tests |
| SharpEmu `79aa764` | Avoid redundant Metal command-buffer waits | Central helper retains the wait unless status is already completed | Build plus unchanged writeback ordering |
| SharpEmu `12432f8` | Resolve reused pthread mutex slots through their current handle | Preserves Touche's mutex semantics while replacing stale cached aliases | New direct and recursive alias tests |
| Kyty Gen5 AGC reference + NID catalog | `sceAgcDcbSetShRegisterDirect` and size export | Reuses Touche's generic PM4 direct-register builder; no Kyty source copied | New exact packet/size tests |
| Castlevania regression analysis | Stable NGS2 streaming snapshots, persistent PCM stride and output limiter | Keeps the recovered gameplay stream while rejecting torn transition buffers and clipping | Five focused NGS2 tests plus live regression logs |

## Already present under Touche implementations

These upstream patches must not be cherry-picked again. Patch IDs, range-diff,
or direct code inspection show equivalent or superseding behavior:

- `996de70` game compatibility (`be0707b` locally).
- `3f9bd2b` AGC command-buffer branches (`8a8d237`).
- `82c2c7f` guest CPU image writes (`f8761c4`).
- `cf3bd0b` opcode `0x8A` handling (`80b5234`).
- `f3d9439` Vulkan presentation synchronization (`e0f8d90`).
- `f36ce40` Windows allocation/mutex fixes (`c4c2645`, with different Touche
  pthread semantics).
- `4b5ea6a` DCC fast clears (`653ddb1`).
- `444af50` / `e7149bf` shader CFG/SSA (`477338c`; equivalent patch ID).
- `532251c` CPU import/memory fast paths (`d58c919`).
- `539baa6`, `e5e02c`, `fc5b6ba`, `0dd5433`, `e1695cf`: SDWA,
  `V_BFE_I32`, format sizing, BC compression and sRGB/UNORM alias behavior.
- `531e35b`, `ea9be74`, `c4ae4a2`: compute completion events, GS/rect-list
  handling and non-blocking timed-out Vulkan fences.
- `8df4039`: equivalent host-path case handling is already in Touche's kernel
  path layer.

## Candidates for isolated follow-up batches

| Priority | Upstream change | Why it is not mixed into this batch |
|---|---|---|
| High | `62c3852` AGC arena/fence recovery | Its memory-identity change cannot be split from queue ownership in Touche: a partial port allowed a worker to resume another wrapper's per-thread `SubmittedGpuState`. It was removed after a Castlevania audio regression report. Port only with shared-state ownership and dedicated stress/replay tests. |
| High | `816ec4a` kernel equeue waiter lifetime | Concurrency-sensitive and large. Requires targeted multi-waiter, delete and timeout tests. |
| High | `93c9f14` AMPR cooked-index preload and FD LRU | Valuable for firmware assets, but changes caching/lifetime behavior and needs real package fixtures. |
| Medium | `eb0653e` vertex stream views | Must be reconciled with Touche's newer vertex metadata and shader paths. |
| Medium | `97bd8c4` combined runtime fixes | Split by subsystem first; never port as one opaque commit. |
| Medium | `544f588` JSON `referValue` portion | Touche has two JSON shadow-state paths; needs unification before importing this behavior. |
| Medium | `5864328` unified FFmpeg bridge | Broad media architecture change; validate against Touche's current codecs and deployment layout. |
| Low | `418eb7e`, `ec65419` diagnostics | Useful counters only; no compatibility blocker. |
| Optional | `8eb2c1e`, `b75e4e0` | Developer RenderDoc integration and opt-in Metal drawable cap; platform/policy features. |
| Platform | `2b6bd5a`, `f095ed6`, `ddcd285` | SDL/AJM/AT9 work needs a separate platform and licensing audit. |

## Intentionally excluded

- GUI, theme, localization and window-layout commits: `6994538`, `b07e4f2`,
  `b21dd9f`, `882a6c0`, `faf49f6`, `77f2297`, `f08308d`, `753ddf9`,
  `a7ec3d5`, `7c9740f`, `c387b96`.
- SharpEmu branding/assets: `02938b5`, `5ee7cd1`, `6b8f11a`, `207441c`.
- SharpEmu versions/release automation: `92e3abe`, `a3130e3`, `d5108e8`,
  `da0de5c`, `7b7a48a`.
- Merge-only commits: `b020f16`, `c990b77`.

Those changes either conflict with Touche PX5 identity/workflows or bring no
firmware/runtime benefit.

## Kyty assessment

Kyty is MIT-licensed but its last commit is from 2022-10-03 and the repository
describes itself as early-stage. Its tree contains extensive unimplemented
paths, especially audio, network and multi-user support. Touche's current SELF,
LLE/firmware, Vulkan/Metal, shader and detile systems are substantially newer.

Useful Kyty material is limited to independent corroboration and small fixtures:

- Gen5 AGC PM4 packet shapes and NID coverage.
- Tiling/swizzle equations as cross-check vectors, not replacement code.
- SELF/ELF dynamic-tag interpretation as parser test cases.
- Stub return behavior where verified by another source.

Do not import Kyty's loader, renderer, networking/audio stubs or Gen4 GNM layer
wholesale. Any substantial code reuse must preserve its MIT copyright/license
notice; this batch only reuses Touche code and independently implements a
three-dword packet confirmed by the NID catalog.

## Local branch audit

The remaining local-only commits were inspected individually after the firmware
changes were consolidated. None should be merged into this branch now:

- `castlevania-flip-fix` (`2e85f97`, `785f12a`): its four boot exports already
  exist under maintained implementations. Its older direct flip-memory capture
  predates Touche's ordered flip snapshots, surface lineage and tracked
  `cpu-write-drain` uploads; importing it would duplicate ownership and bypass
  the current synchronization model.
- `my-changes` (`545e6f6`): an 8,000-line monolithic WIP snapshot. Its useful
  Touche branding, exception, guest-write and runtime foundations have since
  been superseded by the current firmware architecture. It is not safe or
  meaningful to cherry-pick as one patch.
- `perf/demons-souls-ccd-affinity` (seven commits): intentionally remains an
  isolated hardware-affinity experiment. It includes an explicitly negative SMT
  sibling result and is being developed separately, so no part is imported into
  firmware without its own benchmark and regression gate.

## Validation gate

Before this batch is committed:

1. `git diff --check` must be clean.
2. All `SharpEmu.Libs.Tests` must pass.
3. The complete solution must build in Release.
4. No source files outside `codex/firmware-support` are staged or committed.

Results on 2026-08-08:

- `git diff --check`: clean.
- `dotnet build TouchePx5.slnx --no-restore --configuration Release`:
  successful, 0 warnings and 0 errors.
- `dotnet test TouchePx5.slnx --no-build --no-restore --configuration Release`:
  1,169 passed, 0 failed, 0 skipped after removing the unsafe partial AGC
  memory-identity port and adding the Castlevania NGS2 transition safeguards.
- Python aerolib catalog suite: 5 passed.
- Python debugger frontend suite: 9 passed; 2 existing Windows-specific
  harness failures (extensionless shebang executable launch and same-process
  HTTP port replacement). This batch does not modify the debugger frontend.
