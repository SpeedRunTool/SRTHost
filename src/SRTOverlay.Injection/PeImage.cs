using System.Buffers.Binary;

namespace SRTOverlay.Injection;

/// <summary>
/// Just enough PE reading to answer two questions about the shim on disk: what architecture it is,
/// and where one of its exports lives.
/// </summary>
/// <remarks>
/// <para>
/// The alternative - <c>LoadLibraryEx</c> the shim into the injecting process and call
/// <c>GetProcAddress</c> - is shorter and wrong. It maps a native DLL built for a game process into
/// the runner, runs its entry point, and would have the runner carry a loaded copy of the very
/// binary whose whole design goal is to exist in exactly one place. Reading the file is a hundred
/// lines and has none of those properties.
/// </para>
/// <para>
/// The export's RVA is what the injector needs: the remote module's base plus this RVA is the
/// address to create the second thread on. Relocation does not enter into it, because an RVA is
/// relative to whatever base the loader chose.
/// </para>
/// </remarks>
public static class PeImage
{
    private const ushort DosSignature = 0x5A4D;          // 'MZ'
    private const uint PeSignature = 0x00004550;         // 'PE\0\0'
    private const ushort Pe32Magic = 0x010B;
    private const ushort Pe32PlusMagic = 0x020B;

    /// <summary>x86-64.</summary>
    public const ushort MachineAmd64 = 0x8664;

    /// <summary>x86.</summary>
    public const ushort MachineI386 = 0x014C;

    /// <summary>What <see cref="Read"/> found.</summary>
    /// <param name="Machine">COFF machine type; compare against <see cref="MachineAmd64"/>.</param>
    /// <param name="Exports">Exported name to RVA, ordinal-only exports omitted.</param>
    public readonly record struct PeInfo(ushort Machine, IReadOnlyDictionary<string, uint> Exports);

    /// <summary>Read the machine type and named exports of a PE file.</summary>
    /// <exception cref="BadImageFormatException">The file is not a PE image this can read.</exception>
    public static PeInfo Read(string path)
    {
        byte[] image = File.ReadAllBytes(path);
        Headers headers = ReadHeaders(image, path);

        (uint exportRva, uint exportSize) = headers.Directory(image, DirectoryExport);

        Dictionary<string, uint> exports = exportRva == 0 || exportSize == 0
            ? []
            : ReadExports(image, ReadSections(image, headers), exportRva);

        return new PeInfo(headers.Machine, exports);
    }

