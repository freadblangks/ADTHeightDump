using CASCLib;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ADTHeightDump
{
    [Serializable]
    public class TextureInfo
    {
        public int Scale { get; set; }
        public float HeightScale { get; set; }
        public float HeightOffset { get; set; }
    }

    [Serializable]
    public class TextureInfoMeta
    {
        public Dictionary<uint, TextureInfo> TextureInfoByFileId { get; set; } = new Dictionary<uint, TextureInfo>();
        public Dictionary<string, TextureInfo> TextureInfoByFilePath { get; set; } = new Dictionary<string, TextureInfo>();
    }

    internal class Program
    {
        private static string _outputFolder = "." + Path.DirectorySeparatorChar;
        private static string _listFileUrl = "https://github.com/wowdev/wow-listfile/releases/latest/download/community-listfile.csv";

        private static string _textureInfoMetaFileName = Path.DirectorySeparatorChar + "TextureInfoMeta.json";
        private static string _textureInfoByFileIdFileName = Path.DirectorySeparatorChar + "TextureInfoByFileId.json";
        private static string _textureInfoByFilePathFileName = Path.DirectorySeparatorChar + "TextureInfoByFilePath.json";

        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: ADTHeightDump.exe <wowProd> (outputFile)\nExample online mode: ADTHeightDump.exe wowt TextureSettings.json");
                Environment.Exit(-1);
            }

            var wowProd = args[0];
            if (args.Length == 2 && !string.IsNullOrEmpty(args[1]))
                _outputFolder = args[1];

            _outputFolder = _outputFolder.TrimEnd(new char[] { '/', '\\' });

            if (!Directory.Exists(_outputFolder))
                Directory.CreateDirectory(_outputFolder);

            var textureInfoMetaFilePath = _outputFolder + _textureInfoMetaFileName;
            var texDetails = new TextureInfoMeta();
            if (File.Exists(textureInfoMetaFilePath))
                texDetails = JsonSerializer.Deserialize<TextureInfoMeta>(File.ReadAllText(textureInfoMetaFilePath));

            // Download listfile if it doesn't exist or older than 6 hours
            if (!File.Exists("listfile.csv") || (DateTime.Now - File.GetLastWriteTime("listfile.csv")).TotalHours >= 6)
                DownloadListFile();

            // Load TACT keys in case there are encrypted maps
            CASC.LoadKeys();

            CASC.InitCasc(null, wowProd);

            foreach (var file in CASC.Listfile.Where(x => x.Value.EndsWith("tex0.adt")))
            {

                if (!CASC.FileExists((uint)file.Key))
                {
                    Console.WriteLine("File " + file.Key + " " + file.Value + " does not exist, skipping..");
                    continue;
                }

                var adtfile = new ADT();

                using (var ms = new MemoryStream())
                using (var bin = new BinaryReader(ms))
                {
                    try
                    {
                        CASC.GetFileByID((uint)file.Key).CopyTo(ms);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Error opening file " + file.Key + ": " + file.Value + " because of " + e.Message + ", skipping..");
                        continue;
                    }
                    ms.Position = 0;

                    long position = 0;

                    while (position < ms.Length)
                    {
                        ms.Position = position;
                        var chunkName = (ADTChunks)bin.ReadUInt32();
                        var chunkSize = bin.ReadUInt32();

                        position = ms.Position + chunkSize;
                        switch (chunkName)
                        {
                            case ADTChunks.MVER:
                                if (bin.ReadUInt32() != 18)
                                    throw new Exception("Unsupported ADT version!");
                                break;
                            case ADTChunks.MTEX:
                                adtfile.textures = ReadMTEXChunk(chunkSize, bin);
                                break;
                            case ADTChunks.MTXP:
                                adtfile.texParams = ReadMTXPChunk(chunkSize, bin);
                                break;
                            case ADTChunks.MHID: // Height texture fileDataIDs
                                adtfile.heightTextureFileDataIDs = ReadFileDataIDChunk(chunkSize, bin);
                                break;
                            case ADTChunks.MDID: // Diffuse texture fileDataIDs
                                adtfile.diffuseTextureFileDataIDs = ReadFileDataIDChunk(chunkSize, bin);
                                break;
                            case ADTChunks.MCNK:
                            case ADTChunks.MAMP:
                                break;
                            default:
                                Console.WriteLine(string.Format("Found unknown header at offset {1} \"{0}\" while we should've already read them all!", chunkName, position));
                                break;
                        }
                    }
                }

                if (adtfile.texParams != null)
                {
                    Console.WriteLine(file.Key + " " + file.Value);
                    for (var i = 0; i < adtfile.texParams.Length; i++)
                    {
                        var filename = "";

                        var mtxp = adtfile.texParams[i];
                        if (adtfile.diffuseTextureFileDataIDs != null && adtfile.diffuseTextureFileDataIDs[i] != 0)
                            filename = CASC.Listfile[(int)adtfile.diffuseTextureFileDataIDs[i]].Replace("_s.blp", ".blp");

                        if (adtfile.textures.filenames != null)
                            filename = adtfile.textures.filenames[i];

                        if (mtxp.height == 0 && mtxp.offset == 1)
                            continue;

                        if (adtfile.heightTextureFileDataIDs == null || adtfile.heightTextureFileDataIDs[i] == 0)
                            continue;

                        texDetails.TextureInfoByFilePath[filename] = new TextureInfo
                        {
                            Scale = (int)(mtxp.flags >> 4),
                            HeightScale = mtxp.height,
                            HeightOffset = mtxp.offset
                        };

                        texDetails.TextureInfoByFileId[adtfile.heightTextureFileDataIDs[i]] = new TextureInfo
                        {
                            Scale = (int)(mtxp.flags >> 4),
                            HeightScale = mtxp.height,
                            HeightOffset = mtxp.offset
                        };
                    }
                }

                // TESTING
                if (texDetails.TextureInfoByFilePath.Count >= 1)
                    break;

                // save every 25 entries
                if (texDetails.TextureInfoByFilePath.Count > 0 && texDetails.TextureInfoByFilePath.Count % 25 == 0)
                    SaveTextureSettings(texDetails);
            }

            Console.WriteLine("Adding tilesets from listfile starting with tileset and ending in _h.blp with default values (can be wrong)");
            foreach (var file in CASC.Listfile.Where(x => x.Value.EndsWith("_h.blp") && x.Value.StartsWith("tileset")))
            {
                var basename = file.Value.Replace("_h.blp", ".blp");
                TextureInfo textureInfo = new TextureInfo
                {
                    Scale = 1,
                    HeightScale = 6,
                    HeightOffset = 1
                };

                if (!texDetails.TextureInfoByFilePath.ContainsKey(basename))
                {
                    Console.WriteLine("Adding " + file.Value + " from listfile as " + basename);
                    texDetails.TextureInfoByFilePath[basename] = textureInfo;
                }

                if (!texDetails.TextureInfoByFileId.ContainsKey((uint)file.Key))
                {
                    Console.WriteLine("Adding " + file.Value + " from listfile as " + basename);
                    texDetails.TextureInfoByFileId[(uint)file.Key] = textureInfo;
                }
            }

            SaveTextureSettings(texDetails);
        }

        private static MTEX ReadMTEXChunk(uint size, BinaryReader bin)
        {
            var txchunk = new MTEX();

            //List of BLP filenames
            var blpFilesChunk = bin.ReadBytes((int)size);
            var blpFiles = new List<string>();
            var str = new StringBuilder();

            for (var i = 0; i < blpFilesChunk.Length; i++)
            {
                if (blpFilesChunk[i] == '\0')
                {
                    blpFiles.Add(str.ToString());
                    str = new StringBuilder();
                }
                else
                {
                    str.Append((char)blpFilesChunk[i]);
                }
            }

            txchunk.filenames = blpFiles.ToArray();
            return txchunk;
        }

        private static uint[] ReadFileDataIDChunk(uint size, BinaryReader bin)
        {
            var count = size / 4;
            var filedataids = new uint[count];
            for (var i = 0; i < count; i++)
            {
                filedataids[i] = bin.ReadUInt32();
            }
            return filedataids;
        }

        private static MTXP[] ReadMTXPChunk(uint size, BinaryReader bin)
        {
            var count = size / 16;

            var txparams = new MTXP[count];

            for (var i = 0; i < count; i++)
            {
                txparams[i] = bin.Read<MTXP>();
            }

            return txparams;
        }

        private static void DownloadListFile()
        {
            Console.WriteLine("Downloading listfile from " + _listFileUrl);
            using (var stream = new WebClient().OpenRead(_listFileUrl))
            using (var streamReader = new StreamReader(stream))
            {
                File.WriteAllText("listfile.csv", streamReader.ReadToEnd());
            }
        }

        private static void SaveTextureSettings(TextureInfoMeta texDetails)
        {
            Console.WriteLine("Saving " + texDetails.TextureInfoByFileId.Count + " texture settings to " + _outputFolder);
            var textureInfoMetaJson = JsonSerializer.Serialize(texDetails, new JsonSerializerOptions { WriteIndented = true});
            File.WriteAllText(_outputFolder + _textureInfoMetaFileName, textureInfoMetaJson);

            var textureInfoByFileIdJson = JsonSerializer.Serialize(texDetails.TextureInfoByFileId, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_outputFolder + _textureInfoByFileIdFileName, textureInfoByFileIdJson);

            var textureInfoByFilePathJson = JsonSerializer.Serialize(texDetails.TextureInfoByFilePath, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_outputFolder + _textureInfoByFilePathFileName, textureInfoByFilePathJson);

        }
    }
}