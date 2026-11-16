using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Predict.Keyboard;

/// <summary>
/// espeak-ng (libespeak-ng.dll, built by Data.Build/build_espeak.ps1 at the commit piper1-gpl 1.8.0 uses) as Piper's
/// espeakbridge.c calls it: text → IPA phonemes clause by clause, with the clause terminator. espeak is not thread-safe
/// and keeps global state: every call takes one process-wide lock (in the app: the <c>piper</c> thread, a <c>piper-load</c> thread while a voice loads, a session rebuild).
/// </summary>
internal static class EspeakNative
{
    public const string ExpectedVersion = "1.52.0.1";
    private const string Dll = "libespeak-ng";
    private const int AudioOutputSynchronous = 2;
    /// <summary>espeakINITIALIZE_DONT_EXIT: without it a data error calls exit() and kills the app (Piper passes 0).</summary>
    private const int InitializeDontExit = 0x8000;
    private const int CharsAuto = 0, PhonemesIpa = 0x02;

    /// <summary>The data files the French voice needs (the staged subset); checked before espeak sees the folder.</summary>
    public static readonly string[] RequiredData =
        { "phontab", "phonindex", "phondata", "intonations", "fr_dict", "en_dict", "lang/roa/fr", "lang/gmw/en" };

    private static readonly object Lock = new();
    private static bool _initialized;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int espeak_Initialize(int output, int buflength, string? path, int options);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int espeak_SetVoiceByName(string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr espeak_TextToPhonemesWithTerminator(ref IntPtr textptr, int textmode, int phonememode, out int terminator);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr espeak_Info(IntPtr pathData);

    /// <summary>Initialize once with <paramref name="dataDir"/> (the <c>espeak-ng-data</c> folder). Throws (never exits)
    /// when a data file is missing, the library fails, or its version is not <see cref="ExpectedVersion"/>.</summary>
    public static string Initialize(string dataDir)
    {
        lock (Lock)
        {
            foreach (var f in RequiredData)
                if (!File.Exists(Path.Combine(dataDir, f.Replace('/', Path.DirectorySeparatorChar))))
                    throw new FileNotFoundException($"espeak-ng data file missing: {f}");
            if (_initialized) return Version();
            // espeak opens its files with the ANSI API: a folder with non-ASCII characters in its name is passed as its
            // 8.3 short name
            var path = AnsiSafe(dataDir);
            int rate = espeak_Initialize(AudioOutputSynchronous, 0, path, InitializeDontExit);
            if (rate <= 0) throw new InvalidOperationException($"espeak_Initialize failed ({rate})");
            var v = Version();
            if (v != ExpectedVersion) throw new InvalidOperationException($"espeak-ng {v}, expected {ExpectedVersion}");
            _initialized = true;
            return v;
        }
    }

    public static string Version()
    {
        lock (Lock) return Marshal.PtrToStringUTF8(espeak_Info(IntPtr.Zero)) ?? "?";
    }

    public static void SetVoice(string name)
    {
        lock (Lock)
        {
            int r = espeak_SetVoiceByName(name);
            if (r != 0) throw new InvalidOperationException($"espeak_SetVoiceByName({name}) failed ({r})");
        }
    }

    /// <summary>espeakbridge.c <c>get_phonemes</c>: every clause of <paramref name="text"/> (UTF-8, null-terminated in
    /// unmanaged memory; espeak advances the pointer and sets it to null at the end).</summary>
    public static List<EspeakClause> Clauses(string text, string voice)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var buf = Marshal.AllocHGlobal(bytes.Length + 1);
        try
        {
            Marshal.Copy(bytes, 0, buf, bytes.Length);
            Marshal.WriteByte(buf, bytes.Length, 0);
            var o = new List<EspeakClause>();
            lock (Lock)
            {
                int r = espeak_SetVoiceByName(voice);
                if (r != 0) throw new InvalidOperationException($"espeak_SetVoiceByName({voice}) failed ({r})");
                IntPtr p = buf;
                int guard = 0;
                while (p != IntPtr.Zero)
                {
                    var ph = espeak_TextToPhonemesWithTerminator(ref p, CharsAuto, PhonemesIpa, out int term);
                    o.Add(PiperPhonemizer.Clause(Marshal.PtrToStringUTF8(ph) ?? "", term));
                    if (++guard > bytes.Length + 16) throw new InvalidOperationException("espeak did not advance");
                }
            }
            return o;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string AnsiSafe(string path)
    {
        bool ascii = true;
        foreach (char c in path) if (c > 0x7E) { ascii = false; break; }
        if (ascii) return path;
        var sb = new StringBuilder(1024);
        uint n = GetShortPathNameW(path, sb, (uint)sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint cch);
}
