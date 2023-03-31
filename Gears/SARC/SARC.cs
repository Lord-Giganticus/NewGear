using NewGear.GearSystem.Interfaces;
using NewGear.Trees.TrueTree;
using NewGear.IO;
using System.Text;

namespace NewGear.Gears.Containers;

public class SARC : IContainerGear, IModifiableGear
{
    public BranchNode RootNode { get; set; } = new("*") { Metadata = new SARCMetadata() };
    public ICompressionGear? CompressionAlgorithm { get; set; }
    public ByteOrder ByteOrder { get; set; }
    public Encoding Encoding { get; set; } = Encoding.ASCII;

    public bool HashOnly { get; set; } = false;

    public byte[] Write()
    {
        using BinaryStream bs = new()
        {
            ByteOrder = ByteOrder,
            DefaultEncoding = Encoding
        };
        bs.Write("SARC");
        bs.Write((ushort)0x14);
        bs.Write((ushort)0xFEFF);
        bs.Write((uint)0x00);
        bs.Write((uint)0x00);
        bs.Write((ushort)0x100);
        bs.Write((ushort)0x00);
        bs.Write("SFAT");
        bs.Write((ushort)0xc);
        bs.Write((ushort)RootNode.ChildLeaves.Count);
        bs.Write((uint)0x00000065);
        List<ulong> offsets = new();
        string[] keys = RootNode.ChildLeaves.Select(x => x.ID).Cast<string>()
            .OrderBy(x => HashOnly ? StringHashToUint(x) : NameHash(x)).ToArray();
        var files = RootNode.ChildLeaves.Select(x => (x.ID, x.Contents)).Cast<(string, byte[])>()
            .ToDictionary(x => x.Item1, x => x.Item2);
        foreach (var k in keys)
        {
            if (HashOnly)
                bs.Write(StringHashToUint(k));
            else
                bs.Write(NameHash(k));
            offsets.Add(bs.Position);
            bs.Write((uint)0);
            bs.Write((uint)0);
            bs.Write((uint)0);
        }
        bs.Write("SFNT");
        bs.Write((ushort)0x8);
        bs.Write((ushort)0);
        List<ulong> stringoffs = new();
        foreach (var k in keys)
        {
            stringoffs.Add(bs.Position);
            bs.Write(k);
            bs.Align(4);
        }
        bs.Align(0x1000);
        List<ulong> fileoffs = new();
        foreach (var k in keys)
        {
            bs.Align(GuessFileAlignment(files[k]));
            fileoffs.Add(bs.Position);
            bs.Write(files[k]);
        }
        for (int i = 0; i < offsets.Count; i++)
        {
            var off = offsets[i];
            bs.Position = off;
            if (!HashOnly)
                bs.Write(0x01000000 | (uint)((stringoffs[i] - stringoffs[0]) / 4));
            else
                bs.Write<uint>(0);
            bs.Write((uint)(fileoffs[i] - fileoffs[0]));
            bs.Write((uint)(fileoffs[i] + (ulong)files[keys[i]].Length - fileoffs[0]));
        }
        bs.Position = 0x08;
        bs.Write((uint)bs.Length);
        bs.Write((uint)fileoffs[0]);
        return bs.ToArray();
    }

    public void Read(byte[] data)
    {
        using BinaryStream stream = new(data)
        {
            DefaultEncoding = Encoding
        };
        var metadata = new SARCMetadata(stream);
        var sfat = metadata.SFAT;
        var sfnt = metadata.SFNT;
        var startingoff = metadata.Header.StartingOff;
        RootNode.Metadata = metadata;
        if (sfat.NodeCount > 0)
            HashOnly = sfat.Nodes[0].FileBool;
        for (int m = 0; m < sfat.NodeCount; m++)
        {
            stream.Position = sfat.Nodes[m].NodeOffset + startingoff;
            byte[] temp;
            if (m == 0)
            {
                temp = stream.Read<byte>((int)sfat.Nodes[0].EON);
            } else
            {
                int tempint = (int)sfat.Nodes[m].EON - (int)sfat.Nodes[m].NodeOffset;
                temp = stream.Read<byte>(tempint);
            }
            LeafNode node;
            if (sfat.Nodes[m].FileBool)
            {
                node = new(sfnt.FileNames[m])
                {
                    Contents = temp
                };
            } else
            {
                node = new(sfat.Nodes[m].Hash.ToString("X8"))
                {
                    Contents = temp
                };
            }
            RootNode.AddChild(node);
        }
    }

