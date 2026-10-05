namespace ScreenRecorder.Core;

/// <summary>Build-time product configuration. Empty URLs disable the corresponding feature.</summary>
public static class ProductConfig
{
    /// <summary>ECDSA P-256 public key (SPKI, base64) used to verify licence keys.</summary>
    public const string LicensePublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEqICdhqRL/KzYIB5FCgu6BrLZP3XiwIXiET6gnMDvAAKkhQpO6XeL04SnCmiPh1Dk/ohSinTPg/iFiVnlIyLf+w==";

    /// <summary>HTTPS URL of the update manifest JSON ({"version","downloadUrl","notes","sha256"}). Set before release.</summary>
    public const string UpdateManifestUrl = "";

    /// <summary>HTTPS endpoint that accepts crash reports (opt-in only). Set before release.</summary>
    public const string CrashReportUrl = "";

    public const string PurchaseUrl = "";
}
