# shaderdec — read any compiled BSG shader

Turns an AssetStudio Shader JSON dump into DXBC disassembly. Built 2026-07-19 to decode
`Custom/Vert Paint SoftCutout Decal` (Tarkov's road shader) after five rounds of guessing at its
blend math failed; the disassembly answered every open question in one pass. Reusable for **any**
Tarkov shader — POMSix, SSRSix, FogSix, CloudSix.

## Pipeline

1. **AssetStudio** (`C:\Users\lilma\Downloads\AssetStudio.net472.v0.16.47\AssetStudioGUI.exe`)
   → File → Load File → `F:\SPT4.0\EscapeFromTarkov_Data\StreamingAssets\Windows\shaders`
   (378 MB — every shader in the game lives in this one bundle; takes a minute to parse).
2. Find the shader in the Asset List (search by name), right-click → Export selected assets.
   Exports land as `Assets/Shaders/<path>/<Name>.json`.
3. Run this tool:

```sh
cd Tools/shaderdec
dotnet run -- "<path to that .json>" "out"
```

Output in `out/`: `blob.bin` (the decompressed program blob) and one `prog_NN_atOFFSET.asm` per
GPU program — typically 4 vertex + 4 fragment variants (the keyword combos, e.g.
UNITY_HDR_ON × LIGHTPROBE_SH). The largest fragment program is usually the full-featured variant.

## How it works

`m_CompressedBlob` (base64) → LZ4 block decode using `m_CompressedLengths`/`m_DecompressedLengths`/
`m_Offsets` → scan the result for `DXBC` container magics (size is dword 6 of the header) →
`D3DDisassemble` from `d3dcompiler_47.dll` via P/Invoke, calling `GetBufferPointer`/`GetBufferSize`
through the COM vtable.

## Reading the output

Render state (blend modes, ZWrite, queue, tags) is **not** in the disassembly — it's in the JSON
itself under `m_ParsedForm` → `m_SubShaders` → `m_Passes` → `m_State` (see `m_RtBlend0..7`,
`m_RtSeparateBlend`, `m_ZWrite`, `m_Tags`). Property names come from `m_NameIndices`. Read both:
the asm gives the math, the JSON gives the pipeline setup.
