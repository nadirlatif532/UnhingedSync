using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnhingedSync.Models;

namespace UnhingedSync.Services;

/// <summary>Thin wrapper over the Diversion CLI. All calls run off the UI thread.</summary>
public sealed partial class DvCli
{
    private readonly string _exe;
    private readonly string _workingDir;

    public DvCli(string projectRoot)
    {
        _workingDir = projectRoot;
        _exe = LocateDv();
    }

    private static string LocateDv()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidate = Path.Combine(home, ".diversion", "bin", "dv.exe");
        if (File.Exists(candidate)) return candidate;

        // Fall back to PATH.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var probe = Path.Combine(dir.Trim(), "dv.exe");
                if (File.Exists(probe)) return probe;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }

        throw new FileNotFoundException(
            "The Diversion CLI (dv.exe) was not found. Install Diversion and sign in, " +
            "then restart Unhinged Sync.");
    }

    public async Task<string> RunAsync(
        string[] args,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            WorkingDirectory = _workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            log?.Report(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            log?.Report(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            throw new DvException(
                $"dv {string.Join(' ', args)} failed (exit {proc.ExitCode})",
                stderr.Length > 0 ? stderr.ToString() : stdout.ToString());
        }
        return stdout.ToString();
    }

    public async Task<string> GetWorkspaceCommitAsync(CancellationToken ct = default)
    {
        var raw = (await RunAsync(["status", "--commit-id-only"], null, ct)).Trim();
        if (!CommitIdPattern().IsMatch(raw))
            throw new DvException("Unexpected workspace commit id.", raw);
        return raw;
    }

    public async Task<string> GetBranchAsync(CancellationToken ct = default)
        => (await RunAsync(["branch-name"], null, ct)).Trim();

    /// <summary>Parses 'dv log' into commit records, newest first.</summary>
    public async Task<List<CommitInfo>> GetLogAsync(int limit = 50, CancellationToken ct = default)
    {
        var raw = await RunAsync(["log", "-n", limit.ToString(CultureInfo.InvariantCulture), "--date", "iso"], null, ct);
        var commits = new List<CommitInfo>();
        CommitInfo? current = null;

        foreach (var line in raw.Split('\n'))
        {
            var trimmedEnd = line.TrimEnd('\r');

            var commitMatch = CommitHeaderPattern().Match(trimmedEnd);
            if (commitMatch.Success)
            {
                if (current is not null) commits.Add(current);
                current = new CommitInfo
                {
                    CommitId = commitMatch.Groups[1].Value,
                    Ordinal = int.TryParse(commitMatch.Groups[2].Value, out var ord) ? ord : 0
                };
                continue;
            }
            if (current is null) continue;

            if (trimmedEnd.StartsWith("Author:", StringComparison.Ordinal))
            {
                var email = EmailPattern().Match(trimmedEnd);
                current.AuthorEmail = email.Success ? email.Groups[1].Value : trimmedEnd[7..].Trim();
            }
            else if (trimmedEnd.StartsWith("Date:", StringComparison.Ordinal))
            {
                var value = trimmedEnd[5..].Trim();
                if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                    current.Date = dto;
            }
            else if (trimmedEnd.StartsWith('\t') && string.IsNullOrEmpty(current.Message))
            {
                current.Message = trimmedEnd.Trim();
            }
        }
        if (current is not null) commits.Add(current);

        return commits.OrderByDescending(c => c.Ordinal).ToList();
    }

    public Task UpdateAsync(IProgress<string> log, CancellationToken ct = default)
        => RunAsync(["update"], log, ct);

    [GeneratedRegex(@"^dv\.commit\.\d+$")]
    private static partial Regex CommitIdPattern();

    [GeneratedRegex(@"^commit\s+(dv\.commit\.(\d+))")]
    private static partial Regex CommitHeaderPattern();

    [GeneratedRegex(@"<([^>]+)>")]
    private static partial Regex EmailPattern();
}

public sealed class DvException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}
