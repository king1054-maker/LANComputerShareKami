using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanBridge.Core;

public sealed class Identity : IDisposable
{
    public string DataDirectory { get; }
    public string Name { get; }
    public X509Certificate2 Certificate { get; }
    public string Id => Fingerprint(Certificate);
    private readonly object gate = new();
    private readonly Dictionary<string, string> trusted;
    private readonly FileStream instanceLock;
    public Identity(string dataDirectory, string? name = null)
    {
        DataDirectory = System.IO.Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        instanceLock = new FileStream(System.IO.Path.Combine(DataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Name = name ?? Environment.MachineName;
        var pfx = System.IO.Path.Combine(DataDirectory, "identity.pfx");
        if (!File.Exists(pfx))
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=LanBridge", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(pfx, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        // Schannel requires a persisted key container. Default UserKeySet lifetime is tied to the certificate,
        // avoiding permanent per-launch key-container leaks caused by PersistKeySet.
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        Certificate = X509CertificateLoader.LoadPkcs12FromFile(pfx, null, flags);
        var trustPath = System.IO.Path.Combine(DataDirectory, "trusted.json");
        trusted = File.Exists(trustPath) ? JsonFiles.Read<Dictionary<string, string>>(trustPath) : new();
    }
    public static string Fingerprint(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
    public static string ShortCode(string id) => id.Length < 16 ? id : string.Join("-", Enumerable.Range(0, 4).Select(i => id.Substring(i * 4, 4)));
    public bool IsTrusted(string id) { lock (gate) return trusted.ContainsKey(id); }
    public Dictionary<string, string> Trusted() { lock (gate) return new(trusted); }
    public void Trust(string id, string name)
    {
        lock (gate) { trusted[id] = name; Save(); }
    }
    public void Revoke(string id) { lock (gate) { trusted.Remove(id); Save(); } }
    private void Save() => JsonFiles.Write(System.IO.Path.Combine(DataDirectory, "trusted.json"), trusted);
    public void Log(string kind, object data)
    {
        lock (gate)
        {
            var log = System.IO.Path.Combine(DataDirectory, "events.jsonl");
            if (File.Exists(log) && new FileInfo(log).Length > 8_000_000) File.Move(log, log + ".old", true);
            File.AppendAllText(log, System.Text.Json.JsonSerializer.Serialize(new { at = DateTime.UtcNow, kind, data }) + Environment.NewLine);
        }
    }
    public void Dispose() { Certificate.Dispose(); instanceLock.Dispose(); }
}
