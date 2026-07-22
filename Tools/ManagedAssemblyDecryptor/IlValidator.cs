using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

namespace ASWDEBUG.Tools.ManagedAssemblyDecryptor
{
    internal static class IlValidator
    {
        private static readonly Dictionary<ushort, OperandType> OperandTypes = BuildOperandTypes();

        public static void Validate(byte[] code, ManagedPeImage image, string methodName)
        {
            if (code == null) throw new ArgumentNullException("code");
            if (image == null) throw new ArgumentNullException("image");

            var instructionOffsets = new HashSet<int>();
            var branchTargets = new List<int>();
            int position = 0;
            while (position < code.Length)
            {
                int instructionOffset = position;
                instructionOffsets.Add(instructionOffset);
                ushort opcode;
                byte first = code[position++];
                if (first == 0xFE)
                {
                    Require(code, position, 1, methodName);
                    opcode = (ushort)(0xFE00 | code[position++]);
                }
                else
                {
                    opcode = first;
                }

                OperandType operandType;
                if (!OperandTypes.TryGetValue(opcode, out operandType))
                    throw InvalidMethod(methodName, instructionOffset, "unknown opcode 0x" + opcode.ToString("X4"));

                switch (operandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        Require(code, position, 1, methodName);
                        position += 1;
                        break;
                    case OperandType.InlineVar:
                        Require(code, position, 2, methodName);
                        position += 2;
                        break;
                    case OperandType.InlineI:
                    case OperandType.ShortInlineR:
                        Require(code, position, 4, methodName);
                        position += 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        Require(code, position, 8, methodName);
                        position += 8;
                        break;
                    case OperandType.ShortInlineBrTarget:
                    {
                        Require(code, position, 1, methodName);
                        int relative = unchecked((sbyte)code[position]);
                        position += 1;
                        branchTargets.Add(CheckedTarget(position, relative, methodName, instructionOffset));
                        break;
                    }
                    case OperandType.InlineBrTarget:
                    {
                        Require(code, position, 4, methodName);
                        int relative = ReadInt32(code, position);
                        position += 4;
                        branchTargets.Add(CheckedTarget(position, relative, methodName, instructionOffset));
                        break;
                    }
                    case OperandType.InlineSwitch:
                    {
                        Require(code, position, 4, methodName);
                        int count = ReadInt32(code, position);
                        position += 4;
                        if (count < 0 || count > (code.Length - position) / 4)
                            throw InvalidMethod(methodName, instructionOffset, "invalid switch target count");
                        int targetBase = position + count * 4;
                        for (int i = 0; i < count; i++)
                        {
                            int relative = ReadInt32(code, position + i * 4);
                            branchTargets.Add(CheckedTarget(targetBase, relative, methodName, instructionOffset));
                        }
                        position = targetBase;
                        break;
                    }
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineSig:
                    case OperandType.InlineString:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                    {
                        Require(code, position, 4, methodName);
                        uint token = ReadUInt32(code, position);
                        if (!image.IsValidMetadataToken(token))
                            throw InvalidMethod(
                                methodName,
                                instructionOffset,
                                "metadata token 0x" + token.ToString("X8") + " is out of range");
                        position += 4;
                        break;
                    }
                    default:
                        throw InvalidMethod(methodName, instructionOffset, "unsupported operand type " + operandType);
                }
            }

            for (int i = 0; i < branchTargets.Count; i++)
            {
                int target = branchTargets[i];
                if (target < 0 || target >= code.Length || !instructionOffsets.Contains(target))
                    throw new InvalidDataException(
                        "Invalid IL in " + methodName + ": branch target IL_" + target.ToString("X4") +
                        " is not an instruction boundary.");
            }
        }

        private static Dictionary<ushort, OperandType> BuildOperandTypes()
        {
            var result = new Dictionary<ushort, OperandType>();
            FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType != typeof(OpCode)) continue;
                OpCode opcode = (OpCode)fields[i].GetValue(null);
                ushort key = opcode.Size == 1
                    ? (ushort)(byte)opcode.Value
                    : unchecked((ushort)opcode.Value);
                result[key] = opcode.OperandType;
            }
            return result;
        }

        private static int CheckedTarget(int baseOffset, int relative, string methodName, int instructionOffset)
        {
            long target = (long)baseOffset + relative;
            if (target < int.MinValue || target > int.MaxValue)
                throw InvalidMethod(methodName, instructionOffset, "branch target overflow");
            return (int)target;
        }

        private static InvalidDataException InvalidMethod(string methodName, int offset, string detail)
        {
            return new InvalidDataException(
                "Invalid IL in " + methodName + " at IL_" + offset.ToString("X4") + ": " + detail + ".");
        }

        private static void Require(byte[] code, int offset, int count, string methodName)
        {
            if (offset < 0 || count < 0 || offset > code.Length || count > code.Length - offset)
                throw new InvalidDataException("Invalid IL in " + methodName + ": truncated operand.");
        }

        private static int ReadInt32(byte[] value, int offset)
        {
            return unchecked((int)ReadUInt32(value, offset));
        }

        private static uint ReadUInt32(byte[] value, int offset)
        {
            return (uint)(value[offset] |
                          (value[offset + 1] << 8) |
                          (value[offset + 2] << 16) |
                          (value[offset + 3] << 24));
        }
    }
}
