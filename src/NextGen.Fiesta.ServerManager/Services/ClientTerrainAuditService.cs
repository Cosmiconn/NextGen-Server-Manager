using System.Security.Cryptography;
using System.Text;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class ClientTerrainAuditResult
{
    public ClientTerrainAuditInfo Client { get; init; } = new();
    public IReadOnlyList<ClientTerrainProfileEntry> Profiles { get; init; } = Array.Empty<ClientTerrainProfileEntry>();
    public IReadOnlyList<ClientTerrainMapEntry> Maps { get; init; } = Array.Empty<ClientTerrainMapEntry>();
    public string Summary { get; init; } = string.Empty;
}

public sealed class ClientTerrainAuditService
{
    // Public NA2016-main Fiesta.bin supplied with this project analysis.
    public const string KnownNa2016Sha256 = "01196b20abe4fb542ee8c4685f57224ef29051b2bf24196eed80a9b2a95ae4fb";

    private static readonly string[] TerrainStrings =
    {
        "HEIGHTMAP_WIDTH",
        "HEIGHTMAP_HEIGHT",
        "OneBlockWidth",
        "OneBlockHeight",
        "QuadsWide",
        "QuadsHigh",
        "HeightFileName",
        "VerTexColorTexture",
        "HeightMap",
        ".HTD"
    };

    public ClientTerrainAuditResult Analyze(string serverRoot, string? configuredClientBinary)
    {
        var path = ResolveClientBinary(serverRoot, configuredClientBinary);
        var info = InspectClient(path);
        var profiles = BuildProfiles();
        var maps = ScanClientTerrainMaps(path);

        var summary = !info.Exists
            ? "Fiesta.bin wurde nicht automatisch gefunden. Client-Binary auswählen, damit Build/PE/Hash geprüft werden können. Die Größenmatrix basiert bis dahin auf dem verifizierten NA2016-Terrainmodell."
            : info.KnownNa2016Baseline
                ? "Bekannte NA2016 Fiesta.bin erkannt. Der Terrain-Loader verwendet dynamische 32-Bit-Dimensionen/Allokationen; im Kernpfad wurde kein einfacher 512/1024/2048-Vergleich gefunden. Für einen unveränderten 2016-Client wird 512×512 als konservatives produktives Terrain-Ziel geführt. Externe 1024-Support-Arbeiten berichten von 16-bit-Koordinatenpfaden; bei 50 Units/Quad liegt die rechnerische signed-16-bit-Seitengrenze zwischen 655 und 656 Quads."
                : "Fiesta.bin gefunden, aber Hash weicht von der analysierten NA2016-Baseline ab. PE- und Terrain-Signaturen werden angezeigt; die tieferen Aussagen zum Loader gelten nur sicher für die bekannte Baseline.";

        if (maps.Count > 0)
        {
            var largest = maps.OrderByDescending(x => Math.Max(x.WorldX, x.WorldY)).First();
            summary += $" · Client-resmap: {maps.Count} HeightMap-INIs gefunden; größte erkannte Terrain-Ausdehnung {largest.MapName} = {largest.QuadsText} Quads / {largest.WorldText} Weltunits ({largest.Risk}).";
        }
        else if (info.Exists)
        {
            summary += " · Unter dem Clientpfad wurden keine HeightMap-INIs gefunden; aktuelle Client-Mapgrößen können daher noch nicht inventarisiert werden.";
        }

        return new ClientTerrainAuditResult
        {
            Client = info,
            Profiles = profiles,
            Maps = maps,
            Summary = summary
        };
    }

