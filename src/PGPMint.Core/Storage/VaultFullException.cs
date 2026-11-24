namespace PGPMint.Core.Storage;

public sealed class VaultFullException(int capacity)
    : InvalidOperationException($"The key vault is full ({capacity} stored keys). Export or remove a key before minting another.")
{
    public int Capacity { get; } = capacity;
}
