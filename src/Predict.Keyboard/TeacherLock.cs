using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

/// <summary>Why a new PIN is refused.</summary>
internal enum PinProblem { Length, NotDigits, Weak, Mismatch }

internal enum PinOutcome { Ok, Wrong, Wait, NoPin }

/// <param name="Failures">failed attempts in a row (after this one).</param>
/// <param name="WaitSeconds">seconds before the next attempt may be checked (0 = now).</param>
internal readonly record struct PinResult(PinOutcome Outcome, int Failures, int WaitSeconds, long Ms = 0);

/// <summary>
/// The teacher PIN (plan 09 step 7, D29; supersedes D8's 4–8 digit lock): 4–6 digits, PBKDF2-HMAC-SHA256 with a 16-byte
/// salt and the iteration count stored with each hash (<see cref="Iterations"/>, tuned to ≤ 600 ms on the test VM),
/// compared with <c>CryptographicOperations.FixedTimeEquals</c>. Back-off: attempts 1–4 free, the 5th failure → 30 s,
/// each further one doubles the wait up to 15 min; persisted in <c>teacher.json</c> (a restart does not reset it), clock
/// injected. A recovery code <c>XXXX-XXXX-XXXX</c> (shown once, to print) is hashed the same way; using it lets the
/// teacher set a new PIN (and gives a new code). Logs: <c>pin: verified / wrong (n) / wait N s / changed / removed</c> —
/// never a digit of the PIN or of the code. Child-proofing, not a security boundary (D29). Thread-safe (the dialog
/// verifies on a worker thread: PBKDF2 takes a few hundred ms).
/// </summary>
internal sealed class TeacherLock
{
    public const string Alg = "pbkdf2-sha256";
    public const int MinDigits = 4, MaxDigits = 6, FreeFailures = 4, FirstWaitSeconds = 30, MaxWaitSeconds = 15 * 60;
    public const int SaltBytes = 16, HashBytes = 32;
    /// <summary>Iterations for new hashes. OWASP (2023) says 600 000 for PBKDF2-HMAC-SHA256; plan 09 step 15 measured
    /// the cost on the test VM (keyboard-design §6c).</summary>
    public static int Iterations { get; set; } = DefaultIterations;
    public const int DefaultIterations = 600_000;
    private const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I

    private readonly JsonFileStore _file;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string>? _log;
    private readonly object _lock = new();

    public TeacherLock(JsonFileStore file, Func<DateTime>? utcNow, Action<string>? log)
    {
        _file = file;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log;
    }

    public bool HasPin => Pin() != null;

    public DateTime? SetAtUtc => _file.Read(r => r["setAtUtc"] is JsonValue v && v.TryGetValue<string>(out var s) &&
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null);

    private PinHash? Pin() => _file.Read(r => ParseHash(r["pin"]));

    // ------------------------------------------------------------------ rules (pure)
    /// <summary>Null when <paramref name="pin"/> may become the PIN: 4–6 ASCII digits, not in the short weak list.</summary>
    public static PinProblem? Check(string? pin)
    {
        if (pin is null || pin.Length < MinDigits || pin.Length > MaxDigits) return PinProblem.Length;
        if (!pin.All(c => c is >= '0' and <= '9')) return PinProblem.NotDigits;
        if (IsWeak(pin)) return PinProblem.Weak;
        return null;
    }

    /// <summary>One digit repeated (0000), a run up or down (1234, 987654), two digits alternating (1212), and a few
    /// codes everybody tries (2580: the middle column of a keypad, 1004, 2000).</summary>
    public static bool IsWeak(string pin)
    {
        if (pin.Distinct().Count() == 1) return true;
        bool up = true, down = true;
        for (int i = 1; i < pin.Length; i++)
        {
            if (pin[i] - pin[i - 1] != 1) up = false;
            if (pin[i - 1] - pin[i] != 1) down = false;
        }
        if (up || down) return true;
        if (pin.Length >= 4 && Enumerable.Range(2, pin.Length - 2).All(i => pin[i] == pin[i - 2])) return true;
        return pin is "2580" or "0852" or "1004" or "2000" or "1122" or "6969" or "112233" or "121212" or "123123";
    }

