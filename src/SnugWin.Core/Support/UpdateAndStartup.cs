using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text.Json;

namespace SnugWin.Core.Support;

public enum ReleasePackageKind
{
    SelfContained,
    FrameworkDependent
}

public sealed record UpdateAssetInfo(string Name, string DownloadUrl, long Size);

public sealed record UpdateReleaseInfo(
    string TagName,
    string NormalizedVersion,
    string HtmlUrl,
    string Body,
    IReadOnlyList<UpdateAssetInfo> Assets);

public static class AppVersionHelper
{
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "0.0.0";
        string s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return string.IsNullOrWhiteSpace(s) ? "0.0.0" : s;
    }

    public static bool TryParseSemVer(string? raw, out Version version)
    {
        string norm = Normalize(raw);
        if (!Version.TryParse(norm, out var v))
        {
            version = new Version(0, 0, 0, 0);
            return false;
        }
        version = new Version(
            Math.Max(0, v.Major),
            Math.Max(0, v.Minor),
            Math.Max(0, v.Build),
            Math.Max(0, v.Revision));
        return true;
    }

    public static bool IsNewer(string? candidateTag, string? currentVersion)
    {
        if (!TryParseSemVer(candidateTag, out var cand)) return false;
        if (!TryParseSemVer(currentVersion, out var curr)) return true;
        return cand > curr;
    }

    public static string PreferredAssetName(string tagOrVersion, ReleasePackageKind kind)
    {
        string ver = Normalize(tagOrVersion);
        string suffix = kind == ReleasePackageKind.SelfContained ? "selfcontained" : "frameworkdependent";
        return $"SnugWin-v{ver}-win-x64-{suffix}.zip";
    }
}

public static class ChecksumParser
{
    public static bool TryFindSha256(string? checksumsText, string fileName, out string sha256)
    {
        sha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(checksumsText) || string.IsNullOrWhiteSpace(fileName))
            return false;

        string target = Path.GetFileName(fileName.Trim());
        foreach (string rawLine in checksumsText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length < 66) continue;

            int sep = line.IndexOfAny([' ', '\t']);
            if (sep != 64) continue;

            string hash = line[..64];
            if (!IsHex64(hash)) continue;

            string name = line[(sep + 1)..].Trim().TrimStart('*');
            name = Path.GetFileName(name);
            if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
            {
                sha256 = hash.ToUpperInvariant();
                return true;
            }
        }
        return false;
    }

    private static bool IsHex64(string s)
    {
        if (s.Length != 64) return false;
        foreach (char c in s)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }
}

public static class GitHubReleaseParser
{
    public static UpdateReleaseInfo? ParseLatestRelease(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(tag)) return null;

            string htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? "" : "";
            string body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";

            var assets = new List<UpdateAssetInfo>();
            if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in assetsEl.EnumerateArray())
                {
                    string name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    string dl = item.TryGetProperty("browser_download_url", out var dEl) ? dEl.GetString() ?? "" : "";
                    long size = item.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out long sz) ? sz : 0;
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(dl))
                        assets.Add(new UpdateAssetInfo(name, dl, size));
                }
            }

            return new UpdateReleaseInfo(
                tag.Trim(),
                AppVersionHelper.Normalize(tag),
                htmlUrl,
                body,
                assets);
        }
        catch
        {
            return null;
        }
    }
}

public static class StartupTaskDefinition
{
    public const string TaskName = "SnugWinAutoStart";
    public const string MinimizedArg = "--minimized";

    public static string BuildTaskXml(string exePath, string? workingDirectory = null)
    {
        string safeExe = SecurityElement.Escape(exePath) ?? exePath;
        string dir = workingDirectory ?? Path.GetDirectoryName(exePath) ?? "";
        string safeDir = SecurityElement.Escape(dir) ?? "";
        string safeArgs = SecurityElement.Escape(MinimizedArg) ?? MinimizedArg;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>SnugWin UU远程手机竖屏伴侣开机静默启动</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT20S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{safeExe}</Command>
                  <Arguments>{safeArgs}</Arguments>
                  <WorkingDirectory>{safeDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
