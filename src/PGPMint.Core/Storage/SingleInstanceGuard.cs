using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PGPMint.Core.Storage;

public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\PGPMint-SingleInstance-v1";
    private static readonly string PipeName = BuildUserScopedPipeName();

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenerTask;
    private bool _disposed;

    public bool IsPrimaryInstance { get; }
    public event EventHandler? ActivationRequested;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsPrimaryInstance = createdNew;
    }

    public void StartListeningForActivations()
    {
        if (!IsPrimaryInstance || _listenerTask is not null)
        {
            return;
        }

        _listenerTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    readCts.CancelAfter(TimeSpan.FromSeconds(2));

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? message = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
                    if (string.Equals(message, "ACTIVATE", StringComparison.Ordinal))
                    {
                        ActivationRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Per-connection read timeout; loop to accept next connection
                }
                catch
                {
                    try
                    {
                        await Task.Delay(150, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        });
    }

    public static bool SignalPrimaryInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout: 1000);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine("ACTIVATE");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildUserScopedPipeName()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        int sessionId = Process.GetCurrentProcess().SessionId;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sid}:{sessionId}")))[..16];
        return $"PGPMint-Activate-v1-{digest}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        if (_listenerTask is not null)
        {
            try { _listenerTask.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        }

        if (IsPrimaryInstance)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }

        _mutex.Dispose();
        _cts.Dispose();
    }
}
