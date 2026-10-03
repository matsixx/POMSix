using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

// Decodes an AssetStudio Shader JSON dump: base64 m_CompressedBlob -> LZ4 block segments ->
// scan for DXBC containers -> D3DDisassemble (d3dcompiler_47) -> .asm text files.
class Program
{
    [DllImport("d3dcompiler_47.dll")]
    static extern int D3DDisassemble(byte[] pSrcData, IntPtr SrcDataSize, uint Flags,
        string szComments, out IntPtr ppDisassembly);

    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr ptr);

    static void Main(string[] args)
    {
        string jsonPath = args[0];
        string outDir = args[1];
        Directory.CreateDirectory(outDir);

        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;
        byte[] blob = Convert.FromBase64String(root.GetProperty("m_CompressedBlob").GetString());
        long[] comp = ReadLongs(root.GetProperty("m_CompressedLengths"));
        long[] decomp = ReadLongs(root.GetProperty("m_DecompressedLengths"));
        long[] offsets = ReadLongs(root.GetProperty("m_Offsets"));
        Console.WriteLine($"blob={blob.Length} segments={comp.Length}");

        var full = new MemoryStream();
        for (int i = 0; i < comp.Length; i++)
        {
            byte[] seg = new byte[decomp[i]];
            Lz4Decompress(blob, (int)offsets[i], (int)comp[i], seg);
            full.Write(seg, 0, seg.Length);
        }
        byte[] data = full.ToArray();
        Console.WriteLine($"decompressed={data.Length}");
        File.WriteAllBytes(Path.Combine(outDir, "blob.bin"), data);

        // Scan for DXBC containers; header dword 6 = total size.
        int found = 0;
        for (int i = 0; i + 32 < data.Length; i++)
        {
            if (data[i] != 'D' || data[i + 1] != 'X' || data[i + 2] != 'B' || data[i + 3] != 'C') continue;
            int size = BitConverter.ToInt32(data, i + 24);
            if (size < 64 || i + size > data.Length) continue;
            byte[] prog = new byte[size];
            Array.Copy(data, i, prog, 0, size);
            string name = $"prog_{found:D2}_at{i}";
            int hr = D3DDisassemble(prog, (IntPtr)prog.Length, 0, null, out IntPtr blobPtr);
            if (hr == 0)
            {
                IntPtr bufPtr = Marshal.ReadIntPtr(blobPtr, IntPtr.Size == 8 ? 8 : 4); // vtbl hack won't work; use GetBufferPointer via vtable
                // Proper COM call through vtable:
                var getPtr = GetVtblMethod<GetBufferPointerDel>(blobPtr, 3);
                var getSize = GetVtblMethod<GetBufferSizeDel>(blobPtr, 4);
                IntPtr p = getPtr(blobPtr);
                IntPtr n = getSize(blobPtr);
                byte[] txt = new byte[(int)n];
                Marshal.Copy(p, txt, 0, (int)n);
                File.WriteAllBytes(Path.Combine(outDir, name + ".asm"), txt);
                Console.WriteLine($"{name}: {size} bytes -> disassembled {(int)n} chars");
            }
            else Console.WriteLine($"{name}: {size} bytes -> disasm FAILED hr=0x{hr:X8}");
            found++;
            i += size - 1;
        }
        Console.WriteLine($"DXBC containers: {found}");
    }

    delegate IntPtr GetBufferPointerDel(IntPtr self);
    delegate IntPtr GetBufferSizeDel(IntPtr self);
    static T GetVtblMethod<T>(IntPtr comObj, int slot)
    {
        IntPtr vtbl = Marshal.ReadIntPtr(comObj);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    static long[] ReadLongs(JsonElement el)
    {
        // Arrays may be flat or nested per-platform; flatten.
        var list = new List<long>();
        Flatten(el, list);
        return list.ToArray();
    }
    static void Flatten(JsonElement el, List<long> list)
    {
        if (el.ValueKind == JsonValueKind.Array)
            foreach (var e in el.EnumerateArray()) Flatten(e, list);
        else if (el.ValueKind == JsonValueKind.Number) list.Add(el.GetInt64());
    }

    // Minimal LZ4 block decoder.
    static void Lz4Decompress(byte[] src, int srcOff, int srcLen, byte[] dst)
    {
        int s = srcOff, sEnd = srcOff + srcLen, d = 0;
        while (s < sEnd && d < dst.Length)
        {
            byte token = src[s++];
            int lit = token >> 4;
            if (lit == 15) { byte b; do { b = src[s++]; lit += b; } while (b == 255); }
            Array.Copy(src, s, dst, d, lit); s += lit; d += lit;
            if (s >= sEnd) break;
            int off = src[s] | (src[s + 1] << 8); s += 2;
            int match = (token & 15);
            if (match == 15) { byte b; do { b = src[s++]; match += b; } while (b == 255); }
            match += 4;
            int m = d - off;
            for (int i = 0; i < match; i++) dst[d + i] = dst[m + i];
            d += match;
        }
    }
}
