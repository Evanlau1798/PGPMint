using PGPMint.Core.Storage;

namespace PGPMint.Cli;

internal static class SyncCommand
{
    internal static int Run(CommandLine options)
    {
        bool useDpapi = options.Has("windows-dpapi");
        if (useDpapi && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("--windows-dpapi is only supported on Windows.");
        }

        string dataDir = useDpapi ? AppDataPaths.WindowsDpapiDirectory : Program.DataDirectory(options);
        if (!useDpapi && !options.Has("data-dir"))
        {
            AppDataPaths.PreparePasswordVaultDirectory();
        }

        var stateStore = new AppStateStore(dataDir);
        bool fromStdin = options.Has("password-stdin");

        if (options.Get("export") is { } exportPath)
        {
            return RunExport(stateStore, dataDir, exportPath, options, useDpapi, fromStdin);
        }

        string importPath = options.Get("import")!;
        return RunImport(stateStore, dataDir, importPath, options, useDpapi, fromStdin);
    }

    private static int RunExport(
        AppStateStore stateStore,
        string dataDir,
        string exportPath,
        CommandLine options,
        bool useDpapi,
        bool fromStdin)
    {
        bool progressOnly = options.Has("progress-only");
        if (progressOnly)
        {
            SyncExportResult res = SyncBundleService.Export(stateStore, vaultStore: null, exportPath, includeKeys: false, ReadOnlySpan<char>.Empty);
            Console.WriteLine($"Exported progress sync bundle: {res.FilePath} (pattern={res.PatternMask}; checked={res.TotalCheckedCandidates}; keys=0).");
            return 0;
        }

        if (useDpapi)
        {
            var dpapi = new DpapiSeedProtector();
            var vault = new VaultStore(dataDir, dpapi);
            bool hasMatches = vault.LoadMatches().Count > 0;
            char[] bundlePw = hasMatches
                ? SecretInput.Read("Sync bundle passphrase: ", fromStdin, allowEmpty: false)
                : [];
            try
            {
                SyncExportResult res = SyncBundleService.Export(stateStore, vault, exportPath, includeKeys: hasMatches, bundlePw);
                Console.WriteLine($"Exported sync bundle: {res.FilePath} (pattern={res.PatternMask}; checked={res.TotalCheckedCandidates}; keys={res.ExportedKeysCount}).");
                return 0;
            }
            finally
            {
                Array.Clear(bundlePw);
            }
        }

        using var lease = new VaultSession(dataDir);
        using PasswordSeedProtector protector = Program.OpenVaultProtector(lease, options);
        var pwVault = new VaultStore(dataDir, protector);
        bool vaultHasMatches = pwVault.LoadMatches().Count > 0;
        char[] exportPw = vaultHasMatches
            ? SecretInput.Read("Sync bundle passphrase: ", fromStdin, allowEmpty: false)
            : [];
        try
        {
            SyncExportResult res = SyncBundleService.Export(stateStore, pwVault, exportPath, includeKeys: vaultHasMatches, exportPw);
            Console.WriteLine($"Exported sync bundle: {res.FilePath} (pattern={res.PatternMask}; checked={res.TotalCheckedCandidates}; keys={res.ExportedKeysCount}).");
            return 0;
        }
        finally
        {
            Array.Clear(exportPw);
        }
    }

    private static int RunImport(
        AppStateStore stateStore,
        string dataDir,
        string importPath,
        CommandLine options,
        bool useDpapi,
        bool fromStdin)
    {
        SyncBundleInfo info = SyncBundleService.Inspect(importPath);
        if (!info.HasEncryptedKeys || info.KeyCount == 0)
        {
            SyncImportResult res = SyncBundleService.Import(stateStore, vaultStore: null, importPath, ReadOnlySpan<char>.Empty);
            PrintImportSummary(res);
            return 0;
        }

        if (useDpapi)
        {
            var dpapi = new DpapiSeedProtector();
            var vault = new VaultStore(dataDir, dpapi);
            char[] bundlePw = SecretInput.Read("Sync bundle passphrase: ", fromStdin, allowEmpty: false);
            try
            {
                SyncImportResult res = SyncBundleService.Import(stateStore, vault, importPath, bundlePw);
                PrintImportSummary(res);
                return 0;
            }
            finally
            {
                Array.Clear(bundlePw);
            }
        }

        using var lease = new VaultSession(dataDir);
        using PasswordSeedProtector protector = Program.OpenVaultProtector(lease, options);
        var pwVault = new VaultStore(dataDir, protector);
        char[] importPw = SecretInput.Read("Sync bundle passphrase: ", fromStdin, allowEmpty: false);
        try
        {
            SyncImportResult res = SyncBundleService.Import(stateStore, pwVault, importPath, importPw);
            PrintImportSummary(res);
            return 0;
        }
        finally
        {
            Array.Clear(importPw);
        }
    }

    private static void PrintImportSummary(SyncImportResult res)
    {
        string mismatchNote = res.PatternMismatchSkippedProgress ? "; pattern mismatch (progress skipped)" : string.Empty;
        Console.WriteLine(
            $"Imported sync bundle: +{res.AddedCandidates} candidates (total={res.NewTotalCheckedCandidates}); " +
            $"{res.ImportedKeysCount} new keys (duplicates skipped={res.SkippedDuplicateKeysCount}){mismatchNote}.");
    }
}
