using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ASWDEBUG.Tools.ManagedAssemblyDecryptor
{
    internal sealed class ManagedPeImage
    {
        private readonly byte[] _data;
        private readonly List<PeSection> _sections = new List<PeSection>();
        private readonly int[] _tableRowCounts = new int[64];
        private int _sizeOfHeaders;
        private int _stringsOffset;
        private int _stringsSize;
        private int _userStringsSize;

        public readonly List<MethodDefinitionRecord> Methods = new List<MethodDefinitionRecord>();

        public ManagedPeImage(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            _data = data;
            ParsePeAndMetadata();
        }

        public byte[] Data
        {
            get { return _data; }
        }

        public MethodDefinitionRecord GetMethodByToken(int token)
        {
            if ((token & unchecked((int)0xFF000000)) != 0x06000000)
                throw new InvalidDataException("SIL2 token is not a MethodDef token: 0x" + token.ToString("X8"));
            int rid = token & 0x00FFFFFF;
            if (rid <= 0 || rid > Methods.Count)
                throw new InvalidDataException("MethodDef token is outside the metadata table: 0x" + token.ToString("X8"));
            return Methods[rid - 1];
        }

        public MethodBodyInfo ReadMethodBody(MethodDefinitionRecord method)
        {
            if (method == null) throw new ArgumentNullException("method");
            if (method.Rva == 0) throw new InvalidDataException("Method has no RVA: " + method.DisplayName);

            int bodyOffset = RvaToOffset(method.Rva);
            EnsureRange(bodyOffset, 1, "method header");
            byte first = _data[bodyOffset];
            if ((first & 3) == 2)
            {
                int codeSize = first >> 2;
                EnsureRange(bodyOffset + 1, codeSize, "tiny method body");
                return new MethodBodyInfo(
                    method,
                    bodyOffset,
                    bodyOffset + 1,
                    1,
                    codeSize,
                    8,
                    2,
                    0);
            }

            if ((first & 3) != 3)
                throw new InvalidDataException("Unsupported method header at RVA 0x" + method.Rva.ToString("X8") + ".");

            EnsureRange(bodyOffset, 12, "fat method header");
            ushort flagsAndSize = ReadUInt16(bodyOffset);
            int headerSize = ((flagsAndSize >> 12) & 0xF) * 4;
            if (headerSize < 12) throw new InvalidDataException("Invalid fat method header size.");
            EnsureRange(bodyOffset, headerSize, "fat method header");

            int codeSizeValue = CheckedInt(ReadUInt32(bodyOffset + 4), "method code size");
            int codeOffset = CheckedAdd(bodyOffset, headerSize, "method code offset");
            EnsureRange(codeOffset, codeSizeValue, "fat method body");
            return new MethodBodyInfo(
                method,
                bodyOffset,
                codeOffset,
                headerSize,
                codeSizeValue,
                ReadUInt16(bodyOffset + 2),
                flagsAndSize & 0x0FFF,
                ReadUInt32(bodyOffset + 8));
        }

        public byte[] ReadMethodCode(MethodBodyInfo body)
        {
            var code = new byte[body.CodeSize];
            Buffer.BlockCopy(_data, body.CodeOffset, code, 0, code.Length);
            return code;
        }

        public List<ExceptionClauseRecord> ReadExceptionClauses(MethodBodyInfo body)
        {
            var clauses = new List<ExceptionClauseRecord>();
            if ((body.Flags & 8) == 0) return clauses;

            int sectionOffset = BinaryUtil.Align4(CheckedAdd(body.CodeOffset, body.CodeSize, "method section offset"));
            bool more;
            int sectionCount = 0;
            do
            {
                sectionCount++;
                if (sectionCount > 64) throw new InvalidDataException("Too many chained method data sections.");
                EnsureRange(sectionOffset, 4, "method data section header");
                byte kind = _data[sectionOffset];
                more = (kind & 0x80) != 0;
                bool fat = (kind & 0x40) != 0;
                if ((kind & 0x3F) != 1)
                    throw new InvalidDataException("Unsupported method data section kind 0x" + kind.ToString("X2") + ".");

                int dataSize = fat
                    ? _data[sectionOffset + 1] | (_data[sectionOffset + 2] << 8) | (_data[sectionOffset + 3] << 16)
                    : _data[sectionOffset + 1];
                int clauseSize = fat ? 24 : 12;
                if (dataSize < 4 || ((dataSize - 4) % clauseSize) != 0)
                    throw new InvalidDataException("Invalid exception table size.");
                EnsureRange(sectionOffset, dataSize, "exception table");

                int count = (dataSize - 4) / clauseSize;
                int clauseOffset = sectionOffset + 4;
                for (int i = 0; i < count; i++)
                {
                    ExceptionClauseRecord clause;
                    if (fat)
                    {
                        clause = new ExceptionClauseRecord(
                            CheckedInt(ReadUInt32(clauseOffset), "exception flags"),
                            CheckedInt(ReadUInt32(clauseOffset + 4), "try offset"),
                            CheckedInt(ReadUInt32(clauseOffset + 8), "try length"),
                            CheckedInt(ReadUInt32(clauseOffset + 12), "handler offset"),
                            CheckedInt(ReadUInt32(clauseOffset + 16), "handler length"),
                            ReadUInt32(clauseOffset + 20));
                    }
                    else
                    {
                        clause = new ExceptionClauseRecord(
                            ReadUInt16(clauseOffset),
                            ReadUInt16(clauseOffset + 2),
                            _data[clauseOffset + 4],
                            ReadUInt16(clauseOffset + 5),
                            _data[clauseOffset + 7],
                            ReadUInt32(clauseOffset + 8));
                    }
                    ValidateExceptionClause(clause, body.CodeSize);
                    clauses.Add(clause);
                    clauseOffset += clauseSize;
                }

                sectionOffset = BinaryUtil.Align4(CheckedAdd(sectionOffset, dataSize, "next method section"));
            }
            while (more);
            return clauses;
        }

        public int ValidateMethodBody(MethodDefinitionRecord method)
        {
            MethodBodyInfo body = ReadMethodBody(method);
            byte[] code = ReadMethodCode(body);
            IlValidator.Validate(code, this, method.DisplayName);
            ReadExceptionClauses(body);
            return 1;
        }

        public bool IsValidMetadataToken(uint token)
        {
            int table = (int)(token >> 24);
            int rid = (int)(token & 0x00FFFFFF);
            if (table == 0x70) return rid >= 0 && rid < _userStringsSize;
            return table >= 0 && table < _tableRowCounts.Length && rid > 0 && rid <= _tableRowCounts[table];
        }

        private void ParsePeAndMetadata()
        {
            EnsureRange(0, 64, "DOS header");
            if (_data[0] != 0x4D || _data[1] != 0x5A)
                throw new InvalidDataException("File does not start with MZ.");

            int peOffset = ReadInt32(0x3C);
            EnsureRange(peOffset, 24, "PE header");
            if (ReadUInt32(peOffset) != 0x00004550)
                throw new InvalidDataException("Invalid PE signature.");

            int sectionCount = ReadUInt16(peOffset + 6);
            int optionalHeaderSize = ReadUInt16(peOffset + 20);
            int optionalHeaderOffset = peOffset + 24;
            EnsureRange(optionalHeaderOffset, optionalHeaderSize, "PE optional header");
            ushort optionalMagic = ReadUInt16(optionalHeaderOffset);
            int directoryOffset;
            int directoryCountOffset;
            if (optionalMagic == 0x10B)
            {
                directoryOffset = optionalHeaderOffset + 96;
                directoryCountOffset = optionalHeaderOffset + 92;
            }
            else if (optionalMagic == 0x20B)
            {
                directoryOffset = optionalHeaderOffset + 112;
                directoryCountOffset = optionalHeaderOffset + 108;
            }
            else
            {
                throw new InvalidDataException("Unsupported PE optional header magic 0x" + optionalMagic.ToString("X4") + ".");
            }

            _sizeOfHeaders = CheckedInt(ReadUInt32(optionalHeaderOffset + 60), "SizeOfHeaders");
            int directoryCount = CheckedInt(ReadUInt32(directoryCountOffset), "data directory count");
            if (directoryCount <= 14) throw new InvalidDataException("PE file does not contain a CLR directory.");
            const int requiredDirectoryBytes = 15 * 8;
            EnsureRange(directoryOffset, requiredDirectoryBytes, "PE data directories");
            if (directoryOffset + requiredDirectoryBytes > optionalHeaderOffset + optionalHeaderSize)
                throw new InvalidDataException("CLR directory is outside the PE optional header.");

            int sectionOffset = optionalHeaderOffset + optionalHeaderSize;
            EnsureRange(sectionOffset, sectionCount * 40, "PE section table");
            for (int i = 0; i < sectionCount; i++)
            {
                int current = sectionOffset + i * 40;
                _sections.Add(new PeSection(
                    ReadUInt32(current + 12),
                    ReadUInt32(current + 8),
                    ReadUInt32(current + 20),
                    ReadUInt32(current + 16)));
            }

            uint clrRva = ReadUInt32(directoryOffset + 14 * 8);
            uint clrSize = ReadUInt32(directoryOffset + 14 * 8 + 4);
            if (clrRva == 0 || clrSize < 16) throw new InvalidDataException("PE file has no valid CLR header.");
            int clrOffset = RvaToOffset(clrRva);
            EnsureRange(clrOffset, 16, "CLR header");
            uint metadataRva = ReadUInt32(clrOffset + 8);
            int metadataSize = CheckedInt(ReadUInt32(clrOffset + 12), "metadata size");
            int metadataOffset = RvaToOffset(metadataRva);
            EnsureRange(metadataOffset, metadataSize, "CLR metadata");
            ParseMetadata(metadataOffset, metadataSize);
        }

        private void ParseMetadata(int metadataOffset, int metadataSize)
        {
            EnsureRange(metadataOffset, 20, "metadata root");
            int metadataEnd = CheckedAdd(metadataOffset, metadataSize, "metadata end");
            if (ReadUInt32(metadataOffset) != 0x424A5342)
                throw new InvalidDataException("Invalid CLR metadata signature.");

            int versionLength = CheckedInt(ReadUInt32(metadataOffset + 12), "metadata version length");
            int streamHeaderOffset = BinaryUtil.Align4(CheckedAdd(metadataOffset + 16, versionLength, "metadata stream header"));
            EnsureRange(streamHeaderOffset, 4, "metadata stream count");
            int streamCount = ReadUInt16(streamHeaderOffset + 2);
            int current = streamHeaderOffset + 4;
            int tablesOffset = -1;
            int tablesSize = 0;

            for (int i = 0; i < streamCount; i++)
            {
                EnsureRange(current, 8, "metadata stream header");
                if (current + 8 > metadataEnd)
                    throw new InvalidDataException("Metadata stream header exceeds the metadata root.");
                int relativeOffset = CheckedInt(ReadUInt32(current), "metadata stream offset");
                int size = CheckedInt(ReadUInt32(current + 4), "metadata stream size");
                int nameOffset = current + 8;
                int nameEnd = nameOffset;
                while (nameEnd < metadataEnd && _data[nameEnd] != 0) nameEnd++;
                if (nameEnd >= metadataEnd) throw new InvalidDataException("Unterminated metadata stream name.");
                string name = Encoding.ASCII.GetString(_data, nameOffset, nameEnd - nameOffset);
                int headerLength = BinaryUtil.Align4(8 + (nameEnd - nameOffset) + 1);
                current = CheckedAdd(current, headerLength, "next metadata stream header");
                if (current > metadataEnd)
                    throw new InvalidDataException("Metadata stream headers exceed the metadata root.");

                int absoluteOffset = CheckedAdd(metadataOffset, relativeOffset, "metadata stream offset");
                EnsureRange(absoluteOffset, size, "metadata stream");
                if (absoluteOffset + (long)size > metadataEnd)
                    throw new InvalidDataException("Metadata stream exceeds the metadata root.");
                if (name == "#~" || name == "#-")
                {
                    tablesOffset = absoluteOffset;
                    tablesSize = size;
                }
                else if (name == "#Strings")
                {
                    _stringsOffset = absoluteOffset;
                    _stringsSize = size;
                }
                else if (name == "#US")
                {
                    _userStringsSize = size;
                }
            }

            if (tablesOffset < 0) throw new InvalidDataException("CLR metadata has no tables stream.");
            if (_stringsOffset == 0 || _stringsSize == 0) throw new InvalidDataException("CLR metadata has no strings heap.");
            ParseTables(tablesOffset, tablesSize);
        }

        private void ParseTables(int tablesOffset, int tablesSize)
        {
            EnsureRange(tablesOffset, 24, "metadata tables header");
            byte heapSizes = _data[tablesOffset + 6];
            int stringIndexSize = (heapSizes & 1) != 0 ? 4 : 2;
            int guidIndexSize = (heapSizes & 2) != 0 ? 4 : 2;
            int blobIndexSize = (heapSizes & 4) != 0 ? 4 : 2;
            ulong validMask = ReadUInt64(tablesOffset + 8);
            int rowCountOffset = tablesOffset + 24;

            for (int table = 0; table < 64; table++)
            {
                if ((validMask & (1UL << table)) == 0) continue;
                EnsureRange(rowCountOffset, 4, "metadata row count");
                _tableRowCounts[table] = CheckedInt(ReadUInt32(rowCountOffset), "metadata row count");
                rowCountOffset += 4;
            }

            int tableDataOffset = rowCountOffset;
            for (int table = 0; table < 6; table++)
            {
                int rowSize = GetPreMethodTableRowSize(table, stringIndexSize, guidIndexSize, blobIndexSize);
                long byteCount = (long)_tableRowCounts[table] * rowSize;
                if (byteCount > int.MaxValue) throw new InvalidDataException("Metadata table is too large.");
                tableDataOffset = CheckedAdd(tableDataOffset, (int)byteCount, "MethodDef table offset");
            }

            int methodRowSize = 8 + stringIndexSize + blobIndexSize + GetTableIndexSize(8);
            long methodBytes = (long)_tableRowCounts[6] * methodRowSize;
            if (methodBytes > int.MaxValue) throw new InvalidDataException("MethodDef table is too large.");
            EnsureRange(tableDataOffset, (int)methodBytes, "MethodDef table");
            if (tableDataOffset + methodBytes > tablesOffset + (long)tablesSize)
                throw new InvalidDataException("MethodDef table exceeds the metadata tables stream.");

            for (int rid = 1; rid <= _tableRowCounts[6]; rid++)
            {
                int rowOffset = tableDataOffset + (rid - 1) * methodRowSize;
                uint rva = ReadUInt32(rowOffset);
                ushort implFlags = ReadUInt16(rowOffset + 4);
                uint nameIndex = ReadHeapIndex(rowOffset + 8, stringIndexSize);
                string name = ReadString(nameIndex);
                Methods.Add(new MethodDefinitionRecord(rid, rva, implFlags, rowOffset + 4, name));
            }
        }

        private int GetPreMethodTableRowSize(int table, int stringSize, int guidSize, int blobSize)
        {
            switch (table)
            {
                case 0:
                    return 2 + stringSize + guidSize * 3;
                case 1:
                    return GetCodedIndexSize(2, new[] { 0, 26, 35, 1 }) + stringSize * 2;
                case 2:
                    return 4 + stringSize * 2 +
                           GetCodedIndexSize(2, new[] { 2, 1, 27 }) +
                           GetTableIndexSize(4) + GetTableIndexSize(6);
                case 3:
                    return GetTableIndexSize(4);
                case 4:
                    return 2 + stringSize + blobSize;
                case 5:
                    return GetTableIndexSize(6);
                default:
                    throw new InvalidDataException("Unsupported metadata table before MethodDef: " + table);
            }
        }

        private int GetTableIndexSize(int table)
        {
            return _tableRowCounts[table] < 65536 ? 2 : 4;
        }

        private int GetCodedIndexSize(int tagBits, int[] tables)
        {
            int maxRows = 0;
            for (int i = 0; i < tables.Length; i++)
                if (_tableRowCounts[tables[i]] > maxRows) maxRows = _tableRowCounts[tables[i]];
            return maxRows < (1 << (16 - tagBits)) ? 2 : 4;
        }

        private uint ReadHeapIndex(int offset, int size)
        {
            return size == 2 ? ReadUInt16(offset) : ReadUInt32(offset);
        }

        private string ReadString(uint index)
        {
            if (index == 0) return string.Empty;
            if (index >= _stringsSize) throw new InvalidDataException("String heap index is out of range.");
            int start = CheckedAdd(_stringsOffset, (int)index, "string heap offset");
            int end = start;
            int limit = _stringsOffset + _stringsSize;
            while (end < limit && _data[end] != 0) end++;
            if (end >= limit) throw new InvalidDataException("Unterminated string heap value.");
            return Encoding.UTF8.GetString(_data, start, end - start);
        }

        private int RvaToOffset(uint rva)
        {
            if (rva < _sizeOfHeaders)
            {
                int headerOffset = CheckedInt(rva, "header RVA");
                EnsureRange(headerOffset, 1, "header RVA");
                return headerOffset;
            }

            for (int i = 0; i < _sections.Count; i++)
            {
                PeSection section = _sections[i];
                ulong span = Math.Max(section.VirtualSize, section.RawSize);
                if (rva < section.VirtualAddress || (ulong)(rva - section.VirtualAddress) >= span) continue;
                uint delta = rva - section.VirtualAddress;
                if (delta >= section.RawSize) throw new InvalidDataException("RVA points outside section raw data.");
                ulong result = (ulong)section.RawPointer + delta;
                if (result > int.MaxValue) throw new InvalidDataException("PE file offset is too large.");
                EnsureRange((int)result, 1, "RVA data");
                return (int)result;
            }
            throw new InvalidDataException("RVA 0x" + rva.ToString("X8") + " does not map to a PE section.");
        }

        private void ValidateExceptionClause(ExceptionClauseRecord clause, int codeSize)
        {
            if (clause.Flags != 0 && clause.Flags != 1 && clause.Flags != 2 && clause.Flags != 4)
                throw new InvalidDataException("Unsupported exception clause flags: " + clause.Flags);
            ValidateCodeRange(clause.TryOffset, clause.TryLength, codeSize, "try");
            ValidateCodeRange(clause.HandlerOffset, clause.HandlerLength, codeSize, "handler");
            if ((clause.Flags & 1) != 0 && clause.ClassTokenOrFilterOffset >= codeSize)
                throw new InvalidDataException("Exception filter offset is outside the method body.");
            if (clause.Flags == 0 && !IsValidMetadataToken(clause.ClassTokenOrFilterOffset))
                throw new InvalidDataException("Exception catch type token is outside the metadata tables.");
        }

        private static void ValidateCodeRange(int offset, int length, int codeSize, string name)
        {
            if (offset < 0 || length < 0 || offset > codeSize || length > codeSize - offset)
                throw new InvalidDataException("Invalid " + name + " exception range.");
        }

        private void EnsureRange(int offset, int count, string description)
        {
            if (offset < 0 || count < 0 || offset > _data.Length || count > _data.Length - offset)
                throw new InvalidDataException(description + " is outside the file.");
        }

        private ushort ReadUInt16(int offset)
        {
            EnsureRange(offset, 2, "UInt16");
            return (ushort)(_data[offset] | (_data[offset + 1] << 8));
        }

        private int ReadInt32(int offset)
        {
            return unchecked((int)ReadUInt32(offset));
        }

        private uint ReadUInt32(int offset)
        {
            EnsureRange(offset, 4, "UInt32");
            return (uint)(_data[offset] |
                          (_data[offset + 1] << 8) |
                          (_data[offset + 2] << 16) |
                          (_data[offset + 3] << 24));
        }

        private ulong ReadUInt64(int offset)
        {
            uint low = ReadUInt32(offset);
            uint high = ReadUInt32(offset + 4);
            return low | ((ulong)high << 32);
        }

        private static int CheckedInt(uint value, string description)
        {
            if (value > int.MaxValue) throw new InvalidDataException(description + " exceeds Int32.");
            return (int)value;
        }

        private static int CheckedAdd(int left, int right, string description)
        {
            long result = (long)left + right;
            if (result < 0 || result > int.MaxValue) throw new InvalidDataException(description + " overflow.");
            return (int)result;
        }

        private sealed class PeSection
        {
            public readonly uint VirtualAddress;
            public readonly uint VirtualSize;
            public readonly uint RawPointer;
            public readonly uint RawSize;

            public PeSection(uint virtualAddress, uint virtualSize, uint rawPointer, uint rawSize)
            {
                VirtualAddress = virtualAddress;
                VirtualSize = virtualSize;
                RawPointer = rawPointer;
                RawSize = rawSize;
            }
        }
    }

    internal sealed class MethodDefinitionRecord
    {
        public readonly int Rid;
        public readonly uint Rva;
        public readonly ushort ImplFlags;
        public readonly int ImplFlagsOffset;
        public readonly string Name;

        public MethodDefinitionRecord(int rid, uint rva, ushort implFlags, int implFlagsOffset, string name)
        {
            Rid = rid;
            Rva = rva;
            ImplFlags = implFlags;
            ImplFlagsOffset = implFlagsOffset;
            Name = name;
        }

        public int Token
        {
            get { return 0x06000000 | Rid; }
        }

        public string DisplayName
        {
            get { return "0x" + Token.ToString("X8") + " " + Name; }
        }
    }

    internal sealed class MethodBodyInfo
    {
        public readonly MethodDefinitionRecord Method;
        public readonly int BodyOffset;
        public readonly int CodeOffset;
        public readonly int HeaderSize;
        public readonly int CodeSize;
        public readonly int MaxStack;
        public readonly int Flags;
        public readonly uint LocalSignatureToken;

        public MethodBodyInfo(
            MethodDefinitionRecord method,
            int bodyOffset,
            int codeOffset,
            int headerSize,
            int codeSize,
            int maxStack,
            int flags,
            uint localSignatureToken)
        {
            Method = method;
            BodyOffset = bodyOffset;
            CodeOffset = codeOffset;
            HeaderSize = headerSize;
            CodeSize = codeSize;
            MaxStack = maxStack;
            Flags = flags;
            LocalSignatureToken = localSignatureToken;
        }

        public bool InitLocals
        {
            get { return (Flags & 0x10) != 0; }
        }
    }

    internal sealed class ExceptionClauseRecord
    {
        public readonly int Flags;
        public readonly int TryOffset;
        public readonly int TryLength;
        public readonly int HandlerOffset;
        public readonly int HandlerLength;
        public readonly uint ClassTokenOrFilterOffset;

        public ExceptionClauseRecord(
            int flags,
            int tryOffset,
            int tryLength,
            int handlerOffset,
            int handlerLength,
            uint classTokenOrFilterOffset)
        {
            Flags = flags;
            TryOffset = tryOffset;
            TryLength = tryLength;
            HandlerOffset = handlerOffset;
            HandlerLength = handlerLength;
            ClassTokenOrFilterOffset = classTokenOrFilterOffset;
        }
    }
}
