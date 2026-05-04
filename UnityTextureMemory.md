# Unity Texture Memory: What the Profiler Is Actually Telling You

> **TL;DR**
> - `Allocated Size` = CPU RAM + GPU VRAM combined — it doubles when a texture is readable on both sides
> - Setting `makeNoLongerReadable: true` eliminates the CPU copy and halves texture memory cost
> - ASTC keeps textures compressed **in VRAM** — PNG is a source format, not a runtime one
> - A red GC spike + GPU stall on the same frame = double hit; trace via `Function.Invoke()` call stack

---

## Key Learnings

### `Allocated Size` = Native + Graphics — it doubles because the texture lives in two places

Unity keeps a **CPU-side copy** (Native Size, in RAM) and a **GPU-side copy** (Graphics Size, in VRAM). Allocated Size is the sum of both. That's why you see 33 MB = 16.5 MB + 16.5 MB — the texture is mirrored.

### `makeNoLongerReadable: true` is free memory savings on textures you don't modify at runtime

After uploading to GPU, if you never call `GetPixels()` / `SetPixels()`, you don't need the CPU copy. Free it:

```csharp
texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
```

Or uncheck **Read/Write Enabled** in the Texture Import Settings. This alone can halve texture memory.

### PNG is a source format — ASTC is the runtime format the GPU actually uses

PNG is decompressed to raw RGBA when loaded; the GPU can't sample it directly. **ASTC stays compressed in VRAM**, so the GPU samples it natively without decompression overhead.

```
1024×1024 RGBA:
  Uncompressed in RAM:  4.0 MB
  ASTC 6×6 in VRAM:     0.89 MB  (~4.5× smaller)
```

### ASTC block size is the quality/memory dial

| Block | Bits/px | Use |
|-------|---------|-----|
| 4×4   | 8 bpp   | UI, important sprites |
| 6×6   | 3.56 bpp | General textures |
| 8×8   | 2 bpp   | Backgrounds |
| 12×12 | 0.89 bpp | Non-critical large surfaces |

ASTC is supported on all modern iOS (A8+) and Android (OpenGL ES 3.1+) — use it as the default mobile format.

### A GC spike and a GPU stall on the same frame compound each other

When the profiler shows a red GC spike, check the call stack at that exact frame. If `Semaphore.WaitForSignal` also appears on the render thread, the CPU is stalling for the GPU *at the same time* — two independent performance hits landing together.

### `Function.Invoke() → Layout` is the common GC spike culprit

Delegate/event dispatch (C# `Action`, `UnityEvent`) and **Canvas layout rebuilds** are frequent sources of managed heap allocations. A UI element being enabled, disabled, or resized can trigger a cascading dirty hierarchy that allocates heavily.

---

## Gotchas

- Expected Allocated Size ≈ Graphics Size — got 2×. Because CPU copy exists by default unless you opt out.
- Unchecking Read/Write doesn't help textures already loaded — only affects future loads / builds.
- ASTC is lossy. For pixel-art or UI with hard edges, 4×4 is safer than 6×6.
- "Call Stacks" must be enabled in the Profiler toolbar *before* recording to get allocation callstacks on GC spikes.
- `GPU:--ms` in the profiler header means GPU timing is unavailable (common in Editor). Profile on device for real GPU data.

---

## 🔁 Recall Prompts

*Cover the answers and try to answer from memory.*

<details>
<summary><strong>Q1: Why does Allocated Size often equal exactly 2× the Graphics Size?</strong></summary>

Because Unity keeps a CPU-side copy (Native Size) and a GPU-side copy (Graphics Size) by default. Allocated Size is their sum. When both are equal, the texture is fully mirrored in RAM and VRAM.

</details>

<details>
<summary><strong>Q2: How do you eliminate the CPU copy of a texture you only read on the GPU?</strong></summary>

Call `texture.Apply(true, makeNoLongerReadable: true)` after uploading, or uncheck **Read/Write Enabled** in the Texture Import Settings. This frees the CPU-side copy after the GPU upload.

</details>

<details>
<summary><strong>Q3: What's the difference between PNG and ASTC in terms of where compression happens?</strong></summary>

PNG is decompressed to raw RGBA on the CPU when loaded — the GPU cannot sample it compressed. ASTC is a GPU-native format that stays compressed in VRAM and is sampled directly, saving both memory and bandwidth.

</details>

<details>
<summary><strong>Q4: In the profiler, where do you look to find what caused a red GC allocation spike?</strong></summary>

Click the frame with the spike in the timeline, then look at the call stack in the CPU Usage panel. You need "Call Stacks" enabled before recording. Look for `Function.Invoke()`, LINQ, string formatting, or UI Layout calls as common culprits.

</details>
