# Shader diagnostics

Touché PX5 can emit a reproducible guest-to-SPIR-V diagnostic bundle for each
compiled shader. The feature is disabled by default and has no runtime cost
unless explicitly enabled.

```powershell
$env:TOUCHEPX5_DUMP_SHADER_DIAGNOSTICS = "1"
$env:TOUCHEPX5_SHADER_DIAGNOSTICS_DIR = "F:\diagnostics\shaders"
```

Each shader produces:

- `ADDRESS.stage.json`: guest instructions, raw and decoded image descriptors,
  expected descriptor type and expected/observed SPIR-V image operations.
- `ADDRESS.stage.spv`: the exact SPIR-V module submitted to Vulkan.

Validate a capture with the Vulkan SDK:

```powershell
python scripts/validate_shader_diagnostics.py F:\diagnostics\shaders
```

The validator locates `spirv-val` from `PATH` or `VULKAN_SDK`, validates every
module for Vulkan 1.2, and rejects mismatches such as a guest `IMAGE_LOAD`
expected to become `ImageFetch` when no `OpImageFetch` exists in the module.

For driver-level analysis, pass an emitted `.spv` to Radeon GPU Analyzer. RGA
is an optional developer tool and is not a runtime dependency of Touché PX5.

The JSON descriptor reports real extents. RDNA resource descriptors encode
width, height and depth using `extent - 1`; a palette encoded with width 255 and
height 0 therefore appears in the report as `256x1`.