    /// <summary>Seconds to wait after <paramref name="failures"/> failures in a row: 0 for 1–4, then 30, 60, 120… ≤ 900.</summary>
    public static int WaitFor(int failures)
    {
        if (failures <= FreeFailures) return 0;
        int n = failures - FreeFailures - 1;
        return n >= 5 ? MaxWaitSeconds : Math.Min(MaxWaitSeconds, FirstWaitSeconds << n);
    }

    public static PinHash Hash(string secret, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var h = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return new PinHash(Alg, iterations, Convert.ToBase64String(salt), Convert.ToBase64String(h));
    }

    public static bool Matches(string secret, PinHash h)
    {
        if (h.Alg != Alg || h.Iterations <= 0) return false;
        byte[] salt, want;
        try { salt = Convert.FromBase64String(h.Salt); want = Convert.FromBase64String(h.Hash); }
        catch (FormatException) { return false; }
        if (want.Length == 0) return false;
        var got = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, h.Iterations, HashAlgorithmName.SHA256, want.Length);
        return CryptographicOperations.FixedTimeEquals(got, want);
    }

    /// <summary>A new recovery code: 12 characters from a 32-letter alphabet (60 bits), as <c>XXXX-XXXX-XXXX</c>.</summary>
    public static string NewRecoveryCode()
    {
        var b = RandomNumberGenerator.GetBytes(12);
        var c = b.Select(x => RecoveryAlphabet[x & 31]).ToArray();
        return $"{new string(c, 0, 4)}-{new string(c, 4, 4)}-{new string(c, 8, 4)}";
    }

    /// <summary>Upper case, without spaces and dashes. The alphabet has no 0, 1, O or I (easily confused), so a code
    /// typed with one of them is simply wrong; nothing is mapped.</summary>
    public static string NormalizeRecovery(string? code) =>
        new string((code ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    // ------------------------------------------------------------------ state
    /// <summary>Set (or change) the PIN: a new hash, a new recovery code (returned, to be shown once), failures reset.</summary>
    public string SetPin(string pin)
    {
        if (Check(pin) is { } why) throw new ArgumentException("PIN refused: " + why);
        var code = NewRecoveryCode();
        var ph = Hash(pin, Iterations);
        var rh = Hash(NormalizeRecovery(code), Iterations);
        lock (_lock)
        {
            _file.Update(r =>
            {
                r["pin"] = HashJson(ph);
                r["recovery"] = HashJson(rh);
                r["setAtUtc"] = _utcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                r["failures"] = new JsonObject { ["count"] = 0, ["untilUtc"] = null };
            });
        }
        _log?.Invoke($"pin: changed ({ph.Iterations} iterations)");
        return code;
    }

    /// <summary>A new recovery code for the current PIN (the old one stops working).</summary>
    public string NewRecovery()
    {
        var code = NewRecoveryCode();
        var rh = Hash(NormalizeRecovery(code), Iterations);
        lock (_lock) _file.Update(r => r["recovery"] = HashJson(rh));
        _log?.Invoke("pin: new recovery code");
        return code;
    }

    /// <summary>Deactivation: the PIN and the recovery code are removed (D29, Q4).</summary>
    public void Clear()
    {
        lock (_lock)
            _file.Update(r =>
            {
                r.Remove("pin");
                r.Remove("recovery");
                r["failures"] = new JsonObject { ["count"] = 0, ["untilUtc"] = null };
                r["setAtUtc"] = _utcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            });
        _log?.Invoke("pin: removed");
    }

    /// <summary>The stored hashes, for an export with « Inclure le code enseignant » (plan 09B): never the PIN itself.</summary>
    public (PinHash? Pin, PinHash? Recovery) Hashes() =>
        _file.Read(r => (ParseHash(r["pin"]), ParseHash(r["recovery"])));

    /// <summary>
    /// An imported teacher code (plan 09B, only through its own unticked line of the import): the file's PIN hash
    /// replaces this one, with its recovery-code hash (the code printed on the other PC then works here too); a file
    /// without a recovery hash keeps this PC's recovery code. Failures reset; logged as <c>pin: imported</c>.
    /// </summary>
    public void SetHashes(PinHash pin, PinHash? recovery)
    {
        if (!IsImportable(pin) || recovery != null && !IsImportable(recovery)) throw new ArgumentException("unsupported PIN hash");
        lock (_lock)
        {
            _file.Update(r =>
            {
                r["pin"] = HashJson(pin);
                if (recovery != null) r["recovery"] = HashJson(recovery);
                r["setAtUtc"] = _utcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                r["failures"] = new JsonObject { ["count"] = 0, ["untilUtc"] = null };
            });
        }
        _log?.Invoke($"pin: imported ({pin.Iterations} iterations)");
    }

    public static JsonObject ToJson(PinHash h) => HashJson(h);

    public const int MinImportIterations = 100_000, MaxImportIterations = 10_000_000, MinSaltBytes = 16, MaxSaltBytes = 64;

    /// <summary>
    /// A hash from a file (an import, plan 09B) is used only with sane parameters: our algorithm, 100 000–10 000 000
    /// iterations (fewer would make the code easy to guess offline; more would freeze every check), a 16–64-byte salt
    /// and a 32-byte hash, both valid base64. teacher.json itself is not held to this (it is our own file).
    /// </summary>
    public static bool IsImportable(PinHash h)
    {
        if (h.Alg != Alg || h.Iterations < MinImportIterations || h.Iterations > MaxImportIterations) return false;
        try
        {
            int salt = Convert.FromBase64String(h.Salt).Length;
            return salt >= MinSaltBytes && salt <= MaxSaltBytes && Convert.FromBase64String(h.Hash).Length == HashBytes;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Seconds before the next attempt is checked (0 = now).</summary>
    public int WaitRemaining()
    {
        var until = _file.Read(r => (r["failures"] as JsonObject)?["untilUtc"] is JsonValue v && v.TryGetValue<string>(out var s) &&
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null);
        if (until is not DateTime u) return 0;
        var left = (u - _utcNow()).TotalSeconds;
        return left <= 0 ? 0 : (int)Math.Ceiling(left);
    }

    public int Failures => _file.Read(r => (r["failures"] as JsonObject)?["count"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0);

    /// <summary>Check the PIN (or, <paramref name="recovery"/>, the recovery code). During a wait nothing is checked.</summary>
    public PinResult Verify(string secret, bool recovery = false)
    {
        lock (_lock)
        {
            var what = recovery ? "recovery code" : "pin";
            var h = _file.Read(r => ParseHash(r[recovery ? "recovery" : "pin"]));
            if (h is null) return new PinResult(PinOutcome.NoPin, 0, 0);
            int wait = WaitRemaining();
            if (wait > 0)
            {
                _log?.Invoke($"pin: wait {wait} s");
                return new PinResult(PinOutcome.Wait, Failures, wait);
            }
            var sw = Stopwatch.StartNew();
            bool ok = Matches(recovery ? NormalizeRecovery(secret) : secret, h);
            long ms = sw.ElapsedMilliseconds;
            if (ok)
            {
                _file.Update(r => r["failures"] = new JsonObject { ["count"] = 0, ["untilUtc"] = null });
                _log?.Invoke($"pin: verified{(recovery ? " (recovery code)" : "")}; verify {ms} ms ({h.Iterations} iterations)");
                return new PinResult(PinOutcome.Ok, 0, 0, ms);
            }
            int n = Failures + 1;
            int w = WaitFor(n);
            // full precision ("o"): the wait never ends before its time
            var until = w > 0 ? _utcNow().AddSeconds(w).ToString("o", CultureInfo.InvariantCulture) : null;
            _file.Update(r => r["failures"] = new JsonObject { ["count"] = n, ["untilUtc"] = until });
            _log?.Invoke($"pin: wrong {what} ({n}); verify {ms} ms" + (w > 0 ? $"; wait {w} s" : ""));
            return new PinResult(PinOutcome.Wrong, n, w, ms);
        }
    }

    public void Flush() => _file.Flush();

    private static JsonObject HashJson(PinHash h) =>
        new() { ["alg"] = h.Alg, ["iterations"] = h.Iterations, ["salt"] = h.Salt, ["hash"] = h.Hash };

    public static PinHash? ParseHash(JsonNode? n) =>
        n is JsonObject p && p["alg"] is JsonValue a && a.TryGetValue<string>(out var alg) &&
        p["iterations"] is JsonValue i && i.TryGetValue<int>(out var it) && it > 0 &&
        p["salt"] is JsonValue s && s.TryGetValue<string>(out var salt) && salt.Length > 0 &&
        p["hash"] is JsonValue h && h.TryGetValue<string>(out var hash) && hash.Length > 0
            ? new PinHash(alg, it, salt, hash) : null;
}
