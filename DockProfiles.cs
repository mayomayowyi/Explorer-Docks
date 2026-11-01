using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ExplorerDocks
{
    internal enum DockGroupKind { None = 0, Work = 2 }

    internal sealed class DockEntry
    {
        // 同一Work内で同じフォルダーを複数開いていても、別ウィンドウとして保存・復元する。
        public string EntryId { get; set; } = Guid.NewGuid().ToString("N");
        public string Path { get; set; } = "";
        public string GroupId { get; set; } = "";
        public string WorkName { get; set; } = "";
        public string CustomLabel { get; set; } = "";
        public DockGroupKind Kind { get; set; }
        public bool Pinned { get; set; }
        public string Screen { get; set; } = "";
        public DockEdge Edge { get; set; } = DockEdge.Right;
        public double Position { get; set; } = 0.5;
        // Work内の並び順と、縦一列に積むかどうか(同じWorkの全員で同じ値を持つ)
        public int Slot { get; set; }
        public bool Stacked { get; set; }
        public bool SyncWindowSizes { get; set; } = true;
        public int Width { get; set; }
        public int Height { get; set; }
    }

    internal static class DockProfiles
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExplorerDocks", "groups.json");

        private static readonly string WorkNamesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExplorerDocks", "works.json");

        public static string[] ReadWorkNames()
        {
            try
            {
                var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(WorkNamesPath));
                return names?.Where(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 64)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
            }
            catch { return Array.Empty<string>(); }
        }

        public static void WriteWorkNames(IEnumerable<string> names)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WorkNamesPath)!);
            var temp = WorkNamesPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(names.OrderBy(n => n).ToArray()));
            File.Move(temp, WorkNamesPath, true);
        }

        public static List<DockEntry> Read()
        {
            try
            {
                var data = JsonSerializer.Deserialize<List<DockEntry>>(File.ReadAllText(FilePath));
                if (data is not null)
                {
                    // v0.2.0以前のリンク所属だけを解除し、Workと収納位置は維持する。
                    foreach (var entry in data)
                        if ((int)entry.Kind == 1)
                        {
                            entry.Kind = DockGroupKind.None;
                            entry.GroupId = "";
                            entry.WorkName = "";
                        }
                    var valid = data.FindAll(e =>
                        !string.IsNullOrWhiteSpace(e.Path) &&
                        (Path.IsPathFullyQualified(e.Path) ||
                         string.Equals(e.Path, ExplorerFolders.ThisPc, StringComparison.OrdinalIgnoreCase))
                        && e.Path.Length < 4096
                        && Enum.IsDefined(e.Kind) && Enum.IsDefined(e.Edge)
                        && double.IsFinite(e.Position) && e.Position >= 0 && e.Position <= 1);
                    // 旧保存データ（EntryIdなし）にもIDを補い、重複IDは採番し直す。
                    var entryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in valid)
                    {
                        if (string.IsNullOrWhiteSpace(entry.EntryId) || !entryIds.Add(entry.EntryId))
                        {
                            do { entry.EntryId = Guid.NewGuid().ToString("N"); }
                            while (!entryIds.Add(entry.EntryId));
                        }
                    }
                    // 旧版で分裂したWorkを、名前が同じなら1つのグループへ戻す。
                    var workIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in valid.Where(e => e.Kind == DockGroupKind.Work
                        && !string.IsNullOrWhiteSpace(e.WorkName)))
                    {
                        if (!workIds.TryGetValue(entry.WorkName, out string? id))
                        {
                            id = string.IsNullOrEmpty(entry.GroupId)
                                ? Guid.NewGuid().ToString("N") : entry.GroupId;
                            workIds.Add(entry.WorkName, id);
                        }
                        entry.GroupId = id;
                    }
                    return valid;
                }
            }
            catch { }
            return new List<DockEntry>();
        }

        public static void Write(List<DockEntry> entries)
        {
            var parent = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(parent);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
        }
    }
}
