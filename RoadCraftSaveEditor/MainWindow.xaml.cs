using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace RoadCraftSaveEditor;

/// <summary>
/// Stores information about the loaded save file for later saving
/// </summary>
public class SaveFileData
{
    public required string FilePath { get; set; }
    public required string Content { get; set; }
    /// <summary>
    /// Original decompressed bytes - used to detect if content was modified
    /// </summary>
    public required byte[] OriginalDecompressedBytes { get; set; }
    /// <summary>
    /// Original compressed file bytes - used for byte-perfect save when unmodified
    /// </summary>
    public required byte[] OriginalCompressedBytes { get; set; }
    /// <summary>
    /// The compression level detected from the original file
    /// </summary>
    public required CompressionLevel OriginalCompressionLevel { get; set; }
    /// <summary>
    /// The 4-byte file type from the header
    /// </summary>
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

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private SaveFileData? _currentSaveFile;
    private int _lastSearchIndex = -1;

    public ICommand FocusSearchCommand { get; }

    public MainWindow()
    {
        FocusSearchCommand = new RelayCommand(() => SearchTextBox.Focus());
        InitializeComponent();
        DataContext = this;
    }

    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
                FindPrevious();
            else
                FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ContentTextBox.Focus();
            e.Handled = true;
        }
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e) => FindNext();
    private void FindPreviousButton_Click(object sender, RoutedEventArgs e) => FindPrevious();

    private void FindNext()
    {
        var searchText = SearchTextBox.Text;
        if (string.IsNullOrEmpty(searchText))
        {
            SearchStatusText.Text = "";
            return;
        }

        var content = ContentTextBox.Text;
        var comparison = CaseSensitiveCheckBox.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // Start searching from after the current selection
        int startIndex = _lastSearchIndex >= 0 ? _lastSearchIndex + 1 : 0;
        if (startIndex >= content.Length) startIndex = 0;

        int foundIndex = content.IndexOf(searchText, startIndex, comparison);

        // Wrap around if not found
        if (foundIndex < 0 && startIndex > 0)
        {
            foundIndex = content.IndexOf(searchText, 0, comparison);
            if (foundIndex >= 0)
                SearchStatusText.Text = "Wrapped to beginning";
        }

        if (foundIndex >= 0)
        {
            SelectAndScrollTo(foundIndex, searchText.Length);
            _lastSearchIndex = foundIndex;
            if (SearchStatusText.Text != "Wrapped to beginning")
                SearchStatusText.Text = $"Found at position {foundIndex}";
        }
        else
        {
            SearchStatusText.Text = "Not found";
            _lastSearchIndex = -1;
        }
    }

    private void FindPrevious()
    {
        var searchText = SearchTextBox.Text;
        if (string.IsNullOrEmpty(searchText))
        {
            SearchStatusText.Text = "";
            return;
        }

        var content = ContentTextBox.Text;
        var comparison = CaseSensitiveCheckBox.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // Start searching from before the current selection
        int startIndex = _lastSearchIndex > 0 ? _lastSearchIndex - 1 : content.Length - 1;

        int foundIndex = content.LastIndexOf(searchText, startIndex, comparison);

        // Wrap around if not found
        if (foundIndex < 0 && startIndex < content.Length - 1)
        {
            foundIndex = content.LastIndexOf(searchText, content.Length - 1, comparison);
            if (foundIndex >= 0)
                SearchStatusText.Text = "Wrapped to end";
        }

        if (foundIndex >= 0)
        {
            SelectAndScrollTo(foundIndex, searchText.Length);
            _lastSearchIndex = foundIndex;
            if (SearchStatusText.Text != "Wrapped to end")
                SearchStatusText.Text = $"Found at position {foundIndex}";
        }
        else
        {
            SearchStatusText.Text = "Not found";
            _lastSearchIndex = -1;
        }
    }

    private void SelectAndScrollTo(int index, int length)
    {
        ContentTextBox.Focus();
        ContentTextBox.Select(index, length);

        // Scroll to make the selection visible
        var lineIndex = ContentTextBox.GetLineIndexFromCharacterIndex(index);
        ContentTextBox.ScrollToLine(lineIndex);
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

                _currentSaveFile = new SaveFileData
                {
                    FilePath = filePath,
                    Content = content,
                    OriginalDecompressedBytes = decompressedBytes,
                    OriginalCompressedBytes = compressedBytes,
                    OriginalCompressionLevel = compressionLevel,
                    FileTypeBytes = fileTypeBytes
                };

                ContentTextBox.Text = content;
                SaveFileButton.IsEnabled = true;

                // Also log to debug output
                Debug.WriteLine("=== Decompressed Save File Contents ===");
                Debug.WriteLine(content);
                Debug.WriteLine("=== End of Contents ===");
            }
            catch (Exception ex)
            {
                _currentSaveFile = null;
                SaveFileButton.IsEnabled = false;
                ContentTextBox.Text = $"Error loading file: {ex.Message}\n\n{ex.StackTrace}";
                Debug.WriteLine($"Error loading file: {ex}");
            }
        }
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
                // Update content from TextBox (in case it was modified)
                _currentSaveFile.Content = ContentTextBox.Text;

                CompressAndSaveFile(saveFileDialog.FileName, _currentSaveFile);
                MessageBox.Show("File saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Debug.WriteLine($"Error saving file: {ex}");
            }
        }
    }

    private static void CompressAndSaveFile(string filePath, SaveFileData saveData)
    {
        var contentBytes = Encoding.UTF8.GetBytes(saveData.Content);
        var level = saveData.OriginalCompressionLevel;
        using var compressedStream = new MemoryStream();

        // Compress in 1 MB chunks
        // Each chunk format: 4 bytes uncompressed size, 4 bytes compressed size, zlib header, deflate data, adler32
        const int chunkSize = 1024 * 1024; // 1 MB
        int offset = 0;
        int chunkCount = 0;

        while (offset < contentBytes.Length)
        {
            int bytesToCompress = Math.Min(chunkSize, contentBytes.Length - offset);
            var chunkData = new byte[bytesToCompress];
            Array.Copy(contentBytes, offset, chunkData, 0, bytesToCompress);

            // Compress this chunk to a temporary stream
            using var zlibDataStream = new MemoryStream();

            // Write zlib header
            zlibDataStream.WriteByte(0x78);
            zlibDataStream.WriteByte(0x9C);

            // Write deflate compressed data
            using (var deflateStream = new DeflateStream(zlibDataStream, level, leaveOpen: true))
            {
                deflateStream.Write(chunkData, 0, chunkData.Length);
            }

            // Append Adler-32 checksum (big-endian)
            var adler32 = ComputeAdler32(chunkData);
            zlibDataStream.WriteByte((byte)(adler32 >> 24));
            zlibDataStream.WriteByte((byte)(adler32 >> 16));
            zlibDataStream.WriteByte((byte)(adler32 >> 8));
            zlibDataStream.WriteByte((byte)adler32);

            var zlibData = zlibDataStream.ToArray();

            // Write chunk header: 4 bytes uncompressed size + 4 bytes zlib data size
            compressedStream.Write(BitConverter.GetBytes(bytesToCompress), 0, 4);
            compressedStream.Write(BitConverter.GetBytes(zlibData.Length), 0, 4);
            // Write the zlib data
            compressedStream.Write(zlibData, 0, zlibData.Length);

            offset += bytesToCompress;
            chunkCount++;
        }

        Debug.WriteLine($"Wrote {chunkCount} chunks, total uncompressed={contentBytes.Length}");

        var compressedBytes = compressedStream.ToArray();
        
        // Build the output file with header:
        // 4 bytes file type, 4 bytes compressed length, 4 zero bytes, 4 bytes uncompressed length, 4 zero bytes,
        // MD5 hash (UTF8 hex string) of compressed data, single byte '3', compressed data
        using var outputStream = new MemoryStream();
        
        // File type (4 bytes)
        outputStream.Write(saveData.FileTypeBytes, 0, 4);
        
        // Compressed data length (4 bytes, little-endian)
        outputStream.Write(BitConverter.GetBytes(compressedBytes.Length), 0, 4);
        
        // 4 zero bytes
        outputStream.Write(new byte[4], 0, 4);
        
        // Uncompressed data length (4 bytes, little-endian)
        outputStream.Write(BitConverter.GetBytes(contentBytes.Length), 0, 4);
        
        // 4 zero bytes
        outputStream.Write(new byte[4], 0, 4);
        
        // MD5 hash of compressed data (UTF8 hex string)
        var md5Hash = MD5.HashData(compressedBytes);
        var md5HexString = Convert.ToHexString(md5Hash).ToLowerInvariant();
        var md5Bytes = Encoding.UTF8.GetBytes(md5HexString);
        outputStream.Write(md5Bytes, 0, md5Bytes.Length);
        
        // Single byte 3
        outputStream.WriteByte(0x03);
        
        // Compressed data
        outputStream.Write(compressedBytes, 0, compressedBytes.Length);

        File.WriteAllBytes(filePath, outputStream.ToArray());
        Debug.WriteLine($"Saved file using ZLib at level {level}: {filePath} ({outputStream.Length} bytes)");
    }

    /// <summary>
    /// Computes the Adler-32 checksum of the given data.
    /// Used for zlib format which requires this checksum at the end.
    /// </summary>
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

        // Log file header for analysis
        Debug.WriteLine("=== File Header Analysis ===");
        Debug.WriteLine($"File size: {fileData.Length} bytes");
        Debug.WriteLine($"First 64 bytes (hex): {BitConverter.ToString(fileData.Take(64).ToArray())}");
        Debug.WriteLine($"First 64 bytes (ASCII): {Encoding.ASCII.GetString(fileData.Take(64).Select(b => b >= 32 && b < 127 ? b : (byte)'.').ToArray())}");

        // Parse the custom header format:
        // 4 bytes file type, 4 bytes compressed length, 4 zero bytes, 4 bytes uncompressed length, 4 zero bytes,
        // MD5 hash (32 bytes UTF8 hex string), single byte '3', compressed data
        const int headerSize = 4 + 4 + 4 + 4 + 4 + 32 + 1; // = 53 bytes
        
        if (fileData.Length < headerSize)
        {
            throw new InvalidOperationException($"File is too small to contain valid header. Expected at least {headerSize} bytes, got {fileData.Length}");
        }

        // Extract header fields
        var fileTypeBytes = fileData.Take(4).ToArray();
        var compressedLength = BitConverter.ToInt32(fileData, 4);
        // bytes 8-11 are zeros
        var uncompressedLength = BitConverter.ToInt32(fileData, 12);
        // bytes 16-19 are zeros
        var md5HashString = Encoding.UTF8.GetString(fileData, 20, 32);
        var markerByte = fileData[52];
        
        Debug.WriteLine($"File type bytes: {BitConverter.ToString(fileTypeBytes)}");
        Debug.WriteLine($"Compressed length: {compressedLength}");
        Debug.WriteLine($"Uncompressed length: {uncompressedLength}");
        Debug.WriteLine($"MD5 hash: {md5HashString}");
        Debug.WriteLine($"Marker byte: 0x{markerByte:X2} ('{(char)markerByte}')");

        // Extract the compressed data (everything after the header)
        var compressedData = new byte[fileData.Length - headerSize];
        Array.Copy(fileData, headerSize, compressedData, 0, compressedData.Length);
        
        Debug.WriteLine($"Actual compressed data size: {compressedData.Length}");
        
        // Verify MD5 hash
        var computedMd5 = MD5.HashData(compressedData);
        var computedMd5String = Convert.ToHexString(computedMd5).ToLowerInvariant();
        if (!string.Equals(md5HashString, computedMd5String, StringComparison.OrdinalIgnoreCase))
        {
            Debug.WriteLine($"WARNING: MD5 hash mismatch! Expected: {md5HashString}, Computed: {computedMd5String}");
        }
        else
        {
            Debug.WriteLine("MD5 hash verified successfully");
        }

        // Decompress chunked ZLib data
        // Each chunk format: 4 bytes uncompressed size, 4 bytes compressed size, zlib data
        using var decompressedStream = new MemoryStream();
        CompressionLevel compressionLevel = CompressionLevel.Optimal;
        int offset = 0;
        int chunkCount = 0;

        while (offset + 8 <= compressedData.Length)
        {
            // Read chunk header
            int chunkUncompressedSize = BitConverter.ToInt32(compressedData, offset);
            int chunkCompressedSize = BitConverter.ToInt32(compressedData, offset + 4);
            offset += 8;

            if (chunkCompressedSize <= 0 || offset + chunkCompressedSize > compressedData.Length)
            {
                Debug.WriteLine($"Invalid chunk at offset {offset - 8}: uncompressed={chunkUncompressedSize}, compressed={chunkCompressedSize}");
                break;
            }

            // Detect compression level from first chunk's zlib header
            if (chunkCount == 0 && chunkCompressedSize >= 2)
            {
                compressionLevel = compressedData[offset + 1] switch
                {
                    0x01 => CompressionLevel.Fastest,
                    0x5E => CompressionLevel.Fastest,
                    0xDA => CompressionLevel.SmallestSize,
                    _ => CompressionLevel.Optimal
                };
                Debug.WriteLine($"Detected compression level from zlib header: {compressionLevel}");
            }

            // Decompress this chunk
            using var chunkStream = new MemoryStream(compressedData, offset, chunkCompressedSize);
            using var zlibStream = new ZLibStream(chunkStream, CompressionMode.Decompress);
            zlibStream.CopyTo(decompressedStream);

            offset += chunkCompressedSize;
            chunkCount++;
        }

        var decompressedData = decompressedStream.ToArray();

        if (decompressedData.Length == 0)
        {
            throw new InvalidOperationException(
                "Could not decompress file using ZLib.\n" +
                "The file may be corrupted or use a different compression format.");
        }

        Debug.WriteLine($"Successfully decompressed {chunkCount} chunks using ZLib");
        Debug.WriteLine($"Decompressed size: {decompressedData.Length} bytes");
        Debug.WriteLine($"Compression level: {compressionLevel}");

        // Decode as UTF-8 text
        var content = Encoding.UTF8.GetString(decompressedData);

        return (content, decompressedData, fileData, compressionLevel, fileTypeBytes);
    }
}