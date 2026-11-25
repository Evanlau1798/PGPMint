using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PGPMint.Core.Storage;

public sealed class MacKeychainSecretStore : IVaultSecretStore
{
    private const string Service = "org.pgpmint.vault.v1";
    private string? _keychain;
    public string Kind => "macos-login-keychain";
    public MacKeychainSecretStore() { }
    internal MacKeychainSecretStore(string keychain)
    {
        if (!Path.IsPathFullyQualified(keychain) || keychain.Any(char.IsControl))
            throw new ArgumentException("A keychain path must be absolute and contain no control characters.", nameof(keychain));
        _keychain = keychain;
    }

    public char[]? Read(string account)
    {
        ValidateAccount(account);
        var (code, output) = Execute(Arguments("find-generic-password", "-a", account, "-s", Service, "-w"));
        try
        {
            if (code == 44) return null; // security's item-not-found exit status.
            RequireSuccess(code);
            int length = output.Length;
            while (length > 0 && output[length - 1] is (byte)'\n' or (byte)'\r') length--;
            if (length != 64 || output.AsSpan(0, length).ContainsAnyExcept("0123456789ABCDEFabcdef"u8))
                throw new InvalidDataException("The macOS keychain returned an invalid vault key.");
            var secret = new char[length];
            for (int i = 0; i < length; i++) secret[i] = (char)output[i];
            return secret;
        }
        finally { CryptographicOperations.ZeroMemory(output); }
    }

    public void Create(string account, ReadOnlySpan<char> secret)
    {
        ValidateAccount(account);
        if (secret.Length != 64 || secret.ContainsAnyExcept("0123456789ABCDEFabcdef"))
            throw new ArgumentException("A vault key must be 64 hexadecimal characters.", nameof(secret));
        string suffix = " \"" + LoginKeychain().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        // Interactive security commands carry the secret through stdin, never process arguments.
        var (code, output) = Execute(["-i"], $"add-generic-password -a {account} -s {Service} -l PGPMint -w ", secret, suffix);
        try { RequireSuccess(code); }
        finally { CryptographicOperations.ZeroMemory(output); }
    }

    public void Delete(string account)
    {
        ValidateAccount(account);
        var (code, output) = Execute(Arguments("delete-generic-password", "-a", account, "-s", Service));
        try { if (code != 44) RequireSuccess(code); }
        finally { CryptographicOperations.ZeroMemory(output); }
    }

    private string[] Arguments(params string[] arguments) => [.. arguments, LoginKeychain()];

    private string LoginKeychain()
    {
        if (_keychain is not null) return _keychain;
        // The -d domain variant is unsupported; resolve the login keychain without consulting the default.
        var (code, output) = Execute(["login-keychain"]);
        try { RequireSuccess(code); return _keychain = ParseLoginKeychainPath(output); }
        finally { CryptographicOperations.ZeroMemory(output); }
    }

    internal static string ParseLoginKeychainPath(ReadOnlySpan<byte> output)
    {
        string quoted;
        try { quoted = new UTF8Encoding(false, true).GetString(output).Trim(); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid login keychain path encoding.", ex); }
        if (quoted.Length < 4 || quoted[0] != '"' || quoted[^1] != '"')
            throw new InvalidDataException("Cannot resolve the macOS login keychain path.");
        string path = quoted[1..^1];
        if (path[0] != '/' || path.Any(char.IsControl)) throw new InvalidDataException("Invalid macOS login keychain path.");
        return path;
    }

    private static void ValidateAccount(string account)
    {
        if (!Guid.TryParseExact(account, "N", out _)) throw new ArgumentException("Invalid vault keychain account.", nameof(account));
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("This vault uses the macOS login keychain.");
    }

    private static void RequireSuccess(int code)
    {
        if (code != 0) throw new IOException($"macOS login keychain access failed (security exit {code}). Use an unlocked desktop login session; the vault has not been replaced.");
    }

    private static (int Code, byte[] Output) Execute(string[] arguments, string? prefix = null, ReadOnlySpan<char> secret = default, string suffix = "")
    {
        var info = new ProcessStartInfo("/usr/bin/security")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Cannot start the macOS keychain service client.");
        Task<byte[]> output = ReadBoundedAsync(process.StandardOutput.BaseStream);
        Task<byte[]> errors = ReadBoundedAsync(process.StandardError.BaseStream);
        try
        {
            if (prefix is not null)
            {
                process.StandardInput.Write(prefix);
                process.StandardInput.Write(secret);
                process.StandardInput.Write(suffix);
                process.StandardInput.Write('\n');
            }
            process.StandardInput.Close();
            if (!process.WaitForExit(30000)) throw new IOException("macOS keychain authorization timed out.");
            byte[] errorBytes = errors.GetAwaiter().GetResult();
            CryptographicOperations.ZeroMemory(errorBytes);
            return (process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            try { CryptographicOperations.ZeroMemory(output.GetAwaiter().GetResult()); } catch { }
            try { CryptographicOperations.ZeroMemory(errors.GetAwaiter().GetResult()); } catch { }
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream)
    {
        byte[] buffer = new byte[4096];
        int length = 0;
        try
        {
            while (length < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(length));
                if (read == 0) return buffer[..length];
                length += read;
            }
            throw new IOException("Unexpected output from the macOS keychain client.");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