    static uint StringHashToUint(string name)
    {
        if (name.Contains('.'))
            name = name.Split('.')[0];
        if (name.Length != 8) throw new Exception("Invalid hash length");
        return Convert.ToUInt32(name, 16);
    }

    static uint NameHash(string name)
    {
        uint result = 0;
        for (int i = 0; i < name.Length; i++)
        {
            result = name[i] + result * 0x00000065;
        }
        return result;
    }

    public static uint GuessFileAlignment(byte[] f)
    {
        if (f.Matches("SARC")) return 0x2000;
        else if (f.Matches("Yaz")) return 0x80;
        else if (f.Matches("YB") || f.Matches("BY")) return 0x80;
        else if (f.Matches("FRES") || f.Matches("Gfx2") || f.Matches("AAHS") || f.Matches("BAHS")) return 0x2000;
        else if (f.Matches("BNTX") || f.Matches("BNSH") || f.Matches("FSHA")) return 0x1000;
        else if (f.Matches("FFNT")) return 0x2000;
        else if (f.Matches("CFNT")) return 0x80;
        else if (f.Matches(1, "STM") /* *STM */ || f.Matches(1, "WAV") || f.Matches("FSTP")) return 0x20;
        else if (f.Matches("CTPK")) return 0x10;
        else if (f.Matches("CGFX")) return 0x80;
        else if (f.Matches("AAMP")) return 8;
        else if (f.Matches("MsgStdBn") || f.Matches("MsgPrjBn")) return 0x80;
        else return 0x4;
    }
}

static class Ext
{
    public static bool Matches(this byte[] b, string str)
    {
        if (b.Length != str.Length)
            return false;
        for (int i = 0; i < str.Length; i++)
        {
            if (b[i] != str[i])
                return false;
        }
        return true;
    }

    public static bool Matches(this byte[] b, uint additionalChars, string str)
    {
        if (b.Length != str.Length + additionalChars)
            return false;
        for (int i = 0; i < str.Length; i++)
        {
            if (b[i] != str[i])
                return false;
        }
        return true;
    }
}

public class SARCMetadata
{
    public Header Header = new();
    public SFAT SFAT = new();
    public SFNT SFNT = new();
    public ByteOrder ByteOrder { get; set; }
    public Encoding Encoding { get; set; } = Encoding.ASCII;

    public void Read(byte[] data)
    {
        using BinaryStream stream = new(data)
        {
            DefaultEncoding = Encoding
        };
        Header = stream;
        SFAT = stream;
        SFNT = new(Header, stream);
    }

    public SARCMetadata(BinaryStream stream)
    {
        Header = stream;
        SFAT = stream;
        SFNT = new(Header, stream);
    }
    
    public SARCMetadata() { }
}

public class Header
{
    public ushort ChunkLen;
    public ushort Bom;
    public uint Size;
    public uint StartingOff;
    public uint Unk;
    public ByteOrder ByteOrder { get; set; }
    public Encoding Encoding { get; set; } = Encoding.ASCII;
    public void Read(byte[] data)
    {
        using BinaryStream stream = new(data)
        {
            DefaultEncoding = Encoding
        };
        var magic = stream.ReadString(4);
        if (magic != "SARC" && magic != "CRAS")
            throw new InvalidDataException("The given file is not a SARC/CRAS.");
        ChunkLen = stream.Read<ushort>();
        ByteOrder = stream.Read<ByteOrder>();
        Bom = (ushort)ByteOrder;
        Size = stream.Read<uint>();
        StartingOff = stream.Read<uint>();
        Unk = stream.Read<uint>();
    }

