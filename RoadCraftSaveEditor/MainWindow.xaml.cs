using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace RoadCraftSaveEditor;

public enum JsonValueKindDisplay
{
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null,
    Unknown
}

/// <summary>
/// Stores information about the loaded save file for later saving
/// </summary>
public class SaveFileData
{
    public required string FilePath { get; set; }
    public required JsonNode RootJson { get; set; }
    /// <summary>
    /// Original decompressed bytes - used to detect if content was modified
    /// </summary>
    public required byte[] OriginalDecompressedBytes { get; set; }
    public required byte[] OriginalCompressedBytes { get; set; }
    public required CompressionLevel OriginalCompressionLevel { get; set; }
    public required byte[] FileTypeBytes { get; set; }
}

/// <summary>
/// Simple ICommand implementation for binding
/// </summary>
public class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

public class JsonEntryViewModel
{
    private readonly Action<JsonEntryViewModel, string?> _updateValue;

    public JsonEntryViewModel(
        string key,
        JsonNode? node,
        Action<JsonEntryViewModel, string?> updateValue)
    {
        Key = key;
        Node = node;
        _updateValue = updateValue;
        ValueType = GetJsonType(node);
        EditableValue = GetDisplayValue(node);
    }

    public string Key { get; }
    public JsonNode? Node { get; }
    public string ValueType { get; }
    public string? EditableValue { get; set; }

    public bool CanOpen => Node is JsonObject or JsonArray;

    public void CommitEdit() => _updateValue(this, EditableValue);

    private static string GetJsonType(JsonNode? node) => node switch
    {
        JsonObject => "Object",
        JsonArray => "Array",
        JsonValue value when value.TryGetValue<string>(out _) => "String",
        JsonValue value when value.TryGetValue<bool>(out _) => "Boolean",
        JsonValue value when value.TryGetValue<int>(out _) => "Number",
        JsonValue value when value.TryGetValue<long>(out _) => "Number",
        JsonValue value when value.TryGetValue<double>(out _) => "Number",
        JsonValue value when value.TryGetValue<decimal>(out _) => "Number",
        JsonValue => "Value",
        null => "Null",
        _ => "Unknown"
    };

    private static string? GetDisplayValue(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "{...}",
        JsonArray => "[...]",
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonValue value when value.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue value when value.TryGetValue<decimal>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<double>(out var dbl) => dbl.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<int>(out var i) => i.ToString(CultureInfo.InvariantCulture),
        _ => node?.ToJsonString()
    };
}

public partial class MainWindow : Window
{
    private SaveFileData? _currentSaveFile;
    private readonly Stack<(JsonNode Node, string Name)> _navigationStack = new();
    private JsonNode? _currentJsonNode;

    public ObservableCollection<JsonEntryViewModel> VisibleEntries { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        JsonGrid.ItemsSource = VisibleEntries;
    }

    private void RefreshJsonView()
    {
        VisibleEntries.Clear();

        if (_currentJsonNode is JsonObject obj)
        {
            foreach (var pair in obj)
                VisibleEntries.Add(new JsonEntryViewModel(pair.Key, pair.Value, UpdateJsonEntryValue));
        }
        else if (_currentJsonNode is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
                VisibleEntries.Add(new JsonEntryViewModel(i.ToString(), arr[i], UpdateJsonEntryValue));
        }

        BackButton.IsEnabled = _navigationStack.Count > 0;
        BreadcrumbText.Text = BuildBreadcrumbText();
    }

    private string BuildBreadcrumbText()
    {
        if (_navigationStack.Count == 0)
            return "Root";

        return "Root > " + string.Join(" > ", _navigationStack.Select(x => x.Name));
    }

    private void UpdateJsonEntryValue(JsonEntryViewModel entry, string? newValue)
    {
        if (_currentJsonNode is JsonObject obj && obj.TryGetPropertyValue(entry.Key, out var node))
        {
            obj[entry.Key] = ParseValue(node, newValue);
        }
        else if (_currentJsonNode is JsonArray arr && int.TryParse(entry.Key, out var index) && index >= 0 && index < arr.Count)
        {
            arr[index] = ParseValue(arr[index], newValue);
        }

        RefreshJsonView();
    }

