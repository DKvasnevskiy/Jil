﻿using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace Jil.Serialize
{
    class Utils
    {
        private static readonly Dictionary<int, OpCode> OneByteOps;
        private static readonly Dictionary<int, OpCode> TwoByteOps;

        static Utils()
        {
            var oneByte = new List<OpCode>();
            var twoByte = new List<OpCode>();

            foreach(var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var op = (OpCode)field.GetValue(null);

                if (op.Size == 1)
                {
                    oneByte.Add(op);
                    continue;
                }

                if (op.Size == 2)
                {
                    twoByte.Add(op);
                    continue;
                }

                throw new Exception("Unexpected op size for " + op);
            }

            OneByteOps = oneByte.ToDictionary(d => (int)d.Value, d => d);
            TwoByteOps = twoByte.ToDictionary(d => (int)(d.Value & 0xFF), d => d);
        }

        private static Dictionary<Type, Dictionary<PropertyInfo, List<FieldInfo>>> PropertyFieldUsageCached = new Dictionary<Type, Dictionary<PropertyInfo, List<FieldInfo>>>();
        public static Dictionary<PropertyInfo, List<FieldInfo>> PropertyFieldUsage(Type t)
        {
            lock (PropertyFieldUsageCached)
            {
                Dictionary<PropertyInfo, List<FieldInfo>> cached;
                if (PropertyFieldUsageCached.TryGetValue(t, out cached))
                {
                    return cached;
                }
            }

            var ret = _GetPropertyFieldUsage(t);

            lock (PropertyFieldUsageCached)
            {
                PropertyFieldUsageCached[t] = ret;
            }

            return ret;
        }

        private static Dictionary<PropertyInfo, List<FieldInfo>> _GetPropertyFieldUsage(Type t)
        {
            if (t.IsValueType)
            {
                // We'll deal with value types in a bit...
                throw new NotImplementedException();
            }

            var ret = new Dictionary<PropertyInfo, List<FieldInfo>>();

            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic).Where(p => p.GetMethod != null);

			var module = t.Module;

            foreach (var prop in props)
            {
                var getMtd = prop.GetMethod;
                var mtdBody = getMtd.GetMethodBody();
                var il = mtdBody.GetILAsByteArray();

                var fieldHandles = _GetFieldHandles(il);

                var fieldInfos = fieldHandles.Select(f => module.ResolveField(f)).ToList();

                ret[prop] = fieldInfos;
            }

            return ret;
        }

        private static List<int> _GetFieldHandles(byte[] cil)
        {
            var ret = new List<int>();

            int i = 0;
            while (i < cil.Length)
            {
                int? fieldHandle;
                var startsAt = i;
                i += _ReadOp(cil, i, out fieldHandle);

                if (fieldHandle.HasValue)
                {
                    ret.Add(fieldHandle.Value);
                }
            }

            return ret;
        }

        private static int _ReadOp(byte[] cil, int ix, out int? fieldHandle)
        {
			const byte ContinueOpcode = 0xFE;

            int advance = 0;

            OpCode opcode;
            byte first = cil[ix];

            if (first == ContinueOpcode)
            {
                var next = cil[ix + 1];

                opcode = TwoByteOps[next];
                advance += 2;
            }
            else
            {
                opcode = OneByteOps[first];
                advance++;
            }

            fieldHandle = _ReadFieldOperands(opcode, cil, ix, ix + advance, ref advance);

            return advance;
        }

        private static int? _ReadFieldOperands(OpCode op, byte[] cil, int instrStart, int operandStart, ref int advance)
        {
			Func<int, int> readInt = (at) => cil[at] | (cil[at + 1] << 8) | (cil[at + 2] << 16) | (cil[at + 3] << 24);

            switch (op.OperandType)
            {
                case OperandType.InlineBrTarget:
                    advance += 4;
                    return null;

                case OperandType.InlineSwitch:
                    advance += 4;
                    var len = readInt(operandStart);
                    var offset1 = instrStart + len * 4;
                    for (var i = 0; i < len; i++)
                    {
                        advance += 4;
                    }
                    return null;

                case OperandType.ShortInlineBrTarget:
                    advance += 1;
                    return null;

                case OperandType.InlineField:
                    advance += 4;
                    var field = readInt(operandStart);
                    return field;

                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.InlineMethod:
                    advance += 4;
                    return null;

                case OperandType.InlineI:
                    advance += 4;
                    return null;

                case OperandType.InlineI8:
                    advance += 8;
                    return null;

                case OperandType.InlineNone:
                    return null;

                case OperandType.InlineR:
                    advance += 8;
                    return null;

                case OperandType.InlineSig:
                    advance += 4;
                    return null;

                case OperandType.InlineString:
                    advance += 4;
                    return null;

                case OperandType.InlineVar:
                    advance += 2;
                    return null;

                case OperandType.ShortInlineI:
                    advance += 1;
                    return null;

                case OperandType.ShortInlineR:
                    advance += 4;
                    return null;

                case OperandType.ShortInlineVar:
                    advance += 1;
                    return null;

                default: throw new Exception("Unexpected operand type [" + op.OperandType + "]");
            }
        }

        private static Dictionary<Type, Dictionary<FieldInfo, int>> FieldOffsetsInMemoryFieldCache = new Dictionary<Type, Dictionary<FieldInfo, int>>();
        public static Dictionary<FieldInfo, int> FieldOffsetsInMemory(Type t)
        {
            lock (FieldOffsetsInMemoryFieldCache)
            {
				Dictionary<FieldInfo, int> cached;
                if (FieldOffsetsInMemoryFieldCache.TryGetValue(t, out cached))
                {
                    return cached;
                }
            }

            var ret = _GetFieldOffsetsInMemory(t);

            lock (FieldOffsetsInMemoryFieldCache)
            {
                FieldOffsetsInMemoryFieldCache[t] = ret;
            }

            return ret;
        }

        private static Dictionary<FieldInfo, int> _GetFieldOffsetsInMemory(Type t)
        {
			if(t.IsValueType)
            {
				// We'll deal with value types in a bit...
				throw new NotImplementedException();
            }

            var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);

            var emit = Emit<Func<object, ulong[]>>.NewDynamicMethod("_GetOffsetsInMemory" + t.FullName);
            var retLoc = emit.DeclareLocal<ulong[]>("ret");

            emit.LoadConstant(fields.Length);	// ulong
            emit.NewArray(typeof(ulong));		// ulong[]
            emit.StoreLocal(retLoc);			// --empty--

            for(var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];

                emit.LoadLocal(retLoc);			// ulong[]
                emit.LoadConstant(i);			// ulong[] ulong

                emit.LoadArgument(0);			// ulong[] ulong param#0
                emit.CastClass(t);				// ulong[] ulong param#0

                emit.LoadFieldAddress(field);	// ulong[] ulong field&
                emit.Convert<ulong>();			// ulong[] ulong ulong

                emit.StoreElement<ulong>();		// --empty--
            }

            emit.LoadLocal(retLoc);			// ulong[]
            emit.Return();					// --empty--

            var getAddrs = emit.CreateDelegate();

            var obj = Activator.CreateInstance(t);

            var addrs = getAddrs(obj);

            var min = addrs.Min();

            var ret = new Dictionary<FieldInfo, int>();

            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];

                var addr = addrs[i];
                var offset = addr - min;

                ret[field] = (int)offset;
            }

            return ret;
        }

        private static IEnumerable<string> _ExtractStringConstants(Type type, HashSet<Type> alreadySeen = null)
        {
            alreadySeen = alreadySeen ?? new HashSet<Type>();

            if (alreadySeen.Contains(type))
            {
                yield break;
            }

            if (type.IsDictionaryType() || type.IsListType() || type.IsPrimitiveType())
            {
                yield break;
            }

            alreadySeen.Add(type);

            foreach (var prop in type.GetProperties())
            {
                yield return prop.Name;

                foreach (var str in _ExtractStringConstants(prop.PropertyType, alreadySeen))
                {
                    yield return str;
                }
            }

            foreach (var field in type.GetFields())
            {
                yield return field.Name;

                foreach (var str in _ExtractStringConstants(field.FieldType, alreadySeen))
                {
                    yield return str;
                }
            }
        }

        public static StringConstants ExtractStringConstants(Type type)
        {
            var strings = _ExtractStringConstants(type).ToList();
            var uniqueStrings = strings.Distinct().ToList();

            do
            {
                var overlappingSubstrings = uniqueStrings.Where(s => uniqueStrings.Any(t => t != s && t.IndexOf(s) != -1)).ToList();

                if (overlappingSubstrings.Count == 0) break;

                overlappingSubstrings.ForEach(s => uniqueStrings.Remove(s));
            } while (true);

            var joined = string.Concat(uniqueStrings);
            var map = new Dictionary<string, int>();

            foreach (var str in strings)
            {
                map[str] = joined.IndexOf(str);
            }

            return new StringConstants(joined, map);
        }
    }
}
