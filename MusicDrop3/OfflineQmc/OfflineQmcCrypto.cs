/*
MIT License

Copyright (c) 2020-2021 Unlock Music

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

This C# implementation is adapted from the QMC algorithms in
leafxdd/unlock-music, algo/qmc (MIT). It is a clean C# port for MFLAC Drop.
*/

using System.Buffers.Binary;
using System.Text;

namespace MFlacDrop.OfflineQmc;

/// <summary>Decodes QQ Music EKeys into QMC master keys.</summary>
public static class QmcEKey
{
    private const string EncV2Prefix = "QQMusic EncV2,Key:";
    private const int MaxEncodedEKeyLength = 65_535;
    private const int MaxMasterKeyLength = 65_535;

    private static readonly byte[] V2Key1 =
    {
        0x33, 0x38, 0x36, 0x5A, 0x4A, 0x59, 0x21, 0x40,
        0x23, 0x2A, 0x24, 0x25, 0x5E, 0x26, 0x29, 0x28
    };

    private static readonly byte[] V2Key2 =
    {
        0x2A, 0x2A, 0x23, 0x21, 0x28, 0x23, 0x24, 0x25,
        0x26, 0x5E, 0x61, 0x31, 0x63, 0x5A, 0x2C, 0x54
    };

    public static byte[] DeriveMasterKey(string ekey)
    {
        ArgumentNullException.ThrowIfNull(ekey);
        if (ekey.Length == 0 || ekey.Length > MaxEncodedEKeyLength)
            throw new QmcDecryptException($"EKey length {ekey.Length} is outside the supported range.");

        byte[] decoded = DecodeBase64(ekey);
        ReadOnlySpan<byte> v2Prefix = Encoding.ASCII.GetBytes(EncV2Prefix);
        if (decoded.AsSpan().StartsWith(v2Prefix))
        {
            byte[] firstLayer = TencentTea.Decrypt(decoded.AsSpan(v2Prefix.Length), V2Key1);
            byte[] secondLayer = TencentTea.Decrypt(firstLayer, V2Key2);
            decoded = DecodeBase64Ascii(secondLayer);
        }

        if (decoded.Length < 16)
            throw new QmcDecryptException("The decoded EKey is too short.");

        byte[] simpleKey = MakeSimpleKey();
        byte[] teaKey = new byte[16];
        for (int i = 0; i < 8; i++)
        {
            teaKey[i * 2] = simpleKey[i];
            teaKey[i * 2 + 1] = decoded[i];
        }

        byte[] tail = TencentTea.Decrypt(decoded.AsSpan(8), teaKey);
        int masterLength = checked(8 + tail.Length);
        if (masterLength > MaxMasterKeyLength)
            throw new QmcDecryptException($"Decoded QMC key is unexpectedly large ({masterLength} bytes).");

        byte[] master = new byte[masterLength];
        decoded.AsSpan(0, 8).CopyTo(master);
        tail.CopyTo(master, 8);
        return master;
    }

    private static byte[] MakeSimpleKey()
    {
        byte[] key = new byte[8];
        for (int i = 0; i < key.Length; i++)
        {
            double value = Math.Abs(Math.Tan(106.0 + i * 0.1)) * 100.0;
            key[i] = checked((byte)(int)value);
        }
        return key;
    }

    private static byte[] DecodeBase64Ascii(ReadOnlySpan<byte> value)
    {
        int nul = value.IndexOf((byte)0);
        if (nul >= 0)
            value = value[..nul];
        if (value.Length == 0 || value.Length > MaxEncodedEKeyLength)
            throw new QmcDecryptException("The inner EncV2 EKey is empty or too large.");

        foreach (byte b in value)
        {
            if (b > 0x7F)
                throw new QmcDecryptException("The inner EncV2 EKey is not ASCII base64 text.");
        }
        return DecodeBase64(Encoding.ASCII.GetString(value));
    }