    public string? ResolveClientBinary(string serverRoot, string? configuredClientBinary)
    {
        if (!string.IsNullOrWhiteSpace(configuredClientBinary) && File.Exists(configuredClientBinary))
            return Path.GetFullPath(configuredClientBinary);

        if (string.IsNullOrWhiteSpace(serverRoot)) return null;
        var root = Path.GetFullPath(serverRoot);
        var parent = Directory.GetParent(root)?.FullName;
        var candidates = new List<string>
        {
            Path.Combine(root, "Fiesta.bin"),
            Path.Combine(root, "Client", "Fiesta.bin")
        };
        if (!string.IsNullOrWhiteSpace(parent))
        {
            candidates.Add(Path.Combine(parent, "Client", "Fiesta.bin"));
            candidates.Add(Path.Combine(parent, "client", "Fiesta.bin"));
            candidates.Add(Path.Combine(parent, "Fiesta.bin"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static ClientTerrainAuditInfo InspectClient(string? path)
    {
        var info = new ClientTerrainAuditInfo { ClientBinaryPath = path ?? string.Empty, Exists = !string.IsNullOrWhiteSpace(path) && File.Exists(path) };
        if (!info.Exists) return info;

        try
        {
            var bytes = File.ReadAllBytes(path!);
            info.Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            info.KnownNa2016Baseline = string.Equals(info.Sha256, KnownNa2016Sha256, StringComparison.OrdinalIgnoreCase);
            InspectPe(bytes, info);

            var ascii = Encoding.Latin1.GetString(bytes);
            info.TerrainSignatureCount = TerrainStrings.Count(x => ascii.Contains(x, StringComparison.Ordinal));
            info.HasTerrainSignatures = info.TerrainSignatureCount >= 7;

            info.StaticLoaderEvidence = info.KnownNa2016Baseline
                ? "NA2016 disassembly: WIDTH/HEIGHT werden als 32-Bit-Werte gelesen; drei width×height-Floatpuffer werden dynamisch alloziert (12 B/Punkt Baseline); HTD count wird gegen width×height validiert; Chunkraster = (width-1)/QuadsWide × (height-1)/QuadsHigh; Descriptorarray dynamisch mit 0x38 B/Chunk. Im Kernpfad kein fester 512/1024/2048-Cap gefunden."
                : info.HasTerrainSignatures
                    ? "Terrain-INI/HTD-Signaturen vorhanden. Dieser Build wurde jedoch nicht instruction-genau gegen die bekannte NA2016-Baseline verifiziert."
                    : "Zu wenige bekannte Terrain-Signaturen; keine belastbare NA2016-Terrainzuordnung.";

            info.CompatibilityAssessment = info.KnownNa2016Baseline
                ? "Format/Loader sind größer als 512 grundsätzlich dynamisch. Das bedeutet nicht, dass alle Gameplay-, Koordinaten-, NPC-, Render- und Netzwerkpfade stock-sicher sind. Die 655/656-Grenze ist eine begründete Inferenz aus 50 Units/Quad + berichteten signed-16-bit-Pfaden, kein universeller Binary-Hardcap. 1024×1024 wird ausdrücklich nicht als sichere NA2016-Produktionsgröße eingestuft."
                : "Build-Abweichung: maximale Mapgröße nicht aus der NA2016-Baseline ableiten.";
        }
        catch (Exception ex)
        {
            info.StaticLoaderEvidence = "Clientanalyse fehlgeschlagen: " + ex.Message;
        }

        return info;
    }

    private static void InspectPe(byte[] bytes, ClientTerrainAuditInfo info)
    {
        try
        {
            if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            {
                info.PeKind = "kein PE";
                return;
            }
            var pe = BitConverter.ToInt32(bytes, 0x3C);
            if (pe < 0 || pe + 24 >= bytes.Length || bytes[pe] != (byte)'P' || bytes[pe + 1] != (byte)'E')
            {
                info.PeKind = "PE ungültig";
                return;
            }

            var machine = BitConverter.ToUInt16(bytes, pe + 4);
            var characteristics = BitConverter.ToUInt16(bytes, pe + 22);
            var optionalMagic = pe + 24 + 2 <= bytes.Length ? BitConverter.ToUInt16(bytes, pe + 24) : (ushort)0;
            info.LargeAddressAware = (characteristics & 0x20) != 0;
            info.PeKind = optionalMagic == 0x10B ? $"PE32 / 0x{machine:X4}" : optionalMagic == 0x20B ? $"PE32+ / 0x{machine:X4}" : $"PE / 0x{machine:X4}";
        }
        catch
        {
            info.PeKind = "PE-Lesefehler";
        }
    }

    private static IReadOnlyList<ClientTerrainMapEntry> ScanClientTerrainMaps(string? fiestaBin)
    {
        if (string.IsNullOrWhiteSpace(fiestaBin) || !File.Exists(fiestaBin)) return Array.Empty<ClientTerrainMapEntry>();
        var clientRoot = Path.GetDirectoryName(Path.GetFullPath(fiestaBin));
        if (string.IsNullOrWhiteSpace(clientRoot)) return Array.Empty<ClientTerrainMapEntry>();
        var resmap = Path.Combine(clientRoot, "resmap");
        if (!Directory.Exists(resmap)) return Array.Empty<ClientTerrainMapEntry>();

        var result = new List<ClientTerrainMapEntry>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(resmap, "*.ini", SearchOption.AllDirectories).Take(10000).ToArray(); }
        catch { return result; }

        foreach (var file in files)
        {
            try
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var isHeightMap = false;
                foreach (var raw in File.ReadLines(file, Encoding.Latin1))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("//")) continue;
                    var comment = line.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) line = line[..comment].Trim();
                    var colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    var key = line[..colon].Trim().TrimStart('#');
                    var value = line[(colon + 1)..].Trim().Trim('"');
                    if (key.Equals("PGFILE", StringComparison.OrdinalIgnoreCase) && value.Contains("HeightMap", StringComparison.OrdinalIgnoreCase))
                        isHeightMap = true;
                    var knownKey = new[] { "HEIGHTMAP_WIDTH", "HEIGHTMAP_HEIGHT", "OneBlockWidth", "OneBlockHeight", "QuadsWide", "QuadsHigh" }
                        .FirstOrDefault(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (knownKey is not null) values[knownKey] = value;
                }

                if (!isHeightMap && !(values.ContainsKey("HEIGHTMAP_WIDTH") && values.ContainsKey("HEIGHTMAP_HEIGHT"))) continue;
                if (!TryInt(values, "HEIGHTMAP_WIDTH", out var width) || !TryInt(values, "HEIGHTMAP_HEIGHT", out var height) || width < 2 || height < 2) continue;
                TryDouble(values, "OneBlockWidth", out var blockW, 50d);
                TryDouble(values, "OneBlockHeight", out var blockH, blockW > 0 ? blockW : 50d);
                TryInt(values, "QuadsWide", out var chunkW, 64);
                TryInt(values, "QuadsHigh", out var chunkH, chunkW > 0 ? chunkW : 64);

                var qx = width - 1;
                var qy = height - 1;
                var maxQ = Math.Max(qx, qy);
                var worldMax = Math.Max(qx * blockW, qy * blockH);
                var risk = maxQ <= 512 ? "Niedrig" : worldMax <= short.MaxValue ? "Mittel" : maxQ < 1024 ? "Hoch" : "Sehr hoch";
                var notes = maxQ <= 512
                    ? "innerhalb konservativem stock-NA2016-Ziel"
                    : worldMax <= short.MaxValue
                        ? "größer als Standardziel, aber Weltseite noch ≤ signed16"
                        : maxQ < 1024
                            ? "Weltseite > signed16; Koordinatenpfade gezielt testen"
                            : "nicht stock-sicher; Client/Server/Protocol-Pfade auditieren";

                result.Add(new ClientTerrainMapEntry
                {
                    MapName = Path.GetFileNameWithoutExtension(file),
                    RelativePath = Path.GetRelativePath(clientRoot, file),
                    WidthPoints = width,
                    HeightPoints = height,
                    BlockWidth = blockW,
                    BlockHeight = blockH,
                    QuadsWide = Math.Max(1, chunkW),
                    QuadsHigh = Math.Max(1, chunkH),
                    Risk = risk,
                    Notes = notes
                });
            }
            catch
            {
                // Defekte/fremde INI nicht als Terrain behandeln.
            }
        }

        return result.OrderByDescending(x => Math.Max(x.WorldX, x.WorldY)).ThenBy(x => x.MapName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TryInt(Dictionary<string, string> values, string key, out int value, int fallback = 0)
    {
        value = fallback;
        if (!values.TryGetValue(key, out var raw)) return fallback != 0;
        raw = raw.Trim().TrimEnd('f', 'F');
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }
        value = fallback;
        return fallback != 0;
    }

    private static bool TryDouble(Dictionary<string, string> values, string key, out double value, double fallback = 0)
    {
        value = fallback;
        if (!values.TryGetValue(key, out var raw)) return fallback != 0;
        raw = raw.Trim().TrimEnd('f', 'F');
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }
        value = fallback;
        return fallback != 0;
    }

