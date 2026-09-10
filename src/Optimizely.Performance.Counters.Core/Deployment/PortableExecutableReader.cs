using System;
using System.IO;

namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// Reads the module version id - the MVID - out of a managed assembly on disk, without loading
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The MVID is the identity of the compiled binary, and it is the only field here that answers
    /// the question this whole feature exists for. Every other version an assembly carries can stay
    /// the same across a rebuild - Optimizely ships many patches under one <c>AssemblyVersion</c>,
    /// and a hotfix commonly reships a file whose version is byte-for-byte what it was - so a
    /// deployment that changed the bits and not the numbers is invisible to anything comparing
    /// versions. The MVID changes whenever the compiler output does.
    /// </para>
    /// <para>
    /// Hand-parsed rather than through <c>System.Reflection.Metadata</c>, which is in the shared
    /// framework on .NET but a package on net472 - and one that drags
    /// <c>System.Collections.Immutable</c> into a CMS 11 site's bin folder behind it. Adding two
    /// assemblies and their binding redirects to a V11 site, for one 16-byte field, is a worse
    /// trade than the hundred lines below. It also keeps one implementation across all six target
    /// frameworks, so the net472 path is exercised by every test run rather than only on net472.
    /// </para>
    /// <para>
    /// Nothing here loads, locks or maps the file: it is opened for read with full sharing, seeked
    /// through, and closed. Anything unexpected returns null rather than throwing. A file this
    /// cannot read is reported as an assembly with no MVID, which is a worse record than the others
    /// and still better than no record.
    /// </para>
    /// <para>
    /// Structure layouts are from ECMA-335 II.25 (PE) and II.24 (metadata).
    /// </para>
    /// </remarks>
    internal static class PortableExecutableReader
    {
        private const ushort DosSignature = 0x5A4D;          // "MZ"
        private const uint PeSignature = 0x0000_4550;        // "PE\0\0"
        private const uint MetadataSignature = 0x424A_5342;  // "BSJB"

        private const ushort Pe32Magic = 0x010B;
        private const ushort Pe32PlusMagic = 0x020B;

        // Offset of the data directory array within the optional header. The two PE flavours differ
        // only in the width of a handful of fields before it.
        private const int Pe32DataDirectories = 96;
        private const int Pe32PlusDataDirectories = 112;

        // The CLI header is data directory 15 of 16, index 14. Its absence is what makes a file
        // native rather than managed.
        private const int CliHeaderDirectoryIndex = 14;

        private const int SectionHeaderSize = 40;

        /// <summary>
        /// What could be established about one file on disk.
        /// </summary>
        internal readonly struct ModuleIdentity
        {
            internal ModuleIdentity(bool isManaged, Guid mvid)
            {
                IsManaged = isManaged;
                Mvid = mvid;
            }

            /// <summary>True when the file carries a CLI header.</summary>
            internal bool IsManaged { get; }

            /// <summary>
            /// The module version id, or <see cref="Guid.Empty"/> when the file is native or its
            /// metadata could not be walked.
            /// </summary>
            internal Guid Mvid { get; }
        }

        /// <summary>
        /// Reads the module identity of a file.
        /// </summary>
        /// <param name="path">Full path of the file to read.</param>
        /// <returns>
        /// The identity, or null if the file could not be opened or is not a PE image at all.
        /// </returns>
        internal static ModuleIdentity? TryRead(string path)
        {
            try
            {
                // FileShare.ReadWrite | Delete, because this runs against a live site's bin folder:
                // a deployment may be rewriting the very files being read, and a share mode that
                // blocked it would make a diagnostic the cause of a failed deployment.
                using (var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096))
                using (var reader = new BinaryReader(stream))
                {
                    return Read(stream, reader);
                }
            }
            catch (Exception)
            {
                // Unreadable, mid-write, denied, or not a file any more. All ordinary in a bin
                // folder during a deployment, and none of them worth an entry in the site's log.
                return null;
            }
        }

        private static ModuleIdentity? Read(FileStream stream, BinaryReader reader)
        {
            if (stream.Length < 0x40 || reader.ReadUInt16() != DosSignature)
            {
                return null;
            }

            stream.Position = 0x3C;
            var peHeader = reader.ReadUInt32();

            if (!Seek(stream, peHeader) || reader.ReadUInt32() != PeSignature)
            {
                return null;
            }

            // COFF header: Machine, NumberOfSections, TimeDateStamp, PointerToSymbolTable,
            // NumberOfSymbols, SizeOfOptionalHeader, Characteristics.
            stream.Position += 2;
            var sectionCount = reader.ReadUInt16();
            stream.Position += 12;
            var optionalHeaderSize = reader.ReadUInt16();
            stream.Position += 2;

            var optionalHeader = stream.Position;
            var magic = reader.ReadUInt16();

            var directories = magic == Pe32PlusMagic
                ? Pe32PlusDataDirectories
                : magic == Pe32Magic
                    ? Pe32DataDirectories
                    : -1;

            if (directories < 0)
            {
                return null;
            }

            if (!Seek(stream, optionalHeader + directories + (CliHeaderDirectoryIndex * 8)))
            {
                return null;
            }

            var cliRva = reader.ReadUInt32();

            if (cliRva == 0)
            {
                // A native DLL. Expected rather than exceptional - a bin folder holds SQLite,
                // ImageMagick and whatever else a site's packages carry - and worth recording as
                // itself, because a native dependency changing is a deployment change too.
                return new ModuleIdentity(isManaged: false, Guid.Empty);
            }

            var sections = ReadSections(stream, reader, optionalHeader + optionalHeaderSize, sectionCount);

            if (sections == null)
            {
                return null;
            }

            var mvid = ReadMvid(stream, reader, sections, cliRva);

            return new ModuleIdentity(isManaged: true, mvid);
        }

        private static Guid ReadMvid(
            FileStream stream, BinaryReader reader, Section[] sections, uint cliRva)
        {
            if (!Seek(stream, Offset(sections, cliRva)))
            {
                return Guid.Empty;
            }

            // CLI header: cb, MajorRuntimeVersion, MinorRuntimeVersion, then the MetaData directory.
            stream.Position += 8;
            var metadataRva = reader.ReadUInt32();

            var metadata = Offset(sections, metadataRva);

            if (!Seek(stream, metadata) || reader.ReadUInt32() != MetadataSignature)
            {
                return Guid.Empty;
            }

            // Metadata root: signature, MajorVersion, MinorVersion, Reserved, then a length-prefixed
            // version string padded to a four-byte boundary.
            stream.Position += 8;
            var versionLength = reader.ReadUInt32();

            if (versionLength > 0xFF)
            {
                return Guid.Empty;
            }

            stream.Position += versionLength + 2; // version string, then Flags
            var streamCount = reader.ReadUInt16();

            long tables = -1;
            long guids = -1;

            for (var i = 0; i < streamCount; i++)
            {
                var streamOffset = reader.ReadUInt32();
                reader.ReadUInt32(); // size, not needed
                var name = ReadStreamName(reader);

                if (name == null)
                {
                    return Guid.Empty;
                }

                // "#~" is the compressed table stream. "#-" is the uncompressed one, which only
                // appears in edit-and-continue images; its Module table is laid out identically, so
                // it is read the same way.
                if (name == "#~" || name == "#-")
                {
                    tables = metadata + streamOffset;
                }
                else if (name == "#GUID")
                {
                    guids = metadata + streamOffset;
                }
            }

            if (tables < 0 || guids < 0 || !Seek(stream, tables))
            {
                return Guid.Empty;
            }

            // Table stream header: Reserved, MajorVersion, MinorVersion, HeapSizes, Reserved,
            // Valid, Sorted, then one row count per set bit in Valid.
            stream.Position += 6;
            var heapSizes = reader.ReadByte();
            stream.Position += 1;
            var valid = reader.ReadUInt64();
            stream.Position += 8;

            stream.Position += CountBits(valid) * 4;

            if ((valid & 1UL) == 0)
            {
                // No Module table. Not a thing a real assembly does, but the arithmetic below would
                // otherwise read whatever happens to be there.
                return Guid.Empty;
            }

            // Module row: Generation, Name (#Strings index), Mvid (#GUID index), EncId, EncBaseId.
            // Module is table 0, so its first row starts here and no preceding table has to be
            // measured - which is the whole reason this parser is short.
            var stringIndex = (heapSizes & 0x01) != 0 ? 4 : 2;
            var guidIndex = (heapSizes & 0x02) != 0 ? 4 : 2;

            stream.Position += 2 + stringIndex;
            var mvidIndex = guidIndex == 2 ? reader.ReadUInt16() : reader.ReadUInt32();

            if (mvidIndex == 0)
            {
                return Guid.Empty;
            }

            // The #GUID heap is a bare array of 16-byte values indexed from one.
            if (!Seek(stream, guids + ((mvidIndex - 1L) * 16)))
            {
                return Guid.Empty;
            }

            var bytes = reader.ReadBytes(16);

            return bytes.Length == 16 ? new Guid(bytes) : Guid.Empty;
        }

        private static string? ReadStreamName(BinaryReader reader)
        {
            // Null-terminated ASCII, padded with nulls to the next four-byte boundary. Stream names
            // are short and known; the cap stops a malformed header spinning.
            var name = string.Empty;

            for (var i = 0; i < 32; i += 4)
            {
                var chunk = reader.ReadBytes(4);

                if (chunk.Length < 4)
                {
                    return null;
                }

                foreach (var b in chunk)
                {
                    if (b != 0)
                    {
                        name += (char)b;
                    }
                }

                if (chunk[3] == 0)
                {
                    return name;
                }
            }

            return null;
        }

        private static Section[]? ReadSections(
            FileStream stream, BinaryReader reader, long start, int count)
        {
            if (count <= 0 || count > 96 || !Seek(stream, start))
            {
                return null;
            }

            var sections = new Section[count];

            for (var i = 0; i < count; i++)
            {
                stream.Position += 8; // Name
                var virtualSize = reader.ReadUInt32();
                var virtualAddress = reader.ReadUInt32();
                var rawSize = reader.ReadUInt32();
                var rawPointer = reader.ReadUInt32();
                stream.Position += SectionHeaderSize - 24;

                sections[i] = new Section(virtualAddress, Math.Max(virtualSize, rawSize), rawPointer);
            }

            return sections;
        }

        private static long Offset(Section[] sections, uint rva)
        {
            foreach (var section in sections)
            {
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.Size)
                {
                    return section.RawPointer + (rva - section.VirtualAddress);
                }
            }

            return -1;
        }

        private static bool Seek(FileStream stream, long position)
        {
            if (position < 0 || position >= stream.Length)
            {
                return false;
            }

            stream.Position = position;
            return true;
        }

        private static int CountBits(ulong value)
        {
            // Not BitOperations.PopCount: that is .NET Core 3.0 and later, and this file compiles
            // for net472 as well.
            var count = 0;

            while (value != 0)
            {
                value &= value - 1;
                count++;
            }

            return count;
        }

        private readonly struct Section
        {
            internal Section(uint virtualAddress, uint size, uint rawPointer)
            {
                VirtualAddress = virtualAddress;
                Size = size;
                RawPointer = rawPointer;
            }

            internal uint VirtualAddress { get; }

            internal uint Size { get; }

            internal uint RawPointer { get; }
        }
    }
}
