// Microsoft Compound File Binary (OLE structured storage) reader. Spec: [MS-CFB].
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InvRe
{
    public class CfbEntry
    {
        public int Id;
        public string Name;
        public int Type;              // 1 storage, 2 stream, 5 root
        public int Left, Right, Child;
        public Guid Clsid;
        public uint StartSector;
        public long Size;
        public string Path;
        public List<CfbEntry> Children = new List<CfbEntry>();
    }

    public class CfbFile
    {
        const uint ENDOFCHAIN = 0xFFFFFFFE;
        const uint FREESECT = 0xFFFFFFFF;

        byte[] data;
        List<uint> fat = new List<uint>();
        List<uint> miniFat = new List<uint>();
        byte[] miniStream = new byte[0];
        uint miniCutoff;
        int miniSectorSize;

        public int MajorVersion;
        public int SectorSize;
        public List<CfbEntry> Entries = new List<CfbEntry>();
        public CfbEntry Root { get { return Entries[0]; } }

        public CfbFile(string path)
        {
            data = File.ReadAllBytes(path);
            if (data.Length < 512 || BitConverter.ToUInt64(data, 0) != 0xE11AB1A1E011CFD0UL)
                throw new InvalidDataException("Not an OLE compound file: " + path);
            MajorVersion = BitConverter.ToUInt16(data, 0x1A);
            SectorSize = 1 << BitConverter.ToUInt16(data, 0x1E);
            miniSectorSize = 1 << BitConverter.ToUInt16(data, 0x20);
            uint firstDir = BitConverter.ToUInt32(data, 0x30);
            miniCutoff = BitConverter.ToUInt32(data, 0x38);
            uint firstMiniFat = BitConverter.ToUInt32(data, 0x3C);
            uint firstDifat = BitConverter.ToUInt32(data, 0x44);
            uint numDifat = BitConverter.ToUInt32(data, 0x48);

            // FAT sector list: 109 entries in the header, the rest in chained DIFAT sectors.
            List<uint> fatSectors = new List<uint>();
            for (int i = 0; i < 109; i++)
            {
                uint s = BitConverter.ToUInt32(data, 0x4C + i * 4);
                if (s != FREESECT) fatSectors.Add(s);
            }
            int perDifat = SectorSize / 4 - 1;
            uint d = firstDifat;
            for (uint n = 0; n < numDifat && d < ENDOFCHAIN; n++)
            {
                long off = SectorOffset(d);
                for (int i = 0; i < perDifat; i++)
                {
                    uint s = BitConverter.ToUInt32(data, (int)(off + i * 4));
                    if (s != FREESECT) fatSectors.Add(s);
                }
                d = BitConverter.ToUInt32(data, (int)(off + perDifat * 4));
            }
            foreach (uint s in fatSectors)
            {
                long off = SectorOffset(s);
                for (int i = 0; i < SectorSize / 4; i++)
                    fat.Add(off + i * 4 + 4 <= data.Length ? BitConverter.ToUInt32(data, (int)(off + i * 4)) : FREESECT);
            }

            byte[] dir = ReadChain(firstDir, -1);
            for (int i = 0; i + 128 <= dir.Length; i += 128)
            {
                CfbEntry e = new CfbEntry();
                e.Id = i / 128;
                int nameLen = BitConverter.ToUInt16(dir, i + 0x40);
                e.Name = nameLen >= 2 ? Encoding.Unicode.GetString(dir, i, Math.Min(nameLen, 64) - 2) : "";
                e.Type = dir[i + 0x42];
                e.Left = BitConverter.ToInt32(dir, i + 0x44);
                e.Right = BitConverter.ToInt32(dir, i + 0x48);
                e.Child = BitConverter.ToInt32(dir, i + 0x4C);
                byte[] g = new byte[16];
                Array.Copy(dir, i + 0x50, g, 0, 16);
                e.Clsid = new Guid(g);
                e.StartSector = BitConverter.ToUInt32(dir, i + 0x74);
                e.Size = (long)BitConverter.ToUInt64(dir, i + 0x78);
                if (MajorVersion == 3) e.Size &= 0xFFFFFFFFL;
                Entries.Add(e);
            }

            miniStream = ReadChain(Root.StartSector, Root.Size);
            if (firstMiniFat < ENDOFCHAIN)
            {
                byte[] mf = ReadChain(firstMiniFat, -1);
                for (int i = 0; i + 4 <= mf.Length; i += 4) miniFat.Add(BitConverter.ToUInt32(mf, i));
            }

            Root.Path = "";
            BuildTree(Root, new HashSet<int>());
        }

        long SectorOffset(uint s) { return ((long)s + 1) * SectorSize; }

        byte[] ReadChain(uint start, long size)
        {
            MemoryStream ms = new MemoryStream();
            uint s = start;
            int guard = 0;
            while (s < fat.Count && guard++ <= fat.Count)
            {
                long off = SectorOffset(s);
                int n = (int)Math.Min(SectorSize, data.Length - off);
                if (n <= 0) break;
                ms.Write(data, (int)off, n);
                if (size >= 0 && ms.Length >= size) break;
                s = fat[(int)s];
            }
            byte[] r = ms.ToArray();
            if (size >= 0 && r.Length > size) Array.Resize(ref r, (int)size);
            return r;
        }

        byte[] ReadMiniChain(uint start, long size)
        {
            MemoryStream ms = new MemoryStream();
            uint s = start;
            int guard = 0;
            while (s < miniFat.Count && guard++ <= miniFat.Count)
            {
                long off = (long)s * miniSectorSize;
                int n = (int)Math.Min(miniSectorSize, miniStream.Length - off);
                if (n <= 0) break;
                ms.Write(miniStream, (int)off, n);
                if (ms.Length >= size) break;
                s = miniFat[(int)s];
            }
            byte[] r = ms.ToArray();
            if (r.Length > size) Array.Resize(ref r, (int)size);
            return r;
        }

        public byte[] ReadStream(CfbEntry e)
        {
            if (e.Size == 0) return new byte[0];
            return e.Size < miniCutoff ? ReadMiniChain(e.StartSector, e.Size) : ReadChain(e.StartSector, e.Size);
        }

        public CfbEntry Find(string path)
        {
            foreach (CfbEntry e in Entries) if (e.Path == path) return e;
            return null;
        }

        void BuildTree(CfbEntry parent, HashSet<int> seen)
        {
            Stack<int> st = new Stack<int>();
            st.Push(parent.Child);
            while (st.Count > 0)
            {
                int id = st.Pop();
                if (id < 0 || id >= Entries.Count || !seen.Add(id)) continue;
                CfbEntry e = Entries[id];
                e.Path = parent.Path + "/" + e.Name;
                parent.Children.Add(e);
                st.Push(e.Left);
                st.Push(e.Right);
                if (e.Type == 1) BuildTree(e, seen);
            }
            parent.Children.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }
    }
}
