using System.Globalization;
using System.Text;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        string directory = AppDataPaths.PasswordVaultDirectory;
        string backendName = "auto";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length)
            {
                directory = args[++i];
            }
            else if (args[i] == "--backend" && i + 1 < args.Length)
            {
                backendName = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"Unsupported argument '{args[i]}'. Passwords are never accepted on the command line.");
                return 1;
            }
        }

        IGpuBackend backend = backendName switch
        {
            "auto" => GpuBackendFactory.CreateDefault(),
            "vulkan" => new VulkanBackend(),
            "opencl" => new OpenClBackend(),
            "metal" => new MetalBackend(),
            _ => throw new ArgumentException($"Unsupported backend '{backendName}'.")
        };

        IVaultSecretStore? secrets = OperatingSystem.IsMacOS() ? new MacKeychainSecretStore() : null;
        await using var session = new HostSession(backend, directory, secrets);
        var server = new HostServer(session, Console.In, Console.Out);
        await server.RunAsync();
        return 0;
    }
}
