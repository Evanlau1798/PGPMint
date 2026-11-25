namespace PGPMint.Cli;

internal static class SecretInput
{
    internal static char[] Read(string prompt, bool fromStdin, bool allowEmpty = false)
    {
        char[] result;
        if (fromStdin)
        {
            char[] buffer = new char[4096];
            int length = 0;
            try
            {
                int next;
                while ((next = Console.In.Read()) >= 0 && next != '\n')
                {
                    if (next == '\r') continue;
                    if (length == buffer.Length) throw new ArgumentException("Password exceeds 4096 characters.");
                    buffer[length++] = (char)next;
                }
                result = buffer[..length];
            }
            finally { Array.Clear(buffer); }
        }
        else
        {
            if (Console.IsInputRedirected) throw new ArgumentException("Use --password-stdin for redirected password input.");
            Console.Error.Write(prompt);
            char[] buffer = new char[4096];
            int length = 0;
            try
            {
                while (true)
                {
                    ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) break;
                    if (key.Key == ConsoleKey.Backspace) { if (length > 0) buffer[--length] = '\0'; continue; }
                    if (key.KeyChar == '\0' || char.IsControl(key.KeyChar)) continue;
                    if (length == buffer.Length) throw new ArgumentException("Password exceeds 4096 characters.");
                    buffer[length++] = key.KeyChar;
                }
                result = buffer[..length];
            }
            finally { Array.Clear(buffer); Console.Error.WriteLine(); }
        }
        if (!allowEmpty && result.Length == 0) throw new ArgumentException("A nonempty vault password is required.");
        return result;
    }
}