    private static JsonNode? ParseValue(JsonNode? existingNode, string? newValue)
    {
        if (existingNode is JsonObject || existingNode is JsonArray)
            return existingNode;

        if (existingNode is JsonValue existingValue)
        {
            if (existingValue.TryGetValue<bool>(out _))
            {
                if (bool.TryParse(newValue, out var b))
                    return JsonValue.Create(b);

                throw new InvalidOperationException("Invalid boolean value.");
            }

            if (existingValue.TryGetValue<int>(out _))
            {
                if (int.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    return JsonValue.Create(i);

                throw new InvalidOperationException("Invalid integer value.");
            }

            if (existingValue.TryGetValue<long>(out _))
            {
                if (long.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return JsonValue.Create(l);

                throw new InvalidOperationException("Invalid long value.");
            }

            if (existingValue.TryGetValue<decimal>(out _))
            {
                if (decimal.TryParse(newValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return JsonValue.Create(d);

                throw new InvalidOperationException("Invalid decimal value.");
            }

            if (existingValue.TryGetValue<double>(out _))
            {
                if (double.TryParse(newValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl))
                    return JsonValue.Create(dbl);

                throw new InvalidOperationException("Invalid number value.");
            }

            return JsonValue.Create(newValue);
        }

        if (newValue is null || string.Equals(newValue, "null", StringComparison.OrdinalIgnoreCase))
            return null;

        return JsonValue.Create(newValue);
    }

    private void LoadFileButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Title = "Select Save File",
            Filter = "All Files (*.*)|*.*"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            var filePath = openFileDialog.FileName;
            FilePathText.Text = filePath;

            try
            {
                var (content, decompressedBytes, compressedBytes, compressionLevel, fileTypeBytes) = LoadAndDecompressFile(filePath);

                var rootJson = JsonNode.Parse(content);
                if (rootJson == null)
                    throw new InvalidOperationException("The file contents are not valid JSON.");

                _currentSaveFile = new SaveFileData
                {
                    FilePath = filePath,
                    RootJson = rootJson,
                    OriginalDecompressedBytes = decompressedBytes,
                    OriginalCompressedBytes = compressedBytes,
                    OriginalCompressionLevel = compressionLevel,
                    FileTypeBytes = fileTypeBytes
                };

                _navigationStack.Clear();
                _currentJsonNode = rootJson;
                RefreshJsonView();
                SaveFileButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                _currentSaveFile = null;
                SaveFileButton.IsEnabled = false;
                VisibleEntries.Clear();
                BreadcrumbText.Text = "";
                MessageBox.Show($"Error loading file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Debug.WriteLine($"Error loading file: {ex}");
            }
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_navigationStack.Count == 0)
            return;

        var previous = _navigationStack.Pop();
        _currentJsonNode = previous.Node;
        RefreshJsonView();
    }

    private void OpenChildButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not JsonEntryViewModel entry)
            return;

        if (entry.Node is not JsonObject && entry.Node is not JsonArray)
            return;

        if (_currentJsonNode != null)
        {
            _navigationStack.Push((_currentJsonNode, entry.Key));
            _currentJsonNode = entry.Node;
            RefreshJsonView();
        }
    }

    private void JsonGrid_CellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is not JsonEntryViewModel entry)
            return;

