using System;
using System.IO;
using System.Text;

namespace VStudioCraft.Net
{
    // Big-endian primitive reader. Mirror image of PacketWriter — see that
    // file's header for rationale on byte order, custom-vs-BinaryReader, and
    // string framing.
    //
    // The reader uses ReadFully (loop until N bytes have arrived) rather than
    // a single Stream.Read because NetworkStream.Read is allowed to return
    // a short count even when more data is on the way. A naive single-call
    // read would corrupt the next packet whenever TCP splits a primitive
    // across two receive boundaries (i.e. always, eventually).
    //
    // EndOfStreamException is thrown if the underlying stream closes mid-
    // read; the session loop catches it and treats it as a clean disconnect.
    internal sealed class PacketReader
    {
        private readonly Stream _stream;
        private readonly byte[] _scratch = new byte[8];

        public PacketReader(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public Stream BaseStream => _stream;

        // ---- primitives -----------------------------------------------------

        public byte ReadByte()
        {
            ReadFully(_scratch, 0, 1);
            return _scratch[0];
        }

        public bool ReadBool() => ReadByte() != 0;

        public short ReadShort()
        {
            ReadFully(_scratch, 0, 2);
            return (short)((_scratch[0] << 8) | _scratch[1]);
        }

        public ushort ReadUShort() => unchecked((ushort)ReadShort());

        public int ReadInt()
        {
            ReadFully(_scratch, 0, 4);
            return (_scratch[0] << 24) | (_scratch[1] << 16) | (_scratch[2] << 8) | _scratch[3];
        }

        public long ReadLong()
        {
            ReadFully(_scratch, 0, 8);
            // Cast each byte to long BEFORE shifting — int << 32 wraps to 0
            // on a 32-bit shift count, dropping the top half silently.
            return ((long)_scratch[0] << 56)
                 | ((long)_scratch[1] << 48)
                 | ((long)_scratch[2] << 40)
                 | ((long)_scratch[3] << 32)
                 | ((long)_scratch[4] << 24)
                 | ((long)_scratch[5] << 16)
                 | ((long)_scratch[6] << 8)
                 |  (long)_scratch[7];
        }

        public float ReadFloat()
        {
            // FloatIntUnion is defined in PacketWriter.cs (same namespace) —
            // shared between read and write so the bit pattern interpretation
            // is impossible to drift apart accidentally.
            return new FloatIntUnion { Int = ReadInt() }.Float;
        }

        public double ReadDouble()
        {
            return new DoubleLongUnion { Long = ReadLong() }.Double;
        }

        // 2-byte length prefix, then UTF-8 bytes — must match PacketWriter.
        // We cap the wire-side length at 64 KiB inherently (ushort), and the
        // application caller is expected to range-check semantically (e.g.
        // "username must be 1..16 chars") after decode.
        public string ReadString()
        {
            int len = ReadUShort();
            if (len == 0) return string.Empty;
            var bytes = new byte[len];
            ReadFully(bytes, 0, len);
            return Encoding.UTF8.GetString(bytes);
        }

        // 4-byte length prefix payload. Caller is responsible for capping
        // the length before calling — a malicious client could otherwise
        // send Int32.MaxValue and force a 2 GiB allocation. NetSession
        // enforces a per-packet ceiling before invoking the parser.
        public byte[] ReadByteArray(int maxLen)
        {
            int len = ReadInt();
            if (len < 0) throw new InvalidDataException($"Negative length {len} in byte array.");
            if (len > maxLen) throw new InvalidDataException($"Byte array length {len} exceeds limit {maxLen}.");
            if (len == 0) return Array.Empty<byte>();
            var data = new byte[len];
            ReadFully(data, 0, len);
            return data;
        }

        // Loop until `count` bytes are buffered, or the stream ends. The
        // common single-call shape Read(buf, 0, n) is wrong on TCP — partial
        // reads happen often.
        private void ReadFully(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = _stream.Read(buffer, offset, count);
                if (n <= 0) throw new EndOfStreamException("Underlying stream closed mid-packet.");
                offset += n;
                count  -= n;
            }
        }
    }
}
