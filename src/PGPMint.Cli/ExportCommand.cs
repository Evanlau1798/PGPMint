using System.Security.Cryptography;
using PGPMint.Core.Crypto;
using PGPMint.Core.Storage;

namespace PGPMint.Cli;

internal static class ExportCommand
{
    internal static int Run(CommandLine options)
    {
        bool includeEncryptSubkey = !options.Has("no-encrypt-subkey");
        DateTimeOffset? expiresAtUtc = null;
        if (options.Get("expires") is { } rawExpires &&
            !OpenPgpArmorExporter.TryParseExpirationInput(rawExpires, DateTimeOffset.UtcNow, out expiresAtUtc))
        {
            throw new ArgumentException("--expires must be 'never', YYYY-MM-DD (future UTC date), Nd, or Ny.");
        }

        string directory = Program.DataDirectory(options);
        if (!options.Has("data-dir")) AppDataPaths.PreparePasswordVaultDirectory();
        using var lease = new VaultSession(directory);
        PasswordSeedProtector protector = Program.OpenVaultProtector(lease, options);
        using (protector)
        {
            var vault = new VaultStore(directory, protector);
            var record = vault.LoadMatches().SingleOrDefault(x => string.Equals(x.KeyId, options.Get("key"), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("No matching saved key.");
            byte[] seed = vault.DecryptPrivateSeed(record);
            char[] passphrase = [];
            try
            {
                passphrase = SecretInput.Read("OpenPGP export passphrase (empty for unprotected): ", options.Has("password-stdin"), allowEmpty: true);
                var exported = OpenPgpArmorExporter.ExportKeyPair(new(
                    seed,
                    Convert.FromHexString(record.PublicKeyHex),
                    record.CreationTimestamp,
                    options.Get("name")!,
                    options.Get("email"),
                    passphrase.Length == 0 ? null : new string(passphrase),
                    IncludeEncryptionSubkey: includeEncryptSubkey,
                    ExpiresAtUtc: expiresAtUtc));
                string output = Path.GetFullPath(options.Get("output")!);
                PrivateFileSystem.CreateDirectory(output);
                WritePair(exported, output);
                Console.WriteLine($"Exported {exported.KeyId} to {output}.");
            }
            finally { CryptographicOperations.ZeroMemory(seed); Array.Clear(passphrase); }
        }
        return 0;
    }

    internal static void WritePair(OpenPgpExportResult exported, string output)
    {
        string secretPath = Path.Combine(output, exported.KeyId + ".secret.asc");
        string publicPath = Path.Combine(output, exported.KeyId + ".public.asc");
        if (File.Exists(secretPath) || File.Exists(publicPath)) throw new IOException("Export files already exist; choose another output directory.");
        bool createdSecret = false, createdPublic = false;
        try
        {
            using var secret = PrivateFileSystem.CreateNew(secretPath);
            createdSecret = true;
            using var pub = PrivateFileSystem.CreateNew(publicPath);
            createdPublic = true;
            using var secretWriter = new StreamWriter(secret);
            using var publicWriter = new StreamWriter(pub);
            secretWriter.Write(exported.PrivateKeyArmor);
            publicWriter.Write(exported.PublicKeyArmor);
            secretWriter.Flush();
            publicWriter.Flush();
            secret.Flush(flushToDisk: true);
            pub.Flush(flushToDisk: true);
        }
        catch
        {
            if (createdSecret) File.Delete(secretPath);
            if (createdPublic) File.Delete(publicPath);
            throw;
        }
    }
}
