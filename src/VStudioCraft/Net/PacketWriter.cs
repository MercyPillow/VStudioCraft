using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VStudioCraft.Net
{
    // Reinterpret-cast helpers for float<->int and double<->long without
    // requiring /unsafe. BitConverter.SingleToInt32Bits / DoubleToInt64Bits
    // arrived in .NET Core 2.1; net472 doesn't have them, so we drop in
    // an explicit-layout union with both fields at offset 0. The JIT
    // collapses this to the same single-instruction reinterpret as the
    // unsafe version.
    [StructLayout(LayoutKind.Explicit)]
    internal struct FloatIntUnion
    {
        [FieldOffset(0)] public int   Int;
        [FieldOffset(0)] public float Float;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct DoubleLongUnion
    {
        [FieldOffset(0)] public long   Long;
        [FieldOffset(0)] public double Double;
    }

    // Big-endian primitive writer for the VStudioCraft network protocol.
    //
    // Why big-endian: matches the Alpha 1.1.2 wire convention this protocol
    // is shaped after. It also means a packet hex-dumped during debugging
    // reads in the same byte order as the source-code field declarations
    // (most significant byte first) — handy when tracing handshake bugs
    // by eye.
    //
    // Why a custom writer instead of BinaryWriter: BCL's BinaryWriter is
    // little-endian on every modern runtime and, more painfully, writes
    // strings with a 7-bit-encoded length prefix that no other protocol
    // in the world uses. Rolling our own keeps the bytes-on-wire trivially
    // greppable and avoids carrying a "no really, big-endian please"
    // adapter over every call site.
    //
    // Buffering: this writer wraps a Stream and writes directly. The caller
    // is responsible for using a buffered network stream (or framing into
    // a MemoryStream first, then atomically flushing) — see NetSession
    // for the standard "build packet in MemoryStream, send in one Write"
    // pattern that prevents partial-packet races on the read side.
    internal sealed class PacketWriter
    {
        private readonly Stream _stream;
        // Scratch buffer for primitive writes. 8 bytes covers everything up
        // to a long/double; strings allocate fresh each call (rare path).
        private readonly byte[] _scratch = new byte[8];

        public PacketWriter(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public Stream BaseStream => _stream;

        // ---- primitives -----------------------------------------------------

        public void WriteByte(byte value)
        {
            _scratch[0] = value;
            _stream.Write(_scratch, 0, 1);
        }

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteShort(short value)
        {
            _scratch[0] = (byte)(value >> 8);
            _scratch[1] = (byte)value;
            _stream.Write(_scratch, 0, 2);
        }

        public void WriteUShort(ushort value) => WriteShort(unchecked((short)value));

        public void WriteInt(int value)
        {
            _scratch[0] = (byte)(value >> 24);
            _scratch[1] = (byte)(value >> 16);
            _scratch[2] = (byte)(value >> 8);
            _scratch[3] = (byte)value;
            _stream.Write(_scratch, 0, 4);
        }

        public void WriteLong(long value)
        {
            _scratch[0] = (byte)(value >> 56);
            _scratch[1] = (byte)(value >> 48);
            _scratch[2] = (byte)(value >> 40);
            _scratch[3] = (byte)(value >> 32);
            _scratch[4] = (byte)(value >> 24);
            _scratch[5] = (byte)(value >> 16);
            _scratch[6] = (byte)(value >> 8);
            _scratch[7] = (byte)value;
            _stream.Write(_scratch, 0, 8);
        }

        public void WriteFloat(float value)
        {
            // Reinterpret the 32 bits without a BitConverter round-trip
            // (which is little-endian and would need a Reverse() pass).
            // The union punning compiles to a single mov on x64 — same
            // codegen as the *(int*)& trick but doesn't require /unsafe.
            WriteInt(new FloatIntUnion { Float = value }.Int);
        }

        public void WriteDouble(double value)
        {
            WriteLong(new DoubleLongUnion { Double = value }.Long);
        }

        // String wire format: 2-byte unsigned big-endian length, then UTF-8
        // bytes. UTF-8 (vs Alpha's UCS-2) keeps usernames and chat ASCII-clean
        // at half the size for the common case while still allowing non-Latin
        // characters. 64 KiB max per string is plenty for usernames (16 chars)
        // and chat lines (kept under 256 by convention).
        public void WriteString(string value)
        {
            if (value == null) value = string.Empty;
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > ushort.MaxValue)
                throw new ArgumentException($"String length {bytes.Length} exceeds wire maximum {ushort.MaxValue}.", nameof(value));
            WriteUShort((ushort)bytes.Length);
            if (bytes.Length > 0) _stream.Write(bytes, 0, bytes.Length);
        }

        // Write a length-prefixed byte payload. Used by ChunkLoad for the
        // gzipped block bytes. The prefix is a 4-byte int (not 2-byte ushort)
        // because chunk payloads can exceed 64 KiB if compression is poor.
        public void WriteByteArray(byte[] data)
        {
            if (data == null)
            {
                WriteInt(0);
                return;
            }
            WriteInt(data.Length);
            if (data.Length > 0) _stream.Write(data, 0, data.Length);
        }
    }
}
