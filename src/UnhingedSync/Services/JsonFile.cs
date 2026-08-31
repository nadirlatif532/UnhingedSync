using System.IO;
using System.Text.Json;

namespace UnhingedSync.Services;

/// <summary>
/// Reads JSON while keeping track of WHICH file failed.
///
/// System.Text.Json reports a line and a column but never the file, and this app parses
/// several JSON files during startup alone: the project's shared config, the .uproject,
/// the engine's Build.version, the launcher manifest. A bare "'{' is invalid after a
/// single JSON value. LineNumber: 94" therefore points everybody at whichever file they
/// already had in mind. That cost a long, circular debugging session in which a valid
/// config was reset, re-synced and deleted several times over while the actual culprit --
/// a duplicated .uproject -- sat unexamined, because nothing in the message ruled it in.
///
/// The line number is translated too. JsonException.LineNumber is zero based, so what it
/// calls line 94 is line 95 in every text editor, which makes "but the file does not even
/// have that many lines" the natural and completely misleading conclusion.
/// </summary>
public static class JsonFile
{
    /// <summary>Deserializes a file, naming it if the parse fails.</summary>
    public static T Deserialize<T>(string path, JsonSerializerOptions options)
    {
        var text = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<T>(text, options)
                ?? throw new InvalidOperationException($"{path} contains null rather than a value.");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(Explain(path, e), e);
        }
    }

    /// <summary>Parses a file into a document, naming it if the parse fails.</summary>
    public static JsonDocument Parse(string path)
    {
        var text = File.ReadAllText(path);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(Explain(path, e), e);
        }
    }

    private static string Explain(string path, JsonException e)
    {
        // Zero based to one based, so the number matches what an editor shows.
        var line = e.LineNumber is { } n ? n + 1 : (long?)null;
        var at = line is null ? "" : $" at line {line}";

        // Worth naming outright, because both the cause and the fix are specific: the file
        // holds two complete JSON documents, which is what a merge that kept both sides, or
        // a copy written over a longer existing file without truncating it, leaves behind.
        if (e.Message.Contains("is invalid after a single JSON value", StringComparison.Ordinal))
        {
            return $"{path} contains more than one JSON document{at}.\n\n" +
                   "The file is valid up to that point, then a second document begins. That " +
                   "usually means its content got duplicated: a merge that kept both sides, or " +
                   "something written over a longer previous version without truncating it.\n\n" +
                   (line is null
                       ? "Keep the first document and delete everything after it."
                       : $"Open the file, delete everything from line {line} to the end, and keep " +
                         "what comes before it. Restoring this one file from version control " +
                         "does the same thing.");
        }

        return $"{path} is not valid JSON{at}.\n\n{e.Message}";
    }
}
