using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Reflection;

[assembly: AssemblyTitle("Claude Safe Firefox")]
[assembly: AssemblyDescription("Opens web links only in the protected Firefox installation")]
[assembly: AssemblyVersion("1.0.0.0")]

// This helper performs no network I/O, does not log URLs, and has no fallback browser.
public static class ClaudeSafeBrowser {
    public const string PolicyHash = "29463A5ECB4133A412183D601D507552FD433AC437263D9AD9F9D83717296741";

    public static string ParseUrl(string[] args) {
        if (args.Length == 0) return "https://claude.ai/";
        if (args.Length != 2 || args[0] != "--url") throw new ArgumentException("Invalid invocation.");
        string value = args[1];
        if (String.IsNullOrEmpty(value) || value.Length > 24000) throw new ArgumentException("Invalid URL.");
        foreach (char c in value) {
            // Reject raw quoting, separators and ambiguous backslash URL parsing.
            if (Char.IsControl(c) || Char.IsWhiteSpace(c) || c == '"' || c == '\\')
                throw new ArgumentException("Invalid URL characters.");
        }
        Uri parsed;
        if (!(value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
              value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ||
            !Uri.TryCreate(value, UriKind.Absolute, out parsed) ||
            (parsed.Scheme != "http" && parsed.Scheme != "https") ||
            String.IsNullOrEmpty(parsed.Host) || !String.IsNullOrEmpty(parsed.UserInfo))
            throw new ArgumentException("Only absolute HTTP and HTTPS URLs are supported.");
        // Preserve query/fragment bytes, including OAuth state. Do not decode/re-encode.
        return value;
    }

    // Windows CommandLineToArgvW quoting; no cmd.exe, PowerShell or ShellExecute for URLs.
    public static string Quote(string value) {
        StringBuilder text = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value) {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') text.Append('\\', slashes * 2 + 1);
            else text.Append('\\', slashes);
            text.Append(c); slashes = 0;
        }
        text.Append('\\', slashes * 2); text.Append('"');
        return text.ToString();
    }

    public static ProcessStartInfo BuildStartInfo(string root, string url) {
        url = ParseUrl(new string[] { "--url", url });
        string exe = Path.Combine(root, @"runtime\firefox\core\firefox.exe");
        string profile = Path.Combine(root, @"runtime\claude-browser-profile");
        string policy = Path.Combine(root, @"runtime\firefox\core\distribution\policies.json");
        if (!File.Exists(exe) || !File.Exists(policy)) throw new IOException("Protected browser files are missing.");
        string hash;
        using (SHA256 sha = SHA256.Create())
        using (FileStream input = File.OpenRead(policy))
            hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        if (hash != PolicyHash) throw new IOException("Protected browser policy changed; review is required.");
        Directory.CreateDirectory(profile);
        // A fixed profile selects the matching IPC instance, unlike a generic browser launch.
        // Do not use -no-remote: it prevents receiving subsequent authentication links.
        ProcessStartInfo info = new ProcessStartInfo(exe,
            "-profile " + Quote(profile) + " -url " + Quote(url));
        info.UseShellExecute = false;
        info.WorkingDirectory = Path.GetDirectoryName(exe);
        // Ignore inherited launch modifiers that could disable remoting or select another profile.
        foreach (string key in new string[] { "MOZ_NO_REMOTE", "MOZ_NEW_INSTANCE", "XRE_PROFILE_PATH", "XRE_PROFILE_LOCAL_PATH", "XRE_PROFILE_NAME" })
            info.EnvironmentVariables.Remove(key);
        return info;
    }

    [STAThread]
    public static int Main(string[] args) {
        try {
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\.."));
            Process.Start(BuildStartInfo(root, ParseUrl(args)));
            return 0;
        } catch {
            // Never include the supplied URL, OAuth codes, state, or exception text in logs/UI.
            MessageBox.Show("The protected browser could not be opened. Check the dedicated Firefox files and policies. No other browser was opened.",
                "Claude Safe Firefox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }
}
