namespace PGPMint.Core.Storage;

// Preserve the Windows vault format and existing application entry point.
public sealed class DpapiVaultStore(string? dataDirectory = null)
    : VaultStore(dataDirectory, new DpapiSeedProtector());
