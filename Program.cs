using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LZ4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Pfim;

namespace MetaphorAPKPack
{
    internal class Program
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct TextureEntry
        {
            public string filename;
            public int fileSize;
            public int field04;
            public int field08;
            public int field0c;
            public int field10;
            public int field14;
            public int offset;
            public int field1c;
        }

        public class TextureEntryCompressed
        {
            public string filename;
            public int fileSizeDec;
            public int fileSizeComp;
            public int pointer;
            public byte[] compFileData;
        }

        public static readonly string OutputRoot = AppContext.BaseDirectory;
        public static readonly string ExtractedRoot = Path.Combine(AppContext.BaseDirectory, "Extracted");
        public static readonly int DegreeOfParallelism = Math.Max(1, Environment.ProcessorCount);
        private static readonly ConcurrentDictionary<string, byte> usedFileNamesThisRun = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> writtenHashToPath = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<string> extractionLogLines = new ConcurrentQueue<string>();

        public static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Drag and drop an APK file, a folder full of .dds files, or a folder containing APKs (searched recursively) onto the application.\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"Repacked APKs will be written to: {OutputRoot}");
            Console.WriteLine($"Extracted DDS files will be written to: {ExtractedRoot}");

            string extractionLogPath = Path.Combine(ExtractedRoot, "ExtractionLog.txt");
            if (File.Exists(extractionLogPath))
            {
                File.Delete(extractionLogPath);
            }

            foreach (string input in args)
            {
                await ProcessInput(input);
            }

            if (!extractionLogLines.IsEmpty)
            {
                Directory.CreateDirectory(ExtractedRoot);
                File.AppendAllLines(extractionLogPath, extractionLogLines);
            }

            Console.WriteLine("Done. Press any key to exit...");
            Console.ReadKey();
        }

        public static async Task ProcessInput(string input)
        {
            if (File.Exists(input) && Path.GetExtension(input).Equals(".apk", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractApk(input);
            }
            else if (Directory.Exists(input))
            {
                if (IsDdsPackFolder(input))
                {
                    PackFolder(input);
                }
                else
                {
                    await BatchExtractApks(input);
                }
            }
            else
            {
                Console.WriteLine($"Skipping invalid input: {input}");
            }
        }

        public static bool IsDdsPackFolder(string folderPath)
        {
            return File.Exists(Path.Combine(folderPath, "FileList.txt"))
                && Directory.GetFiles(folderPath, "*.dds").Length > 0;
        }

        public static async Task BatchExtractApks(string rootPath)
        {
            Console.WriteLine($"Scanning {rootPath} for .apk files...");

            List<string> apkPaths = SafeEnumerateFiles(rootPath, "*.apk").ToList();

            if (apkPaths.Count == 0)
            {
                Console.WriteLine($"No .apk files found under {rootPath}");
                return;
            }

            Console.WriteLine($"Found {apkPaths.Count} APK file(s). Extracting with up to {DegreeOfParallelism} threads...");

            int succeeded = 0;
            int failed = 0;

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = DegreeOfParallelism };

            await Parallel.ForEachAsync(apkPaths, parallelOptions, async (apkPath, cancellationToken) =>
            {
                bool ok = await ExtractApk(apkPath);
                if (ok)
                {
                    Interlocked.Increment(ref succeeded);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            });

            Console.WriteLine($"Batch extraction finished: {succeeded} succeeded, {failed} failed.");
        }

        public static async Task<bool> ExtractApk(string apkPath)
        {
            try
            {
                Console.WriteLine($"Extracting {apkPath} -> {ExtractedRoot}");
                List<TextureEntry> structList = await ProcessAPK(apkPath);
                await DumpDDSFilesAsPng(structList, apkPath, ExtractedRoot);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to extract {apkPath}: {ex.Message}");
                return false;
            }
        }

        public static async Task ConvertAndSavePng(byte[] ddsBytes, string pngPath, CancellationToken ct = default)
        {
            using var stream = new MemoryStream(ddsBytes);
            using var dds = Pfimage.FromStream(stream);
            dds.Decompress();

            Image image;

            if (dds.Format == ImageFormat.Rgba32)
            {
                image = Image.LoadPixelData<Bgra32>(dds.Data, dds.Width, dds.Height);
            }
            else if (dds.Format == ImageFormat.Rgb24)
            {
                image = Image.LoadPixelData<Bgr24>(dds.Data, dds.Width, dds.Height);
            }
            else
            {
                throw new NotSupportedException($"Unsupported DDS pixel format: {dds.Format}");
            }

            using (image)
            {
                await image.SaveAsPngAsync(pngPath, ct);
            }
        }

        public static bool PackFolder(string folderPath)
        {
            try
            {
                string outputApkName = Path.Combine(OutputRoot, Path.GetFileName(folderPath) + ".apk");
                List<TextureEntryCompressed> structList = ProcessDDSFolder(folderPath);
                WriteDDSToAPK(structList, outputApkName);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to pack {folderPath}: {ex.Message}");
                return false;
            }
        }

        public static string GetUniqueFileName(string desiredFileName)
        {
            string candidate = desiredFileName;
            int suffix = 2;

            while (!usedFileNamesThisRun.TryAdd(candidate, 0))
            {
                string baseName = Path.GetFileNameWithoutExtension(desiredFileName);
                string extension = Path.GetExtension(desiredFileName);
                candidate = $"{baseName}_{suffix}{extension}";
                suffix++;
            }

            return candidate;
        }

        public static IEnumerable<string> SafeEnumerateFiles(string rootPath, string searchPattern)
        {
            var pendingDirs = new Stack<string>();
            pendingDirs.Push(rootPath);

            while (pendingDirs.Count > 0)
            {
                string currentDir = pendingDirs.Pop();
                string[] subDirs = Array.Empty<string>();
                string[] matchedFiles = Array.Empty<string>();

                try
                {
                    matchedFiles = Directory.GetFiles(currentDir, searchPattern);
                    subDirs = Directory.GetDirectories(currentDir);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (string file in matchedFiles)
                {
                    yield return file;
                }

                foreach (string subDir in subDirs)
                {
                    pendingDirs.Push(subDir);
                }
            }
        }

        public static async Task<List<TextureEntry>> ProcessAPK(string apkPath)
        {
            List<TextureEntry> TextureEntries = new List<TextureEntry>();

            using (FileStream fs = new FileStream(apkPath, FileMode.Open, FileAccess.Read))
            {
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    fs.Seek(0x8, SeekOrigin.Begin);

                    var numOfBlocks = reader.ReadInt32();
                    var dummy = reader.ReadInt32();

                    for (int i = 0; i < numOfBlocks; i++)
                    {
                        TextureEntry data = new TextureEntry();

                        // Read name as byte array and convert to string
                        byte[] nameBytes = reader.ReadBytes(0x100);
                        data.filename = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

                        // Read remaining fields
                        data.fileSize = reader.ReadInt32();
                        data.field04 = reader.ReadInt32();
                        data.field08 = reader.ReadInt32();
                        data.field0c = reader.ReadInt32();
                        data.field10 = reader.ReadInt32();
                        data.field14 = reader.ReadInt32();
                        data.offset = reader.ReadInt32();
                        data.field1c = reader.ReadInt32();

                        // Add to the list
                        TextureEntries.Add(data);
                    }
                }
            }

            foreach (var data in TextureEntries)
            {
                Console.WriteLine($"Name: {data.filename}, FileSize: 0x{data.fileSize:X8}, Offset: 0x{data.offset:X8}");
            }

            return TextureEntries;
        }

        public static async Task DumpDDSFilesAsPng(List<TextureEntry> TextureEntries, string filePath, string outputDir)
        {
            var toConvert = new List<(string savedFileName, string outputFilePath, byte[] ddsData)>();

            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                Directory.CreateDirectory(outputDir);

                foreach (var data in TextureEntries)
                {
                    string originalFileName = Path.GetFileName(data.filename.TrimEnd('\0'));

                    fs.Seek(data.offset, SeekOrigin.Begin);

                    byte[] compressedFile = new byte[data.fileSize];
                    await fs.ReadAsync(compressedFile, 0, data.fileSize);

                    int decompressedSize = BitConverter.ToInt32(compressedFile, 0x0C);
                    int compressedSize = BitConverter.ToInt32(compressedFile, 0x20);

                    byte[] compressedData = new byte[compressedSize];
                    Array.Copy(compressedFile, 0x30, compressedData, 0, compressedSize);

                    byte[] decompressedData = LZ4Codec.Decode(compressedData, 0, compressedSize, decompressedSize);

                    string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(decompressedData));

                    if (writtenHashToPath.ContainsKey(hash))
                    {
                        Console.WriteLine($"Skipping {originalFileName} (duplicate of {Path.GetFileName(writtenHashToPath[hash])})");
                        continue;
                    }

                    string savedFileName = Path.ChangeExtension(GetUniqueFileName(originalFileName), ".png");
                    string outputFilePath = Path.Combine(outputDir, savedFileName);

                    if (!writtenHashToPath.TryAdd(hash, outputFilePath))
                    {
                        Console.WriteLine($"Skipping {originalFileName} (duplicate of {Path.GetFileName(writtenHashToPath[hash])})");
                        continue;
                    }

                    extractionLogLines.Enqueue($"{savedFileName}\t{Path.GetFileName(filePath)}\t{originalFileName}");
                    toConvert.Add((savedFileName, outputFilePath, decompressedData));
                }
            }

            Console.WriteLine($"Converting {toConvert.Count} file(s) to PNG with up to {DegreeOfParallelism} threads...");

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = DegreeOfParallelism };

            await Parallel.ForEachAsync(toConvert, parallelOptions, async (entry, ct) =>
            {
                try
                {
                    await ConvertAndSavePng(entry.ddsData, entry.outputFilePath, ct);
                    Console.WriteLine($"Saved {entry.savedFileName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Skipping {entry.savedFileName}: {ex.Message}");
                }
            });
        }

        public static List<TextureEntryCompressed> ProcessDDSFolder(string folderPath)
        {
            List<TextureEntryCompressed> structList = new List<TextureEntryCompressed>();

            // Get all .dds files in the folder
            string[] ddsFiles = Directory.GetFiles(folderPath, "*.dds");

            List<string> FileList = new List<string>(File.ReadAllLines(Path.Combine(folderPath, "FileList.txt")));

            foreach (var ddsFile in ddsFiles)
            {
                FileInfo fileInfo = new FileInfo(ddsFile);
                byte[] fileData = File.ReadAllBytes(ddsFile);

                TextureEntryCompressed data = new TextureEntryCompressed
                {
                    filename = fileInfo.Name,
                    fileSizeDec = fileData.Length,
                    compFileData = fileData,
                };

                structList.Add(data);
            }

            structList.Sort((x, y) => FileList.IndexOf(x.filename).CompareTo(FileList.IndexOf(y.filename)));

            return structList;
        }


        public static void WriteDDSToAPK(List<TextureEntryCompressed> structList, string outputFilePath)
        {
            Console.WriteLine($"Creating Directory {Path.GetDirectoryName(outputFilePath)}");
            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);

            List<TextureEntryCompressed> CompressedDataHolder = new List<TextureEntryCompressed>();

            foreach (var data in structList)
            {
                Console.WriteLine($"Compressing file {data.filename}");

                byte[] compressedData = LZ4Codec.EncodeHC(data.compFileData, 0, data.fileSizeDec);

                int paddingSize = (0x10 - (compressedData.Length % 0x10)) % 0x10;
                byte[] paddedCompressedData = new byte[compressedData.Length + paddingSize];

                Buffer.BlockCopy(compressedData, 0, paddedCompressedData, 0, compressedData.Length);

                byte[] header = new byte[0x30];
                Buffer.BlockCopy(BitConverter.GetBytes(0x305A5A5A), 0, header, 0x0, 4);  // ZZZ0 Magic
                Buffer.BlockCopy(BitConverter.GetBytes(0x010001), 0, header, 0x4, 4);    // Unknown, Bitfield?
                Buffer.BlockCopy(BitConverter.GetBytes(data.fileSizeDec), 0, header, 0xC, 4);  // Decompressed size
                Buffer.BlockCopy(BitConverter.GetBytes(compressedData.Length + 0x30), 0, header, 0x10, 4);  // 0x10
                Buffer.BlockCopy(BitConverter.GetBytes(compressedData.Length), 0, header, 0x20, 4);  // Compressed size
                Buffer.BlockCopy(BitConverter.GetBytes(0x30), 0, header, 0x24, 4);  // Header size

                byte[] outputData = new byte[header.Length + paddedCompressedData.Length];
                Buffer.BlockCopy(header, 0, outputData, 0, header.Length);
                Buffer.BlockCopy(paddedCompressedData, 0, outputData, header.Length, paddedCompressedData.Length);

                CompressedDataHolder.Add(new TextureEntryCompressed
                {
                    filename = data.filename,
                    fileSizeDec = data.fileSizeDec,
                    fileSizeComp = paddedCompressedData.Length,
                    compFileData = outputData,
                });
            }

            using (FileStream fs = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                writer.Write(0x4B434150);
                writer.Write(0x10000);
                writer.Write(CompressedDataHolder.Count);
                writer.Write(0);

                foreach (var data in CompressedDataHolder)
                {
                    writer.Write(new byte[0x120]); // fill dummy headers for pointers
                }

                for (int i = 0; i < CompressedDataHolder.Count; i++)
                {
                    CompressedDataHolder[i].pointer = (int)writer.BaseStream.Position;
                    writer.Write(CompressedDataHolder[i].compFileData);
                }

                writer.Seek(0x10, SeekOrigin.Begin);

                foreach (var data in CompressedDataHolder) // write headers now that we have pointers
                {
                    byte[] nameBytes = Encoding.ASCII.GetBytes(data.filename.PadRight(0x100, '\0'));
                    writer.Write(nameBytes);

                    writer.Write(data.compFileData.Length);
                    writer.Write(0); // 0x04
                    writer.Write(0); // 0x08
                    writer.Write(0); // 0x0C
                    writer.Write(0); // 0x10
                    writer.Write(0); // 0x14
                    writer.Write(data.pointer); // 0x18
                    writer.Write(0); // 0x1C
                }

                Console.WriteLine($"APK created: {outputFilePath}");
            }
        }

    }
}