        if (e.EditAction != System.Windows.Controls.DataGridEditAction.Commit)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                entry.CommitEdit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Invalid value", MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshJsonView();
            }
        }));
    }

    private void SaveFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSaveFile == null)
        {
            MessageBox.Show("No file loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Title = "Save File",
            Filter = "All Files (*.*)|*.*",
            FileName = Path.GetFileName(_currentSaveFile.FilePath),
            InitialDirectory = Path.GetDirectoryName(_currentSaveFile.FilePath)
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                _currentSaveFile.RootJson = _currentSaveFile.RootJson;
                var content = _currentSaveFile.RootJson.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                var saveData = new SaveFileData
                {
                    FilePath = _currentSaveFile.FilePath,
                    RootJson = _currentSaveFile.RootJson,
                    OriginalDecompressedBytes = _currentSaveFile.OriginalDecompressedBytes,
                    OriginalCompressedBytes = _currentSaveFile.OriginalCompressedBytes,
                    OriginalCompressionLevel = _currentSaveFile.OriginalCompressionLevel,
                    FileTypeBytes = _currentSaveFile.FileTypeBytes
                };

                CompressAndSaveFile(saveFileDialog.FileName, content, saveData);
                MessageBox.Show("File saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Debug.WriteLine($"Error saving file: {ex}");
            }
        }
    }

    private static void CompressAndSaveFile(string filePath, string content, SaveFileData saveData)
    {
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var level = saveData.OriginalCompressionLevel;
        using var compressedStream = new MemoryStream();

        const int chunkSize = 1024 * 1024;
        int offset = 0;

        while (offset < contentBytes.Length)
        {
            int bytesToCompress = Math.Min(chunkSize, contentBytes.Length - offset);
            var chunkData = new byte[bytesToCompress];
            Array.Copy(contentBytes, offset, chunkData, 0, bytesToCompress);

            using var zlibDataStream = new MemoryStream();
            zlibDataStream.WriteByte(0x78);
            zlibDataStream.WriteByte(0x9C);

            using (var deflateStream = new DeflateStream(zlibDataStream, level, leaveOpen: true))
            {
                deflateStream.Write(chunkData, 0, chunkData.Length);
            }

            var adler32 = ComputeAdler32(chunkData);
            zlibDataStream.WriteByte((byte)(adler32 >> 24));
            zlibDataStream.WriteByte((byte)(adler32 >> 16));
            zlibDataStream.WriteByte((byte)(adler32 >> 8));
            zlibDataStream.WriteByte((byte)adler32);

            var zlibData = zlibDataStream.ToArray();
            compressedStream.Write(BitConverter.GetBytes(bytesToCompress), 0, 4);
            compressedStream.Write(BitConverter.GetBytes(zlibData.Length), 0, 4);
            compressedStream.Write(zlibData, 0, zlibData.Length);

            offset += bytesToCompress;
        }

        var compressedBytes = compressedStream.ToArray();
        using var outputStream = new MemoryStream();

        outputStream.Write(saveData.FileTypeBytes, 0, 4);
        outputStream.Write(BitConverter.GetBytes(compressedBytes.Length), 0, 4);
        outputStream.Write(new byte[4], 0, 4);
        outputStream.Write(BitConverter.GetBytes(contentBytes.Length), 0, 4);
        outputStream.Write(new byte[4], 0, 4);

        var md5Hash = MD5.HashData(compressedBytes);
        var md5HexString = Convert.ToHexString(md5Hash).ToLowerInvariant();
        var md5Bytes = Encoding.UTF8.GetBytes(md5HexString);
        outputStream.Write(md5Bytes, 0, md5Bytes.Length);

        outputStream.WriteByte(0x03);
        outputStream.Write(compressedBytes, 0, compressedBytes.Length);

        File.WriteAllBytes(filePath, outputStream.ToArray());
    }

    private static uint ComputeAdler32(byte[] data)
    {
        const uint MOD_ADLER = 65521;
        uint a = 1, b = 0;

        foreach (byte byteValue in data)
        {
            a = (a + byteValue) % MOD_ADLER;
            b = (b + a) % MOD_ADLER;
        }

        return (b << 16) | a;
    }

    private static (string Content, byte[] DecompressedBytes, byte[] CompressedBytes, CompressionLevel Level, byte[] FileTypeBytes) LoadAndDecompressFile(string filePath)
    {
        var fileData = File.ReadAllBytes(filePath);

        const int headerSize = 4 + 4 + 4 + 4 + 4 + 32 + 1;

        if (fileData.Length < headerSize)
            throw new InvalidOperationException($"File is too small to contain valid header. Expected at least {headerSize} bytes, got {fileData.Length}");

        var fileTypeBytes = fileData.Take(4).ToArray();
        var md5HashString = Encoding.UTF8.GetString(fileData, 20, 32);

        var compressedData = new byte[fileData.Length - headerSize];
        Array.Copy(fileData, headerSize, compressedData, 0, compressedData.Length);

        var computedMd5 = MD5.HashData(compressedData);
        var computedMd5String = Convert.ToHexString(computedMd5).ToLowerInvariant();
        if (!string.Equals(md5HashString, computedMd5String, StringComparison.OrdinalIgnoreCase))
            Debug.WriteLine($"WARNING: MD5 hash mismatch! Expected: {md5HashString}, Computed: {computedMd5String}");

        using var decompressedStream = new MemoryStream();
        CompressionLevel compressionLevel = CompressionLevel.Optimal;
        int offset = 0;

        while (offset + 8 <= compressedData.Length)
        {
            int chunkCompressedSize = BitConverter.ToInt32(compressedData, offset + 4);
            offset += 8;

            if (chunkCompressedSize <= 0 || offset + chunkCompressedSize > compressedData.Length)
                break;

            if (offset + 1 < compressedData.Length)
            {
                compressionLevel = compressedData[offset + 1] switch
                {
                    0x01 => CompressionLevel.Fastest,
                    0x5E => CompressionLevel.Fastest,
                    0xDA => CompressionLevel.SmallestSize,
                    _ => CompressionLevel.Optimal
                };
            }

            using var chunkStream = new MemoryStream(compressedData, offset, chunkCompressedSize);
            using var zlibStream = new ZLibStream(chunkStream, CompressionMode.Decompress);
            zlibStream.CopyTo(decompressedStream);

            offset += chunkCompressedSize;
        }

        var decompressedData = decompressedStream.ToArray();
        if (decompressedData.Length == 0)
            throw new InvalidOperationException("Could not decompress file using ZLib.");

        var content = Encoding.UTF8.GetString(decompressedData);
        return (content, decompressedData, fileData, compressionLevel, fileTypeBytes);
    }
}