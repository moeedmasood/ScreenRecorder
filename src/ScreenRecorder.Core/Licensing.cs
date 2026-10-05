using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenRecorder.Core;

/// <summary>Features that can be gated by edition. Some are reserved for upcoming Pro functionality.</summary>
public enum Feature { Fps60, HighQuality, SeparateAudioTracks, Hevc4K }

public sealed record LicensePayload(string Licensee, string Edition, DateTime IssuedUtc, DateTime? ExpiresUtc);

public enum LicenseStatus { Free, Valid, Expired, Invalid }

/// <summary>
/// Offline licence validation. A key is <c>base64url(payload-json).base64url(ECDSA-P256-SHA256 signature)</c>.
/// Only the public key ships with the app; the private key stays with the vendor (see tools/ScreenRecorder.LicenseTool).
/// </summary>
public sealed class LicenseService
{
    private readonly string _publicKeyBase64;
    private readonly string _path;
    private readonly Func<DateTime> _now;

    public LicensePayload? Current { get; private set; }
    public LicenseStatus Status { get; private set; } = LicenseStatus.Free;

    public LicenseService(string publicKeySpkiBase64, string? path = null, Func<DateTime>? utcNow = null)
    {
        _publicKeyBase64 = publicKeySpkiBase64;
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenRecorder", "license.key");
        _now = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool IsPro => Status == LicenseStatus.Valid;

    /// <summary>All gated features currently require Pro; the parameter keeps call sites ready for per-feature rules.</summary>
    public bool Has(Feature feature) => IsPro;

    /// <summary>Loads the saved key, if any, and updates <see cref="Status"/>.</summary>
    public void Load()
    {
        Current = null; Status = LicenseStatus.Free;
        try { if (File.Exists(_path)) Apply(File.ReadAllText(_path).Trim()); }
        catch { Status = LicenseStatus.Invalid; }
    }

    /// <summary>Validates and saves a key. Returns the resulting status; nothing is saved unless it is valid.</summary>
    public LicenseStatus Activate(string key)
    {
        var status = Apply(key.Trim());
        if (status == LicenseStatus.Valid)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, key.Trim());
        }
        return status;
    }

    public void Deactivate()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        Current = null; Status = LicenseStatus.Free;
    }

    private LicenseStatus Apply(string key)
    {
        Current = null;
        Status = Verify(key, out var payload);
        if (Status is LicenseStatus.Valid or LicenseStatus.Expired) Current = payload;
        return Status;
    }

    private LicenseStatus Verify(string key, out LicensePayload? payload)
    {
        payload = null;
        var parts = key.Split('.');
        if (parts.Length != 2) return LicenseStatus.Invalid;
        try
        {
            byte[] data = FromB64Url(parts[0]), sig = FromB64Url(parts[1]);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(_publicKeyBase64), out _);
            if (!ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256)) return LicenseStatus.Invalid;
            payload = JsonSerializer.Deserialize<LicensePayload>(data);
            if (payload is null || !string.Equals(payload.Edition, "pro", StringComparison.OrdinalIgnoreCase)) return LicenseStatus.Invalid;
            return payload.ExpiresUtc is { } exp && exp < _now() ? LicenseStatus.Expired : LicenseStatus.Valid;
        }
        catch { return LicenseStatus.Invalid; }
    }

    // ---- Vendor-side helpers (used by the licence tool and tests) ----
    public static string Issue(ECDsa privateKey, LicensePayload payload)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload);
        return ToB64Url(data) + "." + ToB64Url(privateKey.SignData(data, HashAlgorithmName.SHA256));
    }

    private static string ToB64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
