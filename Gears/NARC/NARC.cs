﻿using NewGear.GearSystem.AbstractGears;
using NewGear.TrueTree;
using Syroot.BinaryData;
using System.Diagnostics;
using System.Text;

// Code adapted from NARCSharp (https://github.com/Jenrikku/NARCSharp)
namespace NewGear.Gears.Containers {
    public class NARC : ContainerGear {
        public NARC() {
            // Initialize (extra code specific to gear)
            Header = new NARCHeader() {
                Version = 0x0100,
                bfntUnknown = new byte[] {
                    0x00, 0x00, 0x01, 0x00
                }
            };
        }

        /// <returns>Whether or not the given data matches this gear's specifications.</returns>
        public static new bool Identify(byte[] data) {
            return Encoding.ASCII.GetString(data[0..4]) == "NARC";
        }

        public override void Read(Stream stream, Encoding encoding, bool leaveOpen = false) {
            using BinaryDataReader reader = new(stream, encoding, leaveOpen);

            // Magic check:
            if(reader.ReadString(4) != "NARC")
                throw new InvalidDataException("The given file is not a NARC.");

            // Reads the byte order and changes the one used by the reader if needed:
            ByteOrder = (ByteOrder) reader.ReadUInt16();
            reader.ByteOrder = ByteOrder;

            // NullPointer prevention.
            if(Header is null)
                Header = new NARCHeader();

            Header.Version = reader.ReadUInt16();

            reader.Position += 4; // Length skip (calculated when writing).

            Debug.Assert(reader.ReadUInt16() == 16); // Header length check.
            Debug.Assert(reader.ReadUInt16() == 3);  // Entry count check.

            Debug.Assert(reader.ReadString(4) == "BTAF"); // BFAT magic check.

            // The positions where the sections' reading was left last time.
            long bfatIndex = reader.Position + 8;
            long bfntIndex = reader.Position + reader.ReadUInt32() - 4; // From the BFAT length.
            long fimgIndex;

            uint fileCount = reader.ReadUInt32();

            uint currentFileOffset;
            uint currentFileEnd;

            #region BFNT preparations
            reader.Position = bfntIndex;
            Debug.Assert(reader.ReadString(4) == "BTNF"); // BFNT magic check.

            fimgIndex = reader.Position + reader.ReadUInt32() - 4; // Sets FIMG section begining.

            using(reader.TemporarySeek()) {
                reader.Position = fimgIndex;
                Debug.Assert(reader.ReadString(4) == "GMIF"); // FIMG magic check.
                fimgIndex += 8; // Skips magic and length.
            }

            uint bfntUnknownLength = reader.ReadUInt32() - 4;

            Header.bfntUnknown = new byte[bfntUnknownLength];
            for(int i = 0; i < bfntUnknownLength; i++)
                Header.bfntUnknown[i] = reader.ReadByte();
            #endregion

            Node currentFolder = RootNode;
            for(int i = 0; i < fileCount; i++) {
                byte nameLength = reader.ReadByte();

                if(nameLength == 0x00) { // End of the "folder".
                    currentFolder = currentFolder.Parent ?? currentFolder;
                    i--;
                    continue;
                }

                if(nameLength >= 0x80) { // If it is a "folder".
                    Node childFolder = new(reader.ReadString(nameLength & 0x7F));
                    childFolder.Contents.Add(true); // It is a "folder". (Contents[0] == true)

                    currentFolder = currentFolder.AddChild(childFolder);

                    reader.Position += 2;
                    i--;
                    continue;
                }

                // Decompress BFAT section:
                using(reader.TemporarySeek()) {
                    reader.Position = bfatIndex;

                    currentFileOffset = reader.ReadUInt32();
                    currentFileEnd = reader.ReadUInt32();

                    bfatIndex = reader.Position;
                }

                Node child = new(reader.ReadString(nameLength));
                child.Contents.Add(false); // It is a file. (Contents[0] == false)

                // Decompress FIMG section:
                using(reader.TemporarySeek()) {
                    reader.Position = fimgIndex + currentFileOffset;
                    child.Contents.Add(reader.ReadBytes((int) (currentFileEnd - currentFileOffset)));
                }

                currentFolder.AddChild(child);
            }
        }