    public static implicit operator Header(BinaryStream stream)
    {
        Header res = new()
        {
            ByteOrder = stream.ByteOrder,
            Encoding = stream.DefaultEncoding
        };
        res.Read(stream.ToArray());
        stream.ByteOrder = res.ByteOrder;
        stream.Position += 16;
        return res;
    }
}

public class SFAT
{
    public List<Node> Nodes = new();
    public ushort ChunkSize;
    public ushort NodeCount;
    public uint HashMultiplier;
    public class Node
    {
        public uint Hash;
        public bool FileBool;
        public byte Unknown;
        public ushort FileNameOffset;
        public uint NodeOffset;
        public uint EON;
        public ByteOrder ByteOrder { get; set; }
        public Encoding Encoding { get; set; } = Encoding.ASCII;

        public void Read(byte[] data)
        {
            using BinaryStream stream = new(data)
            {
                ByteOrder = ByteOrder,
                DefaultEncoding = Encoding
            };
            Hash = stream.Read<uint>();
            var attributes = stream.Read<uint>();
            FileBool = (attributes >> 24) != 0;
            Unknown = (byte)((attributes >> 16) & 0xFF);
            FileNameOffset = (ushort)(attributes & 0xFFFF);
            NodeOffset = stream.Read<uint>();
            EON = stream.Read<uint>();
        }

        public static implicit operator Node(BinaryStream stream)
        {
            Node res = new()
            {
                ByteOrder = stream.ByteOrder,
                Encoding = stream.DefaultEncoding
            };
            res.Read(stream.ToArray());
            stream.Position += 16;
            return res;
        }
    }
    public ByteOrder ByteOrder { get; set; }
    public Encoding Encoding { get; set; } = Encoding.ASCII;

    public void Read(byte[] data)
    {
        using BinaryStream stream = new(data)
        {
            ByteOrder = ByteOrder,
            DefaultEncoding = Encoding
        };
        ChunkSize = stream.Read<ushort>();
        NodeCount = stream.Read<ushort>();
        HashMultiplier = stream.Read<uint>();
        Nodes = new(NodeCount);
        for (int i = 0; i < NodeCount; i++)
        {
            Nodes.Add(stream);
        }
    }

    public static implicit operator SFAT(BinaryStream stream)
    {
        SFAT res = new()
        {
            ByteOrder = stream.ByteOrder,
            Encoding = stream.DefaultEncoding
        };
        res.Read(stream.ToArray());
        stream.Position += 6;
        stream.Position += 16 * (ulong)res.Nodes.Count;
        return res;
    }
}

public class SFNT
{
    public List<string> FileNames = new();
    public uint ChunkID;
    public ushort ChunkSize;
    public ushort Unknown;
    public ByteOrder ByteOrder { get; set; }
    public Encoding Encoding { get; set; } = Encoding.ASCII;

    public ulong Start { get; set; }

    public void Read(byte[] data)
    {
        using BinaryStream stream = new(data)
        {
            ByteOrder = ByteOrder,
            DefaultEncoding = Encoding
        };
        ChunkID = stream.Read<uint>();
        ChunkSize = stream.Read<ushort>();
        Unknown = stream.Read<ushort>();
        string tmp = stream.ReadString((int)(Start - stream.Position));
        string[] names = tmp.Split((char)0x00);
        FileNames.AddRange(names.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    public SFNT() { }

    public SFNT(Header header, BinaryStream stream)
    {
        ByteOrder = stream.ByteOrder;
        Encoding = stream.DefaultEncoding;
        Start = header.StartingOff;
        Read(stream.ToArray());
        var size = Start - stream.Position;
        stream.Position += 6;
        stream.Position += size;
    }
}