    private static byte[] DecodeBase64(string value)
    {
        // Go's base64.StdEncoding accepts CR/LF but rejects spaces and other
        // whitespace; retain those semantics rather than relying on the more
        // permissive Convert.FromBase64String behavior.
        var normalized = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '\r' or '\n')
                continue;
            if (!IsBase64Character(c))
                throw new QmcDecryptException("EKey contains a non-base64 character.");
            normalized.Append(c);
        }

        try
        {
            return Convert.FromBase64String(normalized.ToString());
        }
        catch (FormatException ex)
        {
            throw new QmcDecryptException("EKey is not valid padded base64.", null, ex);
        }
    }

    internal static bool LooksLikeBase64(ReadOnlySpan<byte> value)
    {
        if (value.Length < 12 || value.Length % 4 != 0)
            return false;
        bool paddingSeen = false;
        int paddingCount = 0;
        foreach (byte b in value)
        {
            if (b > 0x7F || !IsBase64Character((char)b))
                return false;
            if (b == '=')
            {
                paddingSeen = true;
                paddingCount++;
                if (paddingCount > 2)
                    return false;
            }
            else if (paddingSeen)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsBase64Character(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=';
}

internal interface IQmcCipher
{
    QmcCipherKind Kind { get; }
    void Transform(Span<byte> data, long offset);
}

internal static class QmcCipherFactory
{
    public static IQmcCipher FromMasterKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
            throw new QmcDecryptException("The decoded QMC master key is empty.");
        return key.Length > 300 ? new QmcModifiedRc4Cipher(key) : new QmcMapCipher(key);
    }
}

internal sealed class QmcStaticCipher : IQmcCipher
{
    public static QmcStaticCipher Instance { get; } = new();
    public QmcCipherKind Kind => QmcCipherKind.StaticV1;

    private QmcStaticCipher() { }

    public void Transform(Span<byte> data, long offset)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        for (int i = 0; i < data.Length; i++)
        {
            long position = offset + i;
            if (position > 0x7FFF)
                position %= 0x7FFF;
            int index = (int)((position * position + 27) & 0xFF);
            data[i] ^= StaticBox[index];
        }
    }

    private static readonly byte[] StaticBox =
    {
        0x77, 0x48, 0x32, 0x73, 0xDE, 0xF2, 0xC0, 0xC8, 0x95, 0xEC, 0x30, 0xB2, 0x51, 0xC3, 0xE1, 0xA0,
        0x9E, 0xE6, 0x9D, 0xCF, 0xFA, 0x7F, 0x14, 0xD1, 0xCE, 0xB8, 0xDC, 0xC3, 0x4A, 0x67, 0x93, 0xD6,
        0x28, 0xC2, 0x91, 0x70, 0xCA, 0x8D, 0xA2, 0xA4, 0xF0, 0x08, 0x61, 0x90, 0x7E, 0x6F, 0xA2, 0xE0,
        0xEB, 0xAE, 0x3E, 0xB6, 0x67, 0xC7, 0x92, 0xF4, 0x91, 0xB5, 0xF6, 0x6C, 0x5E, 0x84, 0x40, 0xF7,
        0xF3, 0x1B, 0x02, 0x7F, 0xD5, 0xAB, 0x41, 0x89, 0x28, 0xF4, 0x25, 0xCC, 0x52, 0x11, 0xAD, 0x43,
        0x68, 0xA6, 0x41, 0x8B, 0x84, 0xB5, 0xFF, 0x2C, 0x92, 0x4A, 0x26, 0xD8, 0x47, 0x6A, 0x7C, 0x95,
        0x61, 0xCC, 0xE6, 0xCB, 0xBB, 0x3F, 0x47, 0x58, 0x89, 0x75, 0xC3, 0x75, 0xA1, 0xD9, 0xAF, 0xCC,
        0x08, 0x73, 0x17, 0xDC, 0xAA, 0x9A, 0xA2, 0x16, 0x41, 0xD8, 0xA2, 0x06, 0xC6, 0x8B, 0xFC, 0x66,
        0x34, 0x9F, 0xCF, 0x18, 0x23, 0xA0, 0x0A, 0x74, 0xE7, 0x2B, 0x27, 0x70, 0x92, 0xE9, 0xAF, 0x37,
        0xE6, 0x8C, 0xA7, 0xBC, 0x62, 0x65, 0x9C, 0xC2, 0x08, 0xC9, 0x88, 0xB3, 0xF3, 0x43, 0xAC, 0x74,
        0x2C, 0x0F, 0xD4, 0xAF, 0xA1, 0xC3, 0x01, 0x64, 0x95, 0x4E, 0x48, 0x9F, 0xF4, 0x35, 0x78, 0x95,
        0x7A, 0x39, 0xD6, 0x6A, 0xA0, 0x6D, 0x40, 0xE8, 0x4F, 0xA8, 0xEF, 0x11, 0x1D, 0xF3, 0x1B, 0x3F,
        0x3F, 0x07, 0xDD, 0x6F, 0x5B, 0x19, 0x30, 0x19, 0xFB, 0xEF, 0x0E, 0x37, 0xF0, 0x0E, 0xCD, 0x16,
        0x49, 0xFE, 0x53, 0x47, 0x13, 0x1A, 0xBD, 0xA4, 0xF1, 0x40, 0x19, 0x60, 0x0E, 0xED, 0x68, 0x09,
        0x06, 0x5F, 0x4D, 0xCF, 0x3D, 0x1A, 0xFE, 0x20, 0x77, 0xE4, 0xD9, 0xDA, 0xF9, 0xA4, 0x2B, 0x76,
        0x1C, 0x71, 0xDB, 0x00, 0xBC, 0xFD, 0x0C, 0x6C, 0xA5, 0x47, 0xF7, 0xF6, 0x00, 0x79, 0x4A, 0x11
    };
}

