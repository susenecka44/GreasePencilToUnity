using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace GreasePencilToUnity.Editor
{
    /// <summary>
    /// A parsed .gpencil container: the JSON manifest plus typed access to the
    /// binary blob it points into.
    ///
    /// Layout: "GPU3" | u32 version | u32 jsonLen | u32 blobOffset | u32 blobLen,
    /// then the UTF-8 manifest at offset 20 and the little-endian blob at
    /// blobOffset.  Everything in the blob is 4-byte aligned.
    /// </summary>
    public sealed class GpFile
    {
        public const int SupportedVersion = 1;
        private const int HeaderSize = 20;

        private readonly byte[] _bytes;
        private readonly int _blobOffset;
        private readonly int _blobLength;

        public GpManifest Manifest { get; }

        private GpFile(byte[] bytes, int blobOffset, int blobLength, GpManifest manifest)
        {
            _bytes = bytes;
            _blobOffset = blobOffset;
            _blobLength = blobLength;
            Manifest = manifest;
        }

        public static GpFile Load(string path)
        {
            return Parse(File.ReadAllBytes(path), path);
        }

        public static GpFile Parse(byte[] bytes, string origin)
        {
            if (bytes.Length < HeaderSize ||
                bytes[0] != 'G' || bytes[1] != 'P' || bytes[2] != 'U' || bytes[3] != '3')
            {
                throw new InvalidDataException($"{origin} is not a .gpencil file (bad magic).");
            }

            int version = BitConverter.ToInt32(bytes, 4);
            if (version != SupportedVersion)
            {
                throw new InvalidDataException(
                    $"{origin} is .gpencil version {version}; this importer reads version " +
                    $"{SupportedVersion}. Re-export with a matching add-on version.");
            }

            int jsonLength = BitConverter.ToInt32(bytes, 8);
            int blobOffset = BitConverter.ToInt32(bytes, 12);
            int blobLength = BitConverter.ToInt32(bytes, 16);
            if (jsonLength < 0 || blobOffset < HeaderSize || blobLength < 0 ||
                (long)blobOffset + blobLength > bytes.Length)
            {
                throw new InvalidDataException($"{origin} has a truncated or corrupt header.");
            }

            string json = Encoding.UTF8.GetString(bytes, HeaderSize, jsonLength);
            var manifest = JsonUtility.FromJson<GpManifest>(json);
            if (manifest?.layers == null || manifest.drawings == null)
            {
                throw new InvalidDataException($"{origin} has an unreadable manifest.");
            }

            return new GpFile(bytes, blobOffset, blobLength, manifest);
        }

        /// <summary>Copy a float slice out of the blob.</summary>
        public float[] Floats(GpSlice slice)
        {
            var result = new float[Count(slice, 4, "f32")];
            Buffer.BlockCopy(_bytes, _blobOffset + slice.offset, result, 0, result.Length * 4);
            return result;
        }

        /// <summary>Copy an int slice out of the blob.</summary>
        public int[] Ints(GpSlice slice)
        {
            var result = new int[Count(slice, 4, "i32")];
            Buffer.BlockCopy(_bytes, _blobOffset + slice.offset, result, 0, result.Length * 4);
            return result;
        }

        /// <summary>Copy a byte slice out of the blob.</summary>
        public byte[] Bytes(GpSlice slice)
        {
            var result = new byte[Count(slice, 1, "u8")];
            Buffer.BlockCopy(_bytes, _blobOffset + slice.offset, result, 0, result.Length);
            return result;
        }

        private int Count(GpSlice slice, int stride, string expectedType)
        {
            if (slice == null || slice.count == 0)
            {
                return 0;
            }

            if (!string.IsNullOrEmpty(slice.type) && slice.type != expectedType)
            {
                throw new InvalidDataException(
                    $"Slice is {slice.type} but was read as {expectedType}.");
            }

            long end = (long)slice.offset + (long)slice.count * stride;
            if (slice.offset < 0 || end > _blobLength)
            {
                throw new InvalidDataException("Slice runs past the end of the blob.");
            }

            return slice.count;
        }

        /// <summary>Read a row-major 4x4 out of a flat float array.</summary>
        public static Matrix4x4 ToMatrix(float[] values, int start)
        {
            var m = new Matrix4x4();
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    m[row, column] = values[start + row * 4 + column];
                }
            }

            return m;
        }
    }
}
