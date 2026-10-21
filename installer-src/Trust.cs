using System;
using System.Security.Cryptography;
using System.Text;

// Who may update the mod. An update is only used when its description (version, fingerprint, download address) carries a signature made with the author's private key,
// which exists only on the author's PC. Somebody who breaks into the GitHub account can change files there, but cannot make a signature the launcher accepts.
// The key below is the PUBLIC half (it can only check signatures, not make them).
static partial class Program
{
    static string UpdatePublicKeyXml =
        "<RSAKeyValue><Modulus>weTKb20tFHWfY7bKNZNilE+niF52PEbVxmcgUdnzSPT9gc3Ppv17Xlq0LYUXil9BvYKbCXvkpNdEZggsYqrfewdnwzSHvRuwDHFrk24jmfC8jH11u9/y3ogoh8W5PXJW/VUW4G1hL3mNdeWULkkFegLOM8PPxyZGs7KZ7Sbb/g13vn4P/CPioBj7BdLN5Jc6AhnFK7tlLv+96ET10Ey3Z8/jwH8WuE/SNZKUaw5wC+JjBi1JMBvmlFZLF6g+X0aIF4rBqFbCmvWNUPk7EtglpDQ5Jzh41cFTJ+Am2ssbogKNGTZUPMwC83197l7b1bzNFVti90b/C41151YLKtjKA44c9XXsg53XOEksyTg7Eut11pln+JXo+3bek2HvYpdJ9PDysmx/a+xuz3G2hX97ulWXQjvXQc4nsJNk9/a1I/ZVU9IpqHjk9OTvcS4XMsCNvyQkjwEuRRewroUjp/zfAygvJV9+LcXQZL8aEHnuWspPxfniLSLd7L0s+X9lxKZB</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    // exactly what is signed: the fields that decide what gets installed, in a fixed form
    static byte[] ManifestBytes(int version, string sha256, string url)
    {
        return Encoding.UTF8.GetBytes("growhome-coop-manifest-v1\n" + version + "\n" + (sha256 ?? "").ToLowerInvariant() + "\n" + (url ?? ""));
    }

    static bool VerifyManifest(int version, string sha256, string url, string signatureBase64)
    {
        try
        {
            if (string.IsNullOrEmpty(signatureBase64)) return false;
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.FromXmlString(UpdatePublicKeyXml);
                return rsa.VerifyData(ManifestBytes(version, sha256, url), "SHA256", Convert.FromBase64String(signatureBase64));
            }
        }
        catch { return false; }
    }

    // Only used by the audit (with a throw-away key) to make signed test manifests; the real private key is never in this program.
    static string SignManifestForTest(string privateKeyXml, int version, string sha256, string url)
    {
        using (var rsa = new RSACryptoServiceProvider())
        {
            rsa.FromXmlString(privateKeyXml);
            return Convert.ToBase64String(rsa.SignData(ManifestBytes(version, sha256, url), "SHA256"));
        }
    }

    // Updates may only be fetched over https from GitHub (the audit also allows its own server on this PC).
    static readonly string[] UpdateHosts = { "raw.githubusercontent.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    static bool UrlAllowed(string url)
    {
        Uri u;
        if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
        if (auditMode && u.Scheme == Uri.UriSchemeHttp && u.Host == "127.0.0.1") return true;
        if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo)) return false;
        foreach (var h in UpdateHosts) if (string.Equals(u.Host, h, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
