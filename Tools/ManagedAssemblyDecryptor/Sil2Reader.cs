using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ASWDEBUG.Tools.ManagedAssemblyDecryptor
{
    internal static class Sil2Reader
    {
        private const uint Magic = 0x324C4953;
        private const int MaximumRecordCount = 1024 * 1024;

        public static Sil2Method Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Invalid SIL2 magic: " + path);
                int token = reader.ReadInt32();
                int maxStack = reader.ReadInt32();
                byte initLocalsValue = reader.ReadByte();
                if (initLocalsValue > 1) throw new InvalidDataException("Invalid SIL2 InitLocals value: " + path);

                int localCount = ReadCount(reader, "local variable");
                for (int i = 0; i < localCount; i++)
                {
                    reader.ReadByte();
                    ReadOptionalString(reader);
                }

                int exceptionCount = ReadCount(reader, "exception clause");
                var clauses = new List<Sil2ExceptionClause>(exceptionCount);
                for (int i = 0; i < exceptionCount; i++)
                {
                    int flags = reader.ReadInt32();
                    int tryOffset = reader.ReadInt32();
                    int tryLength = reader.ReadInt32();
                    int handlerOffset = reader.ReadInt32();
                    int handlerLength = reader.ReadInt32();
                    int filterOffset = reader.ReadInt32();
                    ReadOptionalString(reader);
                    clauses.Add(new Sil2ExceptionClause(
                        flags,
                        tryOffset,
                        tryLength,
                        handlerOffset,
                        handlerLength,
                        filterOffset));
                }

                int localSignatureLength = ReadLength(reader, false);
                byte[] localSignature = BinaryUtil.ReadExact(reader, localSignatureLength);
                int ilLength = ReadLength(reader, false);
                byte[] il = BinaryUtil.ReadExact(reader, ilLength);
                if (stream.Position != stream.Length)
                    throw new InvalidDataException("Unexpected trailing data in SIL2 file: " + path);

                return new Sil2Method(
                    path,
                    token,
                    maxStack,
                    initLocalsValue != 0,
                    localCount,
                    localSignature,
                    il,
                    clauses);
            }
        }

        private static int ReadCount(BinaryReader reader, string description)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumRecordCount)
                throw new InvalidDataException("Invalid SIL2 " + description + " count.");
            return count;
        }

        private static void ReadOptionalString(BinaryReader reader)
        {
            int length = ReadLength(reader, true);
            if (length >= 0) BinaryUtil.ReadExact(reader, length);
        }

        private static int ReadLength(BinaryReader reader, bool allowNull)
        {
            int length = reader.ReadInt32();
            if (allowNull && length == -1) return -1;
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid SIL2 field length.");
            return length;
        }
    }

    internal sealed class Sil2Method
    {
        public readonly string Path;
        public readonly int Token;
        public readonly int MaxStack;
        public readonly bool InitLocals;
        public readonly int LocalCount;
        public readonly byte[] LocalSignature;
        public readonly byte[] Il;
        public readonly List<Sil2ExceptionClause> ExceptionClauses;

        public Sil2Method(
            string path,
            int token,
            int maxStack,
            bool initLocals,
            int localCount,
            byte[] localSignature,
            byte[] il,
            List<Sil2ExceptionClause> exceptionClauses)
        {
            Path = path;
            Token = token;
            MaxStack = maxStack;
            InitLocals = initLocals;
            LocalCount = localCount;
            LocalSignature = localSignature;
            Il = il;
            ExceptionClauses = exceptionClauses;
        }
    }

    internal sealed class Sil2ExceptionClause
    {
        public readonly int Flags;
        public readonly int TryOffset;
        public readonly int TryLength;
        public readonly int HandlerOffset;
        public readonly int HandlerLength;
        public readonly int FilterOffset;

        public Sil2ExceptionClause(
            int flags,
            int tryOffset,
            int tryLength,
            int handlerOffset,
            int handlerLength,
            int filterOffset)
        {
            Flags = flags;
            TryOffset = tryOffset;
            TryLength = tryLength;
            HandlerOffset = handlerOffset;
            HandlerLength = handlerLength;
            FilterOffset = filterOffset;
        }
    }
}
