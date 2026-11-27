using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            CommandLine options = CommandLine.Parse(args);
            if (options.Command == "help") { Console.WriteLine(Help); return 0; }
            if (options.Command == "metal-source") { Console.Write(MetalKernelSource.BuildFourKeyMetal()); return 0; }
            if (options.Command == "export") return ExportCommand.Run(options);
            if (options.Command == "sync") return SyncCommand.Run(options);
            IGpuBackend backend = CreateBackend(options);
            IReadOnlyList<GpuDeviceInfo> devices = backend.EnumerateHardwareGpus();
            if (options.Command == "devices")
            {
                if (options.Has("json")) Console.WriteLine(JsonSerializer.Serialize(new
                {
                    platform = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                    runtime = Environment.Version.ToString(), hardwareGpuVerified = false, devices,
                    diagnostics = backend is PortableGpuBackend portable ? portable.Diagnostics : Array.Empty<string>()
                }));
                else
                {
                    foreach (var info in devices) Console.WriteLine($"{info.DeviceKey}\t{info.Name}\t{info.BackendName}\t{info.DriverVersion}");
                    if (backend is PortableGpuBackend portable)
                        foreach (string message in portable.Diagnostics) Console.Error.WriteLine(message);
                    if (devices.Count == 0) Console.Error.WriteLine("No hardware compute GPU detected. GPU correctness and throughput remain unverified.");
                }
                return 0;
            }
            if (options.Get("device") is { } key) devices = devices.Where(x => x.DeviceKey == key).ToArray();
            if (devices.Count == 0) throw new InvalidOperationException("No matching hardware compute GPU is available. No CPU search fallback is enabled.");
            if (options.Command == "verify")
            {
                foreach (var info in devices)
                {
                    using var device = backend.CreateDevice(info);
                    GpuVerification.Verify(device);
                    Console.WriteLine($"PASS: {device.Info.Name}; {device.Info.BackendName}; {device.KernelVersion}; 1-16 hex CPU oracle, overflow, FindOne and cancellation.");
                }
                return 0;
            }
            if (options.Command == "benchmark") return await BenchmarkCommand.RunAsync(backend, devices, options);
            if (options.Command == "curve") return await CurveCommand.RunAsync(backend, devices, options);
            return await SearchCommand.RunAsync(backend, devices, options);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or CryptographicException or NotSupportedException or JsonException
            or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or FormatException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal static IGpuBackend CreateBackend(CommandLine options)
    {
        int group = options.Number("workgroup", 128);
        return (options.Get("backend") ?? "auto") switch
        {
            "vulkan" => new VulkanBackend(workgroupSize: group),
            "opencl" => new OpenClBackend(workgroupSize: group),
            "metal" => new MetalBackend(workgroupSize: group),
            _ => GpuBackendFactory.CreateDefault(workgroupSize: group)
        };
    }

    internal static string DataDirectory(CommandLine options) => Path.GetFullPath(options.Get("data-dir") ?? AppDataPaths.PasswordVaultDirectory);

    internal static PasswordSeedProtector OpenVaultProtector(VaultSession session, CommandLine options, IVaultSecretStore? automaticSecrets = null)
    {
        if (AutomaticVault.IsAutomatic(session.DirectoryPath))
        {
            automaticSecrets ??= OperatingSystem.IsMacOS() ? new MacKeychainSecretStore()
                : throw new PlatformNotSupportedException("This vault requires the original operating-system keychain and user account.");
            return AutomaticVault.TryOpen(session, automaticSecrets)
                ?? throw new InvalidDataException("The automatic vault metadata disappeared; the vault was not replaced.");
        }
        char[] password = SecretInput.Read("Vault password: ", options.Has("password-stdin"));
        try { return PasswordSeedProtector.Open(session.DirectoryPath, password); }
        finally { Array.Clear(password); }
    }

    private const string Help = """
        PGPMint hardware GPU OpenPGP vanity Key IDs
        pgpmint devices [--json] [--backend auto|vulkan|opencl|metal]
        pgpmint verify [--backend NAME] [--device KEY] [--workgroup 64|128|256]
        pgpmint benchmark [--backend NAME] [--device KEY] [--workgroup 64|128|256]
                          [--seconds 60] [--rounds 3] [--pattern 1145141919810114]
                          [--mode find-one|enumerate-all] [--timestamps COUNT]
        pgpmint curve [--backend NAME] [--device KEY] [--workgroup 64|128|256]
                      [--pattern HEX] [--settle-seconds 3] [--measure-seconds 5]
                      [--out-csv PATH] [--out-json PATH] [--data-dir PATH] [--password-stdin]
        pgpmint search --pattern HEX [--backend NAME] [--device KEY] [--data-dir PATH]
                       [--tier eco|balanced|efficiency|max] [--max-temp 65..90]
                       [--keep-running] [--status-interval N] [--json]
                       [--on-hit-exec PATH] [--seconds N] [--password-stdin]
        pgpmint export --key KEYID --name NAME [--email EMAIL] --output DIRECTORY
                       [--data-dir PATH] [--password-stdin]
        pgpmint sync --export FILE | --import FILE [--progress-only]
                     [--data-dir PATH | --windows-dpapi] [--password-stdin]

        Search and export prompt for a password unless the vault uses the macOS keychain.
        --password-stdin reads one line for a password vault, then the export passphrase
        for export (empty permits an unprotected OpenPGP export). Keychain vaults only
        read the export-passphrase line and require an unlocked desktop login session.
        Passwords must never be passed as command-line arguments. Ctrl+C saves progress.
        Benchmark uses isolated disposable data; it does not open the user's vault.
        """;
}
