using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace GraveyardKeeperCoop.Network
{
    /// <summary>
    /// Binary message opcodes. Each message type has a 1-byte opcode followed by payload.
    /// More efficient than string-based protocol for frequent messages like position updates.
    /// </summary>
    public enum Op : byte
    {
        // Connection/Handshake (1-9)
        Hello = 1,
        Ping = 2,
        Pong = 9,
        Invite = 3,
        GameStart = 4,
        GameLoaded = 5,
        ReadyToPlay = 6,
        LobbyRequest = 7,  // Request lobby ID from host (for Steam Rich Presence join)
        LobbyInfo = 8,     // Host responds with lobby ID

        // Player State (10-19)
        PlayerPosition = 10,
        PlayerState = 11,
        PlayerAnimation = 12, // Animation state sync (tool use, attack, etc.)
        PlayerParity = 13,    // Full player stat/buff/action parity snapshot

        // Game Sync (20-29)
        TimeSync = 20,
        WGODestroy = 21,
        WeatherSync = 22,
        InventorySync = 23,
        WGOStateSync = 24,
        CraftSync = 25,
        TechSync = 26,
        QuestSync = 27,
        ZoneNavSync = 28,
        CombatSync = 29,

        // Dialogue (30-39)
        DialogueStart = 30,
        DialogueAdvance = 31,
        DialogueEnd = 32,
        DialogueChoice = 33,
        DialogueBubble = 34,

        // Save Transfer (40-49)
        SaveRequest = 40,
        SaveStart = 41,
        SaveChunk = 42,
        SaveEnd = 43,
        SaveAck = 44,

        // Chat (50-59)
        ChatMessage = 50,
        ChatHistory = 51,

        // Player Cosmetics (60-69)
        PlayerCosmetics = 60,
        CosmeticsRequest = 61,

        // Cutscene/FlowScript Sync (70-79)
        CutsceneSync = 70,     // FlowScript name + optional origin/facing/zone scope for deferred activation
        CutsceneWalkTo = 71,  // Tell remote player to walk to a position next to the local player and face a direction
        CutsceneComplete = 76,    // FlowScript finished/remotely completed; deferred receivers should skip replay
        CutsceneCamera = 78,      // Mirror live cutscene CameraFlyTo/CameraFlyBack commands

        // Lobby host save slot mirror (72-74)
        HostSaveListRequest = 72, // Client -> host: please send me your save slot list
        HostSaveList = 73,        // Host -> client: here is the list of save slots (+ currently selected)
        HostSaveSelected = 74,    // Host -> all: this is the filename I just selected

        // NPC interaction sync (75)
        NpcInteraction = 75,      // Replay an NPC interaction (dialogue, intro, etc.) on the remote machine

        // Intro skip sync (77)
        SkipIntro = 77,           // Tell remote player to skip the intro cutscene

        // Player param sync (80)
        PlayerParamSync = 80,
        PlayerVisualSync = 81,

        // Spawn sync (85)
        SpawnSync = 85,

        // Dungeon sync (90)
        DungeonSync = 90,

        // Worker sync (95)
        WorkerSync = 95,

        // Fishing sync (96)
        FishingSync = 96,

        // DLC sync (97)
        DLCSync = 97,

        // Live transform and host-authority lanes (98-100)
        WGOTransformSync = 98,
        InteractionRequest = 99,
        InteractionZeroHp = 100,
        NpcVisualSync = 101,

        // Lobby control (102-109)
        LobbyKick = 102,          // Host asks a client to leave the lobby/session
        WorkIndicatorSync = 103,  // Remote player-owned HP/crafting progress indicator
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
