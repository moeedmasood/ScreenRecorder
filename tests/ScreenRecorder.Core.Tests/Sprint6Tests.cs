using System.Security.Cryptography;
using ScreenRecorder.Core;

namespace ScreenRecorder.Core.Tests;

public class LicensingTests
{
    private static (ECDsa key, string pub) NewKey()
    {
        var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (k, Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), "lic_" + Guid.NewGuid().ToString("N") + ".key");

    [Fact]
    public void No_key_is_free_tier()
    {
        var (_, pub) = NewKey();
        var s = new LicenseService(pub, Temp()); s.Load();
        Assert.Equal(LicenseStatus.Free, s.Status);
        Assert.False(s.Has(Feature.Fps60));
    }

    [Fact]
    public void Valid_key_unlocks_pro_and_persists()
    {
        var (k, pub) = NewKey(); var path = Temp();
        var key = LicenseService.Issue(k, new LicensePayload("a@b.com", "pro", DateTime.UtcNow, null));
        var s = new LicenseService(pub, path);
        Assert.Equal(LicenseStatus.Valid, s.Activate(key));
        Assert.True(s.Has(Feature.Fps60));
        var again = new LicenseService(pub, path); again.Load();
        Assert.True(again.IsPro);
        Assert.Equal("a@b.com", again.Current!.Licensee);
        File.Delete(path);
    }

    [Fact]
    public void Tampered_or_foreign_keys_are_rejected_and_not_saved()
    {
        var (k, pub) = NewKey(); var (other, _) = NewKey(); var path = Temp();
        var good = LicenseService.Issue(k, new LicensePayload("a", "pro", DateTime.UtcNow, null));
        var foreign = LicenseService.Issue(other, new LicensePayload("a", "pro", DateTime.UtcNow, null));
        var s = new LicenseService(pub, path);
        Assert.Equal(LicenseStatus.Invalid, s.Activate(foreign));
        Assert.Equal(LicenseStatus.Invalid, s.Activate("x" + good));
        Assert.Equal(LicenseStatus.Invalid, s.Activate("garbage"));
        Assert.Equal(LicenseStatus.Invalid, s.Activate(good.Split('.')[0] + "." + good.Split('.')[0]));
        Assert.False(File.Exists(path));
        Assert.False(s.IsPro);
    }

    [Fact]
    public void Expired_key_is_not_pro()
    {
        var (k, pub) = NewKey();
        var key = LicenseService.Issue(k, new LicensePayload("a", "pro", DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(-1)));
        var s = new LicenseService(pub, Temp());
        Assert.Equal(LicenseStatus.Expired, s.Activate(key));
        Assert.False(s.IsPro);
    }

    [Fact]
    public void Wrong_edition_is_invalid()
    {
        var (k, pub) = NewKey();
        var key = LicenseService.Issue(k, new LicensePayload("a", "free", DateTime.UtcNow, null));
        Assert.Equal(LicenseStatus.Invalid, new LicenseService(pub, Temp()).Activate(key));
    }

    [Fact]
    public void Shipped_public_key_is_a_valid_ecdsa_key()
    {
        using var e = ECDsa.Create();
        e.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ProductConfig.LicensePublicKey), out _);
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void Update_version_comparison_and_manifest_parsing()
    {
        Assert.True(UpdateChecker.IsNewer("1.2.0", "1.1.9"));
        Assert.False(UpdateChecker.IsNewer("1.1.9", "1.1.9"));
        Assert.False(UpdateChecker.IsNewer("junk", "1.0.0"));
        var m = UpdateChecker.Parse("{\"version\":\"2.0.0\",\"downloadUrl\":\"https://x/y.exe\"}");
        Assert.Equal("2.0.0", m!.Version);
        Assert.Null(UpdateChecker.Parse("not json"));
    }

    [Fact]
    public async Task Update_check_and_crash_upload_are_disabled_without_https_endpoint()
    {
        Assert.Null(await UpdateChecker.CheckAsync("", "1.0.0"));
        Assert.Null(await UpdateChecker.CheckAsync("http://insecure/x.json", "1.0.0"));
        Assert.Equal(0, await CrashReporter.UploadPendingAsync(true, ""));
        Assert.Equal(0, await CrashReporter.UploadPendingAsync(false, "https://x"));
    }

    [Fact]
    public void Crash_report_is_written_and_logs_export_to_zip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "srlog_" + Guid.NewGuid().ToString("N"));
        Log.UseDirectory(dir);
        try
        {
            Log.Info("hello");
            var crash = CrashReporter.Write(new InvalidOperationException("boom"), "test");
            Assert.Contains("boom", File.ReadAllText(crash));
            Assert.Single(CrashReporter.Pending());
            var zip = Log.Export(Path.Combine(dir, "out.zip"));
            using var z = System.IO.Compression.ZipFile.OpenRead(zip);
            Assert.Contains(z.Entries, e => e.Name == "system.txt");
            Assert.Contains(z.Entries, e => e.Name.StartsWith("crash-"));
            Assert.Contains(z.Entries, e => e.Name.StartsWith("app-"));
        }
        finally { Log.UseDirectory(null); try { Directory.Delete(dir, true); } catch { } }
    }
}
