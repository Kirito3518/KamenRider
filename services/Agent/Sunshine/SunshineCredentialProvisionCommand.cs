using System.Security.Principal;
using System.Text;

namespace KamenRaider.Agent.Sunshine;

internal static class SunshineCredentialProvisionCommand
{
    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Sunshine credential provisioning requires Windows.");
            return 2;
        }

        if (args.Length is < 2 or > 3 || (args.Length == 3 && !string.Equals(args[2], "--replace", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("Usage: Agent.exe --provision-sunshine-credentials <AgentDataDirectory> [--replace]");
            return 2;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("Run this command from an elevated PowerShell or Command Prompt.");
            return 2;
        }
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Run this command in an interactive terminal so the Sunshine password can be entered without echo.");
            return 2;
        }

        try
        {
            var store = new WindowsSunshineCredentialStore(
                args[1],
                WindowsSunshineCredentialStore.ResolveAgentServiceSid(),
                new WindowsDpapiMachineSecretProtector());
            var replace = args.Length == 3;
            if (File.Exists(store.CredentialPath) && !replace)
            {
                Console.Error.WriteLine("Sunshine credentials already exist. Rerun with --replace to rotate them.");
                return 2;
            }

            Console.Write("Sunshine management username: ");
            var username = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(username))
            {
                Console.Error.WriteLine("A Sunshine management username is required.");
                return 2;
            }

            var password = ReadSecret("Sunshine management password: ");
            var confirmation = ReadSecret("Confirm password: ");
            if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("The passwords did not match; no credential file was written.");
                return 2;
            }

            store.Save(new(username, password), replace);
            Console.WriteLine("Sunshine credentials were DPAPI-protected and saved with the Agent service ACL. Restart KamenRaider Agent to load a credential rotation.");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Sunshine credential provisioning failed ({ex.GetType().Name}). No credential values were logged.");
            return 1;
        }
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var buffer = new char[2048];
        var length = 0;
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return new string(buffer, 0, length);
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (length > 0) buffer[--length] = '\0';
                    continue;
                }
                if (!char.IsControl(key.KeyChar) && length < buffer.Length)
                    buffer[length++] = key.KeyChar;
            }
        }
        finally
        {
            Array.Clear(buffer);
        }
    }
}
