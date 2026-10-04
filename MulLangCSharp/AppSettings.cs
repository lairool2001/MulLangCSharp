using System.IO;
using System.Text;

namespace MulLangCSharp;

/// <summary>簡單的使用者設定（%LOCALAPPDATA%\MulLangCSharp\settings.txt，每行「名稱=值」）。</summary>
internal static class AppSettings
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MulLangCSharp", "settings.txt");

    private static Dictionary<string, string>? _values;

    private static Dictionary<string, string> Values
    {
        get
        {
            if (_values is not null) return _values;
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                        if (line.IndexOf('=') is var eq and > 0)
                            _values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            catch (IOException) { }
            return _values;
        }
    }

    public static bool GetBool(string name, bool defaultValue) =>
        Values.TryGetValue(name, out var v) && bool.TryParse(v, out var b) ? b : defaultValue;

    public static void SetBool(string name, bool value)
    {
        Values[name] = value.ToString();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, Values.Select(kv => $"{kv.Key}={kv.Value}"), new UTF8Encoding(true));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
