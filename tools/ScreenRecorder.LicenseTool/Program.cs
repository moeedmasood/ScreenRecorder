using System.Security.Cryptography;
using ScreenRecorder.Core;

// Vendor-side tool. Keep the private key OFF the repository and out of the shipped app.
//   LicenseTool genkey <private.pem>              -> writes the private key, prints the public key (SPKI base64)
//   LicenseTool issue <private.pem> <licensee> [expiresYyyyMmDd]  -> prints a licence key
if (args.Length >= 2 && args[0] == "genkey")
{
    using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(args[1], k.ExportPkcs8PrivateKeyPem());
    Console.WriteLine("Public key (embed in ProductKeys.cs):");
    Console.WriteLine(Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
}
else if (args.Length >= 3 && args[0] == "issue")
{
    using var k = ECDsa.Create();
    k.ImportFromPem(File.ReadAllText(args[1]));
    DateTime? exp = args.Length > 3 ? DateTime.ParseExact(args[3], "yyyyMMdd", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal) : null;
    Console.WriteLine(LicenseService.Issue(k, new LicensePayload(args[2], "pro", DateTime.UtcNow, exp)));
}
else Console.WriteLine("Usage: genkey <private.pem> | issue <private.pem> <licensee> [expiresYyyyMmDd]");
