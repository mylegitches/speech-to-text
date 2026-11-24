using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SpeechToText.Output;

/// <summary>One line of a QuickNotes file: "20251124093000V the note text".</summary>
public sealed record NoteLine(string Raw, DateTime? Time, char Kind, string Text)
{
    /// <summary>Lines that don't match the format are kept as-is (Kind '?') so a rewrite never loses them.</summary>
    public static NoteLine Parse(string raw)
    {
        var m = Regex.Match(raw, @"^(\d{14})([TV]) ?(.*)$");
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var t))
            return new NoteLine(raw, t, m.Groups[2].Value[0], m.Groups[3].Value);
        return new NoteLine(raw, null, '?', raw);
    }

    public NoteLine WithText(string text)
    {
        text = Regex.Replace(text, @"\s*\r?\n\s*", " ").Trim(); // one note per line
        return Time is { } t ? new NoteLine($"{t:yyyyMMddHHmmss}{Kind} {text}", t, Kind, text) : new NoteLine(text, null, '?', text);
    }
}

/// <summary>
/// Edits a QuickNotes file. Every change re-reads the file first, so a note the app appended while
/// the window was open is never overwritten. Deleted notes are quarantined in a sidecar file
/// ("my_notes.deleted.txt"), never erased, so they can be restored.
/// </summary>
public static class QuickNotesFile
{
    static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static string DeletedPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".deleted" + Path.GetExtension(path));

    public static List<NoteLine> Load(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path, Utf8).Where(l => l.Trim().Length > 0).Select(NoteLine.Parse).ToList()
            : [];

    /// <summary>Replaces a note's line. Returns false if it's no longer in the file.</summary>
    public static bool Update(string path, NoteLine oldLine, NoteLine newLine) =>
        Rewrite(path, lines =>
        {
            int i = lines.IndexOf(oldLine.Raw);
            if (i < 0) return -1;
            lines[i] = newLine.Raw;
            return i;
        }) >= 0;

    /// <summary>Moves a note to the .deleted file. Returns its old line index for Undo, or -1.</summary>
    public static int Delete(string path, NoteLine line)
    {
        int index = Rewrite(path, lines =>
        {
            int i = lines.IndexOf(line.Raw);
            if (i >= 0) lines.RemoveAt(i);
            return i;
        });
        if (index >= 0) File.AppendAllText(DeletedPath(path), line.Raw + Environment.NewLine, Utf8);
        return index;
    }

    /// <summary>Puts a deleted note back where it was and takes it out of the .deleted file.</summary>
    public static void Restore(string path, NoteLine line, int index)
    {
        Rewrite(path, lines => { lines.Insert(Math.Clamp(index, 0, lines.Count), line.Raw); return 0; });

        var deleted = DeletedPath(path);
        if (!File.Exists(deleted)) return;
        var kept = File.ReadAllLines(deleted, Utf8).ToList();
        int last = kept.LastIndexOf(line.Raw);
        if (last >= 0) kept.RemoveAt(last);
        WriteAtomic(deleted, kept);
    }

    static int Rewrite(string path, Func<List<string>, int> change)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path, Utf8).Where(l => l.Trim().Length > 0).ToList() : [];
        int result = change(lines);
        if (result >= 0) WriteAtomic(path, lines);
        return result;
    }

    static void WriteAtomic(string path, List<string> lines)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines) + Environment.NewLine, Utf8);
        File.Move(tmp, path, overwrite: true);
    }
}