internal sealed class QmcMapCipher : IQmcCipher
{
    private readonly byte[] _key;

    public QmcMapCipher(byte[] key)
    {
        if (key.Length == 0)
            throw new QmcDecryptException("Map cipher key is empty.");
        _key = (byte[])key.Clone();
    }

    public QmcCipherKind Kind => QmcCipherKind.Map;

    public void Transform(Span<byte> data, long offset)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        for (int i = 0; i < data.Length; i++)
        {
            long position = offset + i;
            if (position > 0x7FFF)
                position %= 0x7FFF;
            int keyIndex = (int)((position * position + 71_214) % _key.Length);
            int rotation = ((keyIndex & 7) + 4) % 8;
            int value = _key[keyIndex];
            // This intentionally mirrors QMC's unusual shift/or operation; it
            // is not a conventional 8-bit rotate-right complement.
            byte mask = (byte)((value << rotation) | (value >> rotation));
            data[i] ^= mask;
        }
    }
}

internal sealed class QmcModifiedRc4Cipher : IQmcCipher
{
    private const int FirstSegmentSize = 128;
    private const int SegmentSize = 5_120;

    private readonly byte[] _key;
    private readonly byte[] _initialBox;
    private readonly uint _hash;

    public QmcModifiedRc4Cipher(byte[] key)
    {
        if (key.Length == 0)
            throw new QmcDecryptException("Modified RC4 key is empty.");
        _key = (byte[])key.Clone();
        _initialBox = new byte[_key.Length];
        for (int i = 0; i < _initialBox.Length; i++)
            _initialBox[i] = unchecked((byte)i);

        int swap = 0;
        for (int i = 0; i < _initialBox.Length; i++)
        {
            swap = (swap + _initialBox[i] + _key[i % _key.Length]) % _initialBox.Length;
            (_initialBox[i], _initialBox[swap]) = (_initialBox[swap], _initialBox[i]);
        }

        uint hash = 1;
        foreach (byte value in _key)
        {
            if (value == 0)
                continue;
            uint next = unchecked(hash * value);
            if (next == 0 || next <= hash)
                break;
            hash = next;
        }
        _hash = hash;
    }

    public QmcCipherKind Kind => QmcCipherKind.ModifiedRc4;

    public void Transform(Span<byte> data, long offset)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        int remaining = data.Length;
        int processed = 0;
        if (offset < FirstSegmentSize)
        {
            int count = Math.Min(remaining, FirstSegmentSize - (int)offset);
            TransformFirst(data[..count], offset);
            processed += count;
            remaining -= count;
            offset += count;
        }

        long segmentOffset = offset % SegmentSize;
        if (remaining > 0 && segmentOffset != 0)
        {
            int count = Math.Min(remaining, SegmentSize - (int)segmentOffset);
            TransformSegment(data.Slice(processed, count), offset);
            processed += count;
            remaining -= count;
            offset += count;
        }

        while (remaining > SegmentSize)
        {
            TransformSegment(data.Slice(processed, SegmentSize), offset);
            processed += SegmentSize;
            remaining -= SegmentSize;
            offset += SegmentSize;
        }