        public override void Write(Stream stream, Encoding encoding, bool leaveOpen = false) {
            using BinaryDataWriter writer = new(stream, encoding, leaveOpen) {
                ByteOrder = ByteOrder
            };

            #region Header
            writer.Write("NARC",
                BinaryStringFormat.NoPrefixOrTermination); // Magic string.
            writer.Write((ushort) 0xFFFE);                 // Byte order.
            writer.Write(Header?.Version);                 // Version.
            writer.Position += 4;                          // Length skip. (calculated later)
            writer.Write((ushort) 0x10);                   // Header length.
            writer.Write((ushort) 0x03);                   // Section count.
            #endregion

            #region BFAT (pre)
            writer.Write("BTAF",
                BinaryStringFormat.NoPrefixOrTermination); // Magic string.

            long bfatLengthIndex = writer.Position; // For calculation the position later.

            writer.Position += 4; // Length skip. (calculated later)

            List<byte[]> fileContainer = new(); // Contains all the files without "folders".
            FolderIterate(RootNode);

            writer.Write(fileContainer.Count);               // Number of files. (hash pairs)
            writer.Write(new byte[fileContainer.Count * 8]); // Reserve hashes' positions.
            #endregion

            #region BFNT
            writer.Write("BTNF",
                BinaryStringFormat.NoPrefixOrTermination); // Magic string.

            long bfntLengthIndex = writer.Position; // For calculation the position later.

            writer.Position += 4;                         // Length skip. (calculated later)
            writer.Write(Header?.bfntUnknown.Length + 4); // Header length.
            writer.Write(Header?.bfntUnknown);            // Header data.

            byte folderCount = 1;
            WriteBFNTEntry(RootNode); // Write all BFNT entries recursively.

            writer.Align(128); // Alignment required for proper reading.
            #endregion

            #region FIMG & BFAT
            writer.Write("GMIF",
                BinaryStringFormat.NoPrefixOrTermination); // Magic string.

            long fimgLengthIndex = writer.Position;

            writer.Position += 4; // Length skip (calculated later)

            long bfatIndex = 0x1C; // BFAT current position.
            foreach(byte[] entry in fileContainer) {
                uint currentOffset =
                    (uint) (writer.Position - fimgLengthIndex - 4); // BFAT pair first entry.

                writer.Write(entry); // File contents.

                using(writer.TemporarySeek()) {
                    writer.Position = bfatIndex;
                    writer.Write(currentOffset); // Relative offset of the file to the FIMG section.
                    writer.Write((uint) (currentOffset + entry.Length)); // Relative end of the file to the FIMG section.

                    bfatIndex += 8; // Update bfatIndex.
                }

                writer.Align(128); // Alignment required for proper reading.
            }
            #endregion

            #region Sections' length
            writer.Position = bfatLengthIndex;
            writer.Write((uint) (bfntLengthIndex - bfatLengthIndex)); // BFAT length.

            writer.Position = bfntLengthIndex;
            writer.Write((uint) (fimgLengthIndex - bfntLengthIndex)); // BFNT length.

            writer.Position = fimgLengthIndex;
            writer.Write((uint) (writer.BaseStream.Length - fimgLengthIndex)); // FIMG length.

            writer.Position = 0x08;
            writer.Write((uint) writer.BaseStream.Length); // NARC total length.
            #endregion


            void FolderIterate(Node folderNode) {
                foreach(Node node in folderNode) {
                    if(node.Contents[0] == true) // If it is a "folder" then iterate through it.
                        FolderIterate(node);
                    else                         // If it is a file then add its content to the list.
                        fileContainer.Add(node.Contents[1]);
                }
            }

            void WriteBFNTEntry(Node entry) {
                foreach(Node node in entry) {
                    if(node.Contents[0] == true) { // If it is a "folder".
                        writer.Write((byte) (node.Name.Length + 0x80)); // Name's length.
                        writer.Write(node.Name,
                            BinaryStringFormat.NoPrefixOrTermination);  // Name.
                        writer.Write(folderCount++);                    // Folder id. (count)
                        writer.Write((byte) 0xF0);                      // Constant.

                        WriteBFNTEntry(node);
                    } else {
                        writer.Write(node.Name); // File name.
                    }
                }

                writer.Write((byte) 0x00); // End of "folder".
            }
        }

        public struct NARCHeader {
            public ushort Version;
            public byte[] bfntUnknown;
        }
    }
}