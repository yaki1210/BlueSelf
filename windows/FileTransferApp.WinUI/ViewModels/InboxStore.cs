using System.IO;
using System.Text.Json;

namespace FileTransferApp.WinUI.ViewModels;

/// <summary>JSON snapshot of one inbox row, independent of WPF observable types.</summary>
internal sealed class InboxSnapshot
{
    public string Id { get; set; } = "";
    public string? MessageId { get; set; }
    public string Device { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsOutgoing { get; set; }
    public bool IsUnread { get; set; }
    public InboxAttachmentSnapshot[] Attachments { get; set; } = [];
}

/// <summary>JSON snapshot of one attachment on an inbox row.</summary>
internal sealed class InboxAttachmentSnapshot
{
    public string Name { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string Kind { get; set; } = "file";
    public string Path { get; set; } = "";
    public bool IsIncomingSaved { get; set; }
}

/// <summary>
/// Persists inbox history to %LOCALAPPDATA%\BlueSelf\inbox.json so records
/// survive app restarts (mirrors Android Room, using JSON like settings).
/// </summary>
internal static class InboxStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static string InboxFile => Path.Combine(BlueSelfPaths.DataDir, "inbox.json");

    /// <summary>Reads persisted inbox rows; newest-first. Empty when missing or malformed.</summary>
    public static IReadOnlyList<InboxSnapshot> Load() => LoadFrom(InboxFile);

    public static void Save(IEnumerable<InboxEntry> entries) => SaveTo(InboxFile, entries);

    internal static IReadOnlyList<InboxSnapshot> LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<InboxSnapshot[]>(json, JsonOptions);
            if (list == null || list.Length == 0) return [];
            return list
                .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                .OrderByDescending(s => s.CreatedAt)
                .ToList();
        }
        catch
        {
            try
            {
                if (File.Exists(path))
                    File.Copy(path, path + ".corrupt", overwrite: true);
            }
            catch { /* ignore */ }
            return [];
        }
    }

    internal static void SaveTo(string path, IEnumerable<InboxEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var snapshots = entries.Select(ToSnapshot).ToArray();
            var json = JsonSerializer.Serialize(snapshots, JsonOptions);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Inbox persistence is best-effort; ignore failures.
        }
    }

    internal static InboxSnapshot ToSnapshot(InboxEntry entry) => new()
    {
        Id = entry.Id,
        MessageId = entry.MessageId,
        Device = entry.Device,
        Content = entry.Content ?? "",
        CreatedAt = entry.CreatedAt,
        IsOutgoing = entry.IsOutgoing,
        IsUnread = entry.IsUnread,
        Attachments = entry.Attachments.Select(a => new InboxAttachmentSnapshot
        {
            Name = a.Name,
            SizeText = a.SizeText,
            Kind = a.Kind,
            Path = a.Path ?? "",
            IsIncomingSaved = a.IsIncomingSaved
        }).ToArray()
    };

    internal static InboxEntry FromSnapshot(InboxSnapshot s)
    {
        var entry = new InboxEntry
        {
            Id = string.IsNullOrWhiteSpace(s.Id) ? Guid.NewGuid().ToString("N") : s.Id,
            MessageId = string.IsNullOrWhiteSpace(s.MessageId) ? null : s.MessageId,
            Device = s.Device ?? "",
            Content = s.Content ?? "",
            CreatedAt = s.CreatedAt == default ? DateTimeOffset.Now : s.CreatedAt,
            IsOutgoing = s.IsOutgoing,
            IsUnread = s.IsUnread
        };
        if (s.Attachments != null)
        {
            foreach (var a in s.Attachments)
            {
                entry.Attachments.Add(new InboxAttachment
                {
                    Name = a.Name ?? "",
                    SizeText = a.SizeText ?? "",
                    Kind = string.IsNullOrWhiteSpace(a.Kind) ? "file" : a.Kind,
                    Path = a.Path ?? "",
                    IsIncomingSaved = a.IsIncomingSaved
                });
            }
        }
        entry.NotifyAttachments();
        return entry;
    }

    /// <summary>Case-insensitive match against device name, body text, and attachment names.</summary>
    internal static bool Matches(InboxEntry entry, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        if (entry.Device.Contains(q, StringComparison.CurrentCultureIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(entry.Content) &&
            entry.Content.Contains(q, StringComparison.CurrentCultureIgnoreCase)) return true;
        foreach (var a in entry.Attachments)
        {
            if (a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)) return true;
        }
        return false;
    }
}