    /// <summary>
    /// The PKCS#7 blob of an Authenticode signature embedded in a PE, or <see langword="null"/> when
    /// the file is unsigned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the framework API that used to answer "who signed this file" -
    /// <c>X509Certificate.CreateFromSignedFile</c> - is obsolete as of SYSLIB0057, and its
    /// replacement <c>X509CertificateLoader</c> deliberately loads certificate blobs rather than
    /// digging one out of a PE. Reading it here costs a directory lookup, because the file is already
    /// being parsed for its exports.
    /// </para>
    /// <para>
    /// Unlike every other data directory, the certificate table's address is a <b>file offset</b> and
    /// not an RVA - the signature is not mapped into memory when the image loads. Treating it as an
    /// RVA is the classic bug here and it produces a plausible-looking offset in the wrong place.
    /// </para>
    /// </remarks>
    public static byte[]? ReadSignatureBlob(string path)
    {
        byte[] image = File.ReadAllBytes(path);
        Headers headers = ReadHeaders(image, path);

        (uint offset, uint size) = headers.Directory(image, DirectoryCertificate);
        if (offset == 0 || size < WinCertificateHeaderSize || offset + size > (uint)image.Length)
            return null;

        // WIN_CERTIFICATE: dwLength, wRevision, wCertificateType, then the DER PKCS#7.
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)offset));
        if (length <= WinCertificateHeaderSize || offset + length > (uint)image.Length)
            return null;

        return image.AsSpan((int)offset + WinCertificateHeaderSize, (int)(length - WinCertificateHeaderSize)).ToArray();
    }

    private const int DirectoryExport = 0;
    private const int DirectoryCertificate = 4;
    private const int WinCertificateHeaderSize = 8;

    /// <summary>Where the interesting parts of a PE's headers are, once located.</summary>
    private readonly record struct Headers(ushort Machine, int Optional, ushort OptionalSize, ushort SectionCount, int Directories)
    {
        /// <summary>Address and size of one data directory entry.</summary>
        internal (uint Address, uint Size) Directory(ReadOnlySpan<byte> span, int index)
        {
            int entry = Directories + (index * 8);
            return entry + 8 > span.Length
                ? (0, 0)
                : (BinaryPrimitives.ReadUInt32LittleEndian(span[entry..]),
                   BinaryPrimitives.ReadUInt32LittleEndian(span[(entry + 4)..]));
        }
    }

    private static Headers ReadHeaders(ReadOnlySpan<byte> span, string path)
    {
        if (span.Length < 0x40 || BinaryPrimitives.ReadUInt16LittleEndian(span) != DosSignature)
            throw new BadImageFormatException($"'{path}' does not start with an MZ header.");

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(span[0x3C..]);
        if (peOffset < 0 || peOffset + 24 > span.Length)
            throw new BadImageFormatException($"'{path}' has an out-of-range PE header offset.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(span[peOffset..]) != PeSignature)
            throw new BadImageFormatException($"'{path}' has no PE signature.");

        int coff = peOffset + 4;
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(span[coff..]);
        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(span[(coff + 2)..]);
        ushort optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(span[(coff + 16)..]);

        int optional = coff + 20;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(span[optional..]);

        // The data directories sit at a different offset in PE32 and PE32+ because the fields before
        // them - ImageBase and the four reserve/commit sizes - widen from 4 bytes to 8.
        int directories = magic switch
        {
            Pe32Magic => optional + 96,
            Pe32PlusMagic => optional + 112,
            _ => throw new BadImageFormatException($"'{path}' has an unrecognised optional header magic 0x{magic:X4}."),
        };

        return new Headers(machine, optional, optionalSize, sectionCount, directories);
    }

    private static Section[] ReadSections(ReadOnlySpan<byte> span, Headers headers)
        => ReadSections(span, headers.Optional + headers.OptionalSize, headers.SectionCount);

    private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawAddress, uint RawSize);

    private static Section[] ReadSections(ReadOnlySpan<byte> span, int offset, ushort count)
    {
        Section[] sections = new Section[count];
        for (int i = 0; i < count; i++)
        {
            int header = offset + (i * 40);
            if (header + 40 > span.Length)
                throw new BadImageFormatException("Section table runs past the end of the file.");

            sections[i] = new Section(
                VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(span[(header + 12)..]),
                VirtualSize: BinaryPrimitives.ReadUInt32LittleEndian(span[(header + 8)..]),
                RawAddress: BinaryPrimitives.ReadUInt32LittleEndian(span[(header + 20)..]),
                RawSize: BinaryPrimitives.ReadUInt32LittleEndian(span[(header + 16)..]));
        }

        return sections;
    }

    /// <summary>Map a virtual address back to a file offset through the section table.</summary>
    private static int ToFileOffset(Section[] sections, uint rva)
    {
        foreach (Section section in sections)
        {
            // VirtualSize can exceed SizeOfRawData for sections with a zero-filled tail; the check
            // uses the larger of the two so an export table at the end of a section still resolves.
            uint size = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + size)
                return (int)(section.RawAddress + (rva - section.VirtualAddress));
        }

        throw new BadImageFormatException($"RVA 0x{rva:X} falls outside every section.");
    }

    private static Dictionary<string, uint> ReadExports(ReadOnlySpan<byte> span, Section[] sections, uint exportRva)
    {
        int directory = ToFileOffset(sections, exportRva);

        uint nameCount = BinaryPrimitives.ReadUInt32LittleEndian(span[(directory + 24)..]);
        uint functionsRva = BinaryPrimitives.ReadUInt32LittleEndian(span[(directory + 28)..]);
        uint namesRva = BinaryPrimitives.ReadUInt32LittleEndian(span[(directory + 32)..]);
        uint ordinalsRva = BinaryPrimitives.ReadUInt32LittleEndian(span[(directory + 36)..]);

        int functions = ToFileOffset(sections, functionsRva);
        int names = ToFileOffset(sections, namesRva);
        int ordinals = ToFileOffset(sections, ordinalsRva);

        Dictionary<string, uint> exports = new(StringComparer.Ordinal);
        for (int i = 0; i < nameCount; i++)
        {
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(span[(names + (i * 4))..]);
            ushort ordinal = BinaryPrimitives.ReadUInt16LittleEndian(span[(ordinals + (i * 2))..]);
            uint functionRva = BinaryPrimitives.ReadUInt32LittleEndian(span[(functions + (ordinal * 4))..]);

            exports[ReadAsciiZ(span, ToFileOffset(sections, nameRva))] = functionRva;
        }

        return exports;
    }

    private static string ReadAsciiZ(ReadOnlySpan<byte> span, int offset)
    {
        ReadOnlySpan<byte> rest = span[offset..];
        int end = rest.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(end < 0 ? rest : rest[..end]);
    }
}
