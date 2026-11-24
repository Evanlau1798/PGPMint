namespace PGPMint.Core.Search;

internal sealed class GpuDeviceLostException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
