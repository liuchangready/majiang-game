using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mahjong.Infrastructure
{
    /// <summary>
    /// AES-256-GCM envelope for all durable table-side payloads. The AAD binds the
    /// ciphertext to schema/table/room/version/purpose, so a payload copied to a
    /// different row fails closed rather than being accepted as another state.
    ///
    /// Envelope: magic "MJP1" (4) | nonce (12) | tag (16) | ciphertext.
    /// </summary>
    internal sealed class PostgresPayloadProtector : IDisposable
    {
        private const int Magic = 0x4D4A5031; // MJP1
        private const int NonceBytes = 12;
        private const int TagBytes = 16;
        private const int HeaderBytes = 4 + NonceBytes + TagBytes;

        private readonly object _gate = new object();
        private readonly AesGcm _aead;
        private bool _disposed;

        internal PostgresPayloadProtector(byte[] key)
        {
            if (key == null || key.Length != 32)
            {
                throw new ArgumentException("AES-256-GCM requires a 32-byte key.", nameof(key));
            }

            _aead = new AesGcm((byte[])key.Clone(), TagBytes);
        }

        internal byte[] Seal(ReadOnlySpan<byte> plaintext, string aad)
        {
            ThrowIfDisposed();
            byte[] associated = Encoding.UTF8.GetBytes(aad ?? string.Empty);
            var nonce = new byte[NonceBytes];
            RandomNumberGenerator.Fill(nonce);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagBytes];
            lock (_gate)
            {
                _aead.Encrypt(nonce, plaintext, ciphertext, tag, associated);
            }

            var envelope = new byte[HeaderBytes + ciphertext.Length];
            BinaryPrimitives.WriteInt32BigEndian(envelope.AsSpan(0, 4), Magic);
            Buffer.BlockCopy(nonce, 0, envelope, 4, NonceBytes);
            Buffer.BlockCopy(tag, 0, envelope, 4 + NonceBytes, TagBytes);
            Buffer.BlockCopy(ciphertext, 0, envelope, HeaderBytes, ciphertext.Length);
            return envelope;
        }

        internal byte[] Open(ReadOnlySpan<byte> envelope, string aad)
        {
            ThrowIfDisposed();
            if (envelope.Length < HeaderBytes)
            {
                throw new CryptographicException("Sealed payload is truncated.");
            }

            if (BinaryPrimitives.ReadInt32BigEndian(envelope.Slice(0, 4)) != Magic)
            {
                throw new CryptographicException("Sealed payload magic is invalid.");
            }

            ReadOnlySpan<byte> nonce = envelope.Slice(4, NonceBytes);
            ReadOnlySpan<byte> tag = envelope.Slice(4 + NonceBytes, TagBytes);
            ReadOnlySpan<byte> ciphertext = envelope.Slice(HeaderBytes);
            var plaintext = new byte[ciphertext.Length];
            byte[] associated = Encoding.UTF8.GetBytes(aad ?? string.Empty);
            lock (_gate)
            {
                _aead.Decrypt(nonce, ciphertext, tag, plaintext, associated);
            }

            return plaintext;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PostgresPayloadProtector));
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _aead.Dispose();
            }
        }
    }
}
