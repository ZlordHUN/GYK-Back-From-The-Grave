using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace GraveyardKeeperCoop.Network
{
    /// <summary>
    /// Multiplayer wire/session compatibility. Change this only when builds can no
    /// longer safely exchange handshake, transport, save-transfer, or gameplay data.
    /// Product releases are versioned independently through Directory.Build.props.
    /// </summary>
    public static class CoopProtocol
    {
        public const string Revision = "6";
    }

    public enum WorldEntryBarrierPhase : byte
    {
        Prepare = 0,
        Prepared = 1,
        Release = 2
    }

    public enum IntroSkipPhase : byte
    {
        Request = 0,
        Grant = 1,
        Progress = 2,
        Cancel = 3,
        Release = 4
    }

    public enum TradePhase : byte
    {
        Request = 0,
        Invite = 1,
        Accept = 2,
        Decline = 3,
        Begin = 4,
        Offer = 5,
        Ready = 6,
        Cancel = 7,
        Commit = 8,
        CommitAck = 9
    }

    public sealed class CutsceneCameraState
    {
        public bool FlyBack;
        public long TargetUniqueId;
        public string TargetCustomTag = "";
        public string TargetObjId = "";
        public string TargetName = "";
        public string TargetRelativePath = "";
        public Vector3 TargetPosition;
        public float Duration;
    }

    /// <summary>
    /// Binary message writer with pooled memory stream.
    /// Usage: using (var w = new MsgWriter(Op.Position)) { w.Write(x); ... return w.ToArray(); }
    /// </summary>
    public sealed class MsgWriter : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly BinaryWriter _writer;
        private bool _disposed;

        public MsgWriter(Op op, int initialCapacity = 256)
        {
            _stream = new MemoryStream(initialCapacity);
            _writer = new BinaryWriter(_stream, Encoding.UTF8);
            _writer.Write((byte)op);
        }

        public void Write(byte v) => _writer.Write(v);
        public void Write(sbyte v) => _writer.Write(v);
        public void Write(short v) => _writer.Write(v);
        public void Write(ushort v) => _writer.Write(v);
        public void Write(int v) => _writer.Write(v);
        public void Write(uint v) => _writer.Write(v);
        public void Write(long v) => _writer.Write(v);
        public void Write(ulong v) => _writer.Write(v);
        public void Write(float v) => _writer.Write(v);
        public void Write(double v) => _writer.Write(v);
        public void Write(bool v) => _writer.Write(v);

        /// <summary>
        /// Write string with length prefix (BinaryWriter's default format)
        /// </summary>
        public void Write(string v) => _writer.Write(v ?? "");

        /// <summary>
        /// Write Vector2 as two floats
        /// </summary>
        public void Write(Vector2 v)
        {
            _writer.Write(v.x);
            _writer.Write(v.y);
        }

        /// <summary>
        /// Write Vector3 as three floats
        /// </summary>
        public void Write(Vector3 v)
        {
            _writer.Write(v.x);
            _writer.Write(v.y);
            _writer.Write(v.z);
        }

        /// <summary>
        /// Write raw bytes without length prefix
        /// </summary>
        public void WriteRaw(byte[] data)
        {
            _writer.Write(data);
        }

        /// <summary>
        /// Write bytes with length prefix
        /// </summary>
        public void WriteBytes(byte[] data)
        {
            _writer.Write(data.Length);
            _writer.Write(data);
        }

        /// <summary>
        /// Write a portion of bytes without length prefix (for chunked transfers)
        /// </summary>
        public void WriteBytes(byte[] data, int offset, int count)
        {
            _writer.Write(data, offset, count);
        }

        /// <summary>
        /// Get the complete message as byte array
        /// </summary>
        public byte[] ToArray()
        {
            _writer.Flush();
            return _stream.ToArray();
        }

        /// <summary>
        /// Current message length
        /// </summary>
        public int Length => (int)_stream.Length;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
            _stream?.Dispose();
        }
    }

    /// <summary>
    /// Binary message reader. Lightweight struct for reading binary messages.
    /// </summary>
    public struct MsgReader
    {
        private readonly BinaryReader _reader;
        private readonly MemoryStream _stream;

        /// <summary>
        /// The opcode of this message
        /// </summary>
        public readonly Op Op;

        /// <summary>
        /// Total length of the message
        /// </summary>
        public readonly int Length;

        public MsgReader(byte[] buffer, int length)
        {
            Length = length;
            _stream = new MemoryStream(buffer, 0, length, writable: false);
            _reader = new BinaryReader(_stream, Encoding.UTF8);
            Op = (Op)_reader.ReadByte();
        }

        public byte ReadByte() => _reader.ReadByte();
        public sbyte ReadSByte() => _reader.ReadSByte();
        public short ReadInt16() => _reader.ReadInt16();
        public ushort ReadUInt16() => _reader.ReadUInt16();
        public int ReadInt32() => _reader.ReadInt32();
        public uint ReadUInt32() => _reader.ReadUInt32();
        public long ReadInt64() => _reader.ReadInt64();
        public ulong ReadUInt64() => _reader.ReadUInt64();
        public float ReadFloat() => _reader.ReadSingle();
        public double ReadDouble() => _reader.ReadDouble();
        public bool ReadBool() => _reader.ReadBoolean();
        public string ReadString() => _reader.ReadString();

        public Vector2 ReadVector2() => new Vector2(_reader.ReadSingle(), _reader.ReadSingle());
        public Vector3 ReadVector3() => new Vector3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());

        /// <summary>
        /// Read raw bytes (fixed length, no prefix)
        /// </summary>
        public byte[] ReadRawBytes(int count) => _reader.ReadBytes(count);

        /// <summary>
        /// Read bytes with length prefix
        /// </summary>
        public byte[] ReadBytes()
        {
            int length = _reader.ReadInt32();
            return _reader.ReadBytes(length);
        }

        /// <summary>
        /// Bytes remaining in the message
        /// </summary>
        public int Remaining => Length - (int)_stream.Position;
    }

    /// <summary>
    /// Extension methods for binary protocol
    /// </summary>
    public static class BinaryProtocolExtensions
    {
        /// <summary>
        /// Check if a byte array is a binary protocol message (vs legacy string message)
        /// Binary messages always start with a valid Op byte (1-255)
        /// Legacy string messages start with ASCII letters
        /// </summary>
        public static bool IsBinaryMessage(byte[] data, int length)
        {
            if (data == null || length < 1) return false;

            byte firstByte = data[0];

            // Check if the first byte matches any known Op enum value.
            // Legacy string messages start with ASCII letters (65-90, 97-122)
            // so we check against all defined opcodes explicitly.
            return System.Enum.IsDefined(typeof(Op), firstByte);
        }
    }
}