        if (remaining > 0)
            TransformSegment(data.Slice(processed, remaining), offset);
    }

    private void TransformFirst(Span<byte> data, long offset)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] ^= _key[GetSegmentSkip(offset + i)];
    }

    private void TransformSegment(Span<byte> data, long offset)
    {
        byte[] box = (byte[])_initialBox.Clone();
        int j = 0;
        int k = 0;
        int skip = checked((int)(offset % SegmentSize) + GetSegmentSkip(offset / SegmentSize));

        for (int cursor = -skip; cursor < data.Length; cursor++)
        {
            j = (j + 1) % box.Length;
            k = (box[j] + k) % box.Length;
            (box[j], box[k]) = (box[k], box[j]);
            if (cursor >= 0)
                data[cursor] ^= box[(box[j] + box[k]) % box.Length];
        }
    }

    private int GetSegmentSkip(long id)
    {
        int seed = _key[(int)(id % _key.Length)];
        if (seed == 0)
            return 0;
        double value = (double)_hash / ((id + 1.0) * seed) * 100.0;
        long index = (long)value;
        int skip = (int)(index % _key.Length);
        return skip < 0 ? skip + _key.Length : skip;
    }
}

internal static class TencentTea
{
    private const uint Delta = 0x9E3779B9;
    private const int Cycles = 16; // x/crypto/tea NewCipherWithRounds(key, 32)

    public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key)
    {
        if (key.Length != 16)
            throw new QmcDecryptException("Tencent TEA requires a 16-byte key.");
        if (ciphertext.Length < 16 || ciphertext.Length % 8 != 0)
            throw new QmcDecryptException("Tencent TEA ciphertext must be at least 16 bytes and block aligned.");

        byte[] plain = new byte[ciphertext.Length];
        Span<byte> previousCipher = stackalloc byte[8];
        Span<byte> previousInner = stackalloc byte[8];
        Span<byte> mixed = stackalloc byte[8];
        Span<byte> inner = stackalloc byte[8];

        for (int offset = 0; offset < ciphertext.Length; offset += 8)
        {
            ReadOnlySpan<byte> cipherBlock = ciphertext.Slice(offset, 8);
            for (int i = 0; i < 8; i++)
                mixed[i] = (byte)(cipherBlock[i] ^ previousInner[i]);
            DecryptBlock(mixed, inner, key);
            for (int i = 0; i < 8; i++)
                plain[offset + i] = (byte)(inner[i] ^ previousCipher[i]);
            cipherBlock.CopyTo(previousCipher);
            inner.CopyTo(previousInner);
        }

        int paddingLength = plain[0] & 7;
        int payloadStart = 1 + paddingLength + 2;
        int payloadEnd = plain.Length - 7;
        if (payloadStart > payloadEnd)
            throw new QmcDecryptException("Tencent TEA padding produces an invalid payload length.");
        for (int i = payloadEnd; i < plain.Length; i++)
        {
            if (plain[i] != 0)
                throw new QmcDecryptException("Tencent TEA zero-padding validation failed.");
        }

        return plain.AsSpan(payloadStart, payloadEnd - payloadStart).ToArray();
    }

    private static void DecryptBlock(ReadOnlySpan<byte> input, Span<byte> output, ReadOnlySpan<byte> key)
    {
        uint y = BinaryPrimitives.ReadUInt32BigEndian(input);
        uint z = BinaryPrimitives.ReadUInt32BigEndian(input[4..]);
        uint k0 = BinaryPrimitives.ReadUInt32BigEndian(key);
        uint k1 = BinaryPrimitives.ReadUInt32BigEndian(key[4..]);
        uint k2 = BinaryPrimitives.ReadUInt32BigEndian(key[8..]);
        uint k3 = BinaryPrimitives.ReadUInt32BigEndian(key[12..]);
        uint sum = unchecked(Delta * Cycles);

        unchecked
        {
            for (int i = 0; i < Cycles; i++)
            {
                z -= ((y << 4) + k2) ^ (sum + y) ^ ((y >> 5) + k3);
                y -= ((z << 4) + k0) ^ (sum + z) ^ ((z >> 5) + k1);
                sum -= Delta;
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(output, y);
        BinaryPrimitives.WriteUInt32BigEndian(output[4..], z);
    }
}