    private static IReadOnlyList<ClientTerrainProfileEntry> BuildProfiles()
    {
        var sizes = new[] { 64, 128, 256, 512, 640, 655, 656, 768, 1024, 1536, 2048, 4096 };
        return sizes.Select(BuildProfile).ToList();
    }

    private static ClientTerrainProfileEntry BuildProfile(int quads)
    {
        var points = quads + 1;
        var pointCount = (long)points * points;
        var chunksWide = (quads + 63) / 64;
        var chunks = chunksWide * chunksWide;
        var worldSide = (long)quads * 50L;
        var fitsSigned16 = worldSide <= short.MaxValue;
        var risk = quads switch
        {
            <= 512 => "Niedrig",
            <= 655 => "Mittel",
            < 1024 => "Hoch",
            _ => "Sehr hoch"
        };
        var recommendation = quads switch
        {
            <= 512 => "Konservatives NA2016-Produktionsziel. Standardgröße bleibt unter 32.767 Weltunits und ist durch bekannte 2016-Map-Workflows gut belegt.",
            <= 655 => "Experimenteller Bereich. Bei 50 Units/Quad bleibt die Seitenlänge noch innerhalb signed-16-bit; andere Arrays, Header und Gameplaypfade sind aber nicht vollständig verifiziert.",
            < 1024 => "16-bit-Risikobereich: bei 50 Units/Quad überschreitet die Weltseite 32.767. Terrainloader kann dynamisch allozieren, andere Koordinaten-/NPC-/Packetpfade können trotzdem überlaufen.",
            1024 => "Nicht stock-sicher. 51.200 Weltunits überschreiten signed-16-bit deutlich; Community-Berichte nennen starke 2016-Lags und 1024-Support-Arbeiten mussten Koordinatenpfade verbreitern.",
            _ => "Nur Engine-/Protocol-Research. Der Terrainloader ist nicht der einzige Grenzfaktor; >1024 verlangt für einen belastbaren Betrieb Client-, Server-, NPC- und Packet-Audits/Patches."
        };

        return new ClientTerrainProfileEntry
        {
            TerrainQuads = quads,
            HeightPoints = points,
            HeightPointCount = pointCount,
            WorldSideUnits = worldSide,
            ChunkCount = chunks,
            RawHeightArraysBytes = pointCount * 12L,
            HtdPayloadBytes = 4L + pointCount * 4L,
            ShbdPayloadBytes = (long)quads * quads * 8L + 8L,
            CollisionGridSide = (long)quads * 8L,
            FitsSigned16WorldSide = fitsSigned16,
            Risk = risk,
            Recommendation = recommendation
        };
    }
}